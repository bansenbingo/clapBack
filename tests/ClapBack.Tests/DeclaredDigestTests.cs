using System.Net;
using System.Security.Cryptography;
using ClapBack.Core.Models;
using ClapBack.Stt;
using Xunit;

namespace ClapBack.Tests;

/// <summary>
/// 回归：Hugging Face 把 LFS 文件放在 Xet 存储后端之后，跟随 302 拿到的最终响应里
/// <c>X-Linked-Etag</c> 会消失，只剩一个 64 位十六进制的 Xet <c>ETag</c>——它不是文件的 SHA-256。
/// 旧实现把它当摘要用，导致每一次合法下载都被判成「声明值不一致」而丢弃。
///
/// 同时守住主从关系：内置锁定哈希（信任根）已经命中时，源端声明值不能反过来否决它。
/// </summary>
public class DeclaredDigestTests
{
    private static readonly byte[] Payload = { 1, 2, 3, 4, 5 };

    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // ------------------------------------------------------------ 摘要头解析

    [Fact]
    public void PlainEtagWithoutPrefix_IsIgnored()
    {
        // Xet 后端的 ETag：64 位十六进制，但不是文件 SHA-256。
        var xetEtag = new string('4', 64);

        Assert.Null(ModelIntegrity.TryParseDeclaredSha256(null, xetEtag));
    }

    [Fact]
    public void LinkedEtagWithBareHex_IsAccepted()
    {
        // ModelScope 的约定：X-Linked-Etag 是裸十六进制，且确实是文件 SHA-256。
        // NormalizeHex 会把结果规范成大写，断言也要跟着大写。
        var bare = new string('a', 64);

        Assert.Equal(new string('A', 64), ModelIntegrity.TryParseDeclaredSha256(bare, null));
    }

    [Fact]
    public void LinkedEtagWithSha256Prefix_IsAccepted()
    {
        var hex = new string('b', 64);

        Assert.Equal(new string('B', 64), ModelIntegrity.TryParseDeclaredSha256($"\"sha256:{hex}\"", null));
    }

    [Fact]
    public void PlainEtagWithSha256Prefix_IsAccepted()
    {
        var hex = new string('c', 64);

        Assert.Equal(new string('C', 64), ModelIntegrity.TryParseDeclaredSha256(null, $"sha256:{hex}"));
    }

    // ------------------------------------------------------------ 端到端

    [Fact]
    public async Task PinnedDigestWins_EvenWhenSourceDeclaresSomethingElse()
    {
        var payloadSha = Sha256Of(Payload);
        var downloader = new HuggingFaceModelDownloader(new HttpClient(new StubHandler(
            (request, response) =>
            {
                response.Content = new ByteArrayContent(Payload);
                // 故意声明一个不相干的摘要——这就是线上真实发生的情况。
                response.Headers.TryAddWithoutValidation("ETag", new string('4', 64));
                response.Headers.TryAddWithoutValidation("X-Linked-Etag", new string('7', 64));
            })));

        var destination = Path.Combine(Path.GetTempPath(), "clapback-digest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await downloader.EnsureFileAsync(
                destinationPath: destination,
                huggingFaceRepository: "owner/repo",
                huggingFaceRevision: "main",
                huggingFaceFile: "model.gguf",
                modelScopeRepository: "owner/repo",
                modelScopeRevision: "master",
                modelScopeFile: "model.gguf",
                huggingFaceTimeoutSeconds: 20,
                source: ModelDownloadSource.HuggingFaceOnly,
                expectedSha256: payloadSha,
                requireIntegrity: true);

            Assert.Equal(payloadSha, result.Sha256Hex);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            File.Delete(destination);
            File.Delete(destination + ".sha256");
        }
    }

    [Fact]
    public async Task PinnedDigestMismatch_IsStillRejected()
    {
        var downloader = new HuggingFaceModelDownloader(new HttpClient(new StubHandler(
            (_, response) => response.Content = new ByteArrayContent(Payload))));

        var destination = Path.Combine(Path.GetTempPath(), "clapback-digest-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => downloader.EnsureFileAsync(
                    destinationPath: destination,
                    huggingFaceRepository: "owner/repo",
                    huggingFaceRevision: "main",
                    huggingFaceFile: "model.gguf",
                    modelScopeRepository: "owner/repo",
                    modelScopeRevision: "master",
                    modelScopeFile: "model.gguf",
                    huggingFaceTimeoutSeconds: 20,
                    source: ModelDownloadSource.HuggingFaceOnly,
                    expectedSha256: new string('d', 64),
                    requireIntegrity: true));
        }
        finally
        {
            File.Delete(destination);
            File.Delete(destination + ".sha256");
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Action<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Action<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            _respond(request, response);
            return Task.FromResult(response);
        }
    }
}

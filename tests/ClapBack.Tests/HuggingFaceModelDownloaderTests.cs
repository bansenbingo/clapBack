using System.Net;
using System.Net.Http;
using ClapBack.Stt;
using Xunit;

namespace ClapBack.Tests;

public sealed class HuggingFaceModelDownloaderTests
{
    [Fact]
    public async Task DownloadsConfiguredFileToLocalCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clapback-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Uri? requestedUri = null;
            using var httpClient = new HttpClient(new StubHandler(request =>
            {
                requestedUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
                };
            }));
            var downloader = new HuggingFaceModelDownloader(httpClient);
            var destination = Path.Combine(root, "models", "sensevoice-small-q8.gguf");

            await downloader.EnsureFileAsync(
                destination,
                "FunAudioLLM/SenseVoiceSmall-GGUF",
                "main",
                "sensevoice-small-q8.gguf");

            // 默认端点是 hf-mirror.com：huggingface.co 在不少网络被 DNS 劫持到无关 IP，直连必然超时。
            Assert.Equal(
                "https://hf-mirror.com/FunAudioLLM/SenseVoiceSmall-GGUF/resolve/main/sensevoice-small-q8.gguf?download=true",
                requestedUri?.ToString());
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FallsBackToModelScopeWhenHuggingFaceTimesOut()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clapback-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var requests = new List<Uri>();
            using var httpClient = new HttpClient(new StubHandler(request =>
            {
                requests.Add(request.RequestUri!);
                if (!request.RequestUri!.Host.Contains("modelscope", StringComparison.OrdinalIgnoreCase))
                {
                    // Hugging Face 兼容端点（默认 hf-mirror.com）超时，应当回退 ModelScope。
                    throw new TaskCanceledException("timeout");
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 4, 5, 6 })
                };
            }));
            var downloader = new HuggingFaceModelDownloader(httpClient);
            var destination = Path.Combine(root, "model.gguf");

            var result = await downloader.EnsureFileAsync(
                destination,
                "owner/hf-model",
                "main",
                "model.gguf",
                "owner/cn-model",
                "master",
                "model.gguf",
                1);

            Assert.True(result.UsedModelScope);
            Assert.Equal(2, requests.Count);
            Assert.Contains("hf-mirror.com", requests[0].Host);
            Assert.Contains("modelscope.cn", requests[1].Host);
            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExplicitHuggingFaceEndpointOverridesTheDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clapback-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Uri? requestedUri = null;
            using var httpClient = new HttpClient(new StubHandler(request =>
            {
                requestedUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 7 })
                };
            }));
            var downloader = new HuggingFaceModelDownloader(httpClient);
            var destination = Path.Combine(root, "model.gguf");

            await downloader.EnsureFileAsync(
                destination,
                "owner/repo",
                "main",
                "model.gguf",
                "owner/repo",
                "master",
                "model.gguf",
                huggingFaceTimeoutSeconds: 5,
                source: ClapBack.Core.Models.ModelDownloadSource.HuggingFaceFirst,
                expectedSha256: null,
                requireIntegrity: false,
                huggingFaceEndpoint: "https://mirror.example.com/",
                modelScopeEndpoint: null);

            Assert.Equal(
                "https://mirror.example.com/owner/repo/resolve/main/model.gguf?download=true",
                requestedUri?.ToString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("http://mirror.example.com/")]
    [InlineData("ftp://mirror.example.com/")]
    [InlineData("file:///tmp/")]
    [InlineData("not a url")]
    public void InsecureDownloadEndpointIsRejected(string endpoint)
    {
        Assert.Throws<ArgumentException>(() =>
            HuggingFaceModelDownloader.BuildResolveUri(endpoint, "owner/repo", "main", "model.gguf"));
    }

    [Theory]
    [InlineData("https://huggingface.co")]
    [InlineData("https://hf-mirror.com/")]
    public void HttpsDownloadEndpointIsAccepted(string endpoint)
    {
        var uri = HuggingFaceModelDownloader.BuildResolveUri(endpoint, "owner/repo", "main", "model.gguf");
        Assert.Equal("https", uri.Scheme);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}

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

            Assert.Equal(
                "https://huggingface.co/FunAudioLLM/SenseVoiceSmall-GGUF/resolve/main/sensevoice-small-q8.gguf?download=true",
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
                if (request.RequestUri!.Host.Contains("huggingface", StringComparison.OrdinalIgnoreCase))
                {
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
            Assert.Contains("huggingface.co", requests[0].Host);
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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}

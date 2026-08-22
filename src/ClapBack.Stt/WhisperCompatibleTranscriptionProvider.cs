using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;

namespace ClapBack.Stt;

public sealed class WhisperCompatibleTranscriptionProvider : ITranscriptionProvider
{
    private readonly HttpClient _httpClient;
    private readonly Func<ClapBackSettings> _settings;
    private readonly Func<CancellationToken, Task<string?>> _apiKeyProvider;

    public WhisperCompatibleTranscriptionProvider(
        HttpClient httpClient,
        Func<ClapBackSettings> settings,
        Func<CancellationToken, Task<string?>> apiKeyProvider)
    {
        _httpClient = httpClient;
        _settings = settings;
        _apiKeyProvider = apiKeyProvider;
    }

    public async Task<TranscriptResult> TranscribeAsync(
        Stream audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!audio.CanRead)
        {
            throw new ArgumentException("音频流不可读", nameof(audio));
        }

        var settings = _settings();
        var apiKey = await _apiKeyProvider(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("尚未配置语音识别 API Key");
        }

        var audioBytes = await ReadAudioAsync(audio, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                BuildEndpoint(settings.SttBaseUrl));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var multipart = new MultipartFormDataContent();
            using var audioContent = new ByteArrayContent(audioBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            multipart.Add(audioContent, "file", "clapback.wav");
            multipart.Add(new StringContent(options.Model, Encoding.UTF8), "model");
            if (!string.IsNullOrWhiteSpace(options.Language))
            {
                multipart.Add(new StringContent(options.Language, Encoding.UTF8), "language");
            }

            request.Content = multipart;
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return ParseResult(responseBody);
            }

            if (attempt == 0 && IsTransient(response.StatusCode))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new HttpRequestException($"识别服务返回 {(int)response.StatusCode}：{Sanitize(responseBody)}");
        }

        throw new InvalidOperationException("识别服务请求未完成");
    }

    private static async Task<byte[]> ReadAudioAsync(Stream audio, CancellationToken cancellationToken)
    {
        if (audio is MemoryStream memoryStream && memoryStream.TryGetBuffer(out var segment))
        {
            return segment.ToArray();
        }

        using var copy = new MemoryStream();
        await audio.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }

    private static TranscriptResult ParseResult(string responseBody)
    {
        WhisperResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<WhisperResponse>(responseBody);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("识别服务返回了无法解析的结果", ex);
        }

        return new TranscriptResult(parsed?.Text?.Trim() ?? string.Empty);
    }

    private static bool IsTransient(System.Net.HttpStatusCode statusCode) =>
        statusCode == System.Net.HttpStatusCode.RequestTimeout
        || statusCode == System.Net.HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    private static Uri BuildEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/audio/transcriptions", UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException("语音识别服务 URL 无效");
        }

        return endpoint;
    }

    private static string Sanitize(string value)
    {
        var compact = value.Replace('\n', ' ').Replace('\r', ' ');
        return compact.Length <= 300 ? compact : compact[..300] + "…";
    }

    private sealed record WhisperResponse(string? Text);
}

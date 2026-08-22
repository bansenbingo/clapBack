using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;

namespace ClapBack.Stt;

/// <summary>
/// Selects local/cloud recognition without exposing provider details to the core controller.
/// LocalFirst is intentionally explicit: it may send audio to the cloud only after local
/// recognition fails, while LocalOnly never uploads audio.
/// </summary>
public sealed class CompositeTranscriptionProvider : ITranscriptionProvider
{
    private readonly ITranscriptionProvider _local;
    private readonly ITranscriptionProvider _cloud;
    private readonly Func<ClapBackSettings> _settings;

    public CompositeTranscriptionProvider(
        ITranscriptionProvider local,
        ITranscriptionProvider cloud,
        Func<ClapBackSettings> settings)
    {
        _local = local;
        _cloud = cloud;
        _settings = settings;
    }

    public async Task<TranscriptResult> TranscribeAsync(
        Stream audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        var mode = _settings().TranscriptionMode;
        if (mode == TranscriptionMode.LocalOnly)
        {
            return await _local.TranscribeAsync(audio, options, cancellationToken).ConfigureAwait(false);
        }

        if (mode == TranscriptionMode.CloudOnly)
        {
            return await _cloud.TranscribeAsync(audio, options, cancellationToken).ConfigureAwait(false);
        }

        await using var bufferedAudio = new MemoryStream();
        if (audio.CanSeek)
        {
            audio.Position = 0;
        }

        await audio.CopyToAsync(bufferedAudio, cancellationToken).ConfigureAwait(false);
        var bytes = bufferedAudio.ToArray();
        Exception? localError = null;
        try
        {
            await using var localAudio = new MemoryStream(bytes, writable: false);
            var localResult = await _local.TranscribeAsync(localAudio, options, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(localResult.Text))
            {
                return localResult;
            }

            localError = new InvalidOperationException("本地识别返回空结果");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            localError = ex;
        }

        try
        {
            await using var cloudAudio = new MemoryStream(bytes, writable: false);
            return await _cloud.TranscribeAsync(cloudAudio, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception cloudError)
        {
            throw new InvalidOperationException(
                $"本地识别失败：{localError?.Message ?? "未知错误"}；云端回退也失败：{cloudError.Message}",
                cloudError);
        }
    }
}

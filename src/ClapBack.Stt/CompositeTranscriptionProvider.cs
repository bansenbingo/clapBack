using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;

namespace ClapBack.Stt;

/// <summary>
/// Selects local/cloud recognition without exposing provider details to the core controller.
///
/// 语义严格按 <c>TranscriptionMode</c> 走 fail-closed：
/// <list type="bullet">
/// <item><see cref="TranscriptionMode.LocalOnly"/>——只走本地，任何情况下都不上传音频。</item>
/// <item><see cref="TranscriptionMode.CloudOnly"/>——只走云端（这是用户显式选择的上传路径）。</item>
/// <item><see cref="TranscriptionMode.LocalFirst"/>——本地失败才回退云端；本地「返回空文本」默认
/// <b>不算失败</b>（那代表没听清/没人说话，而不是本地引擎坏了），除非打开
/// <c>FallbackOnEmptyResult</c>。回退前会通过 <c>onCloudUpload</c> 回调告知调用方。</item>
/// <item>未知/非法枚举值——一律按 <see cref="TranscriptionMode.LocalOnly"/> 处理，
/// 绝不因为配置损坏而静默上传音频。</item>
/// </list>
/// </summary>
public sealed class CompositeTranscriptionProvider : ITranscriptionProvider
{
    private readonly ITranscriptionProvider _local;
    private readonly ITranscriptionProvider _cloud;
    private readonly Func<ClapBackSettings> _settings;
    private readonly Action<string>? _onCloudUpload;

    public CompositeTranscriptionProvider(
        ITranscriptionProvider local,
        ITranscriptionProvider cloud,
        Func<ClapBackSettings> settings,
        Action<string>? onCloudUpload = null)
    {
        _local = local;
        _cloud = cloud;
        _settings = settings;
        _onCloudUpload = onCloudUpload;
    }

    public async Task<TranscriptResult> TranscribeAsync(
        Stream audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        var settings = _settings();

        // 未知枚举值 fail-closed 到「仅本地」，见类型注释。
        var mode = settings.TranscriptionMode switch
        {
            TranscriptionMode.CloudOnly => TranscriptionMode.CloudOnly,
            TranscriptionMode.LocalFirst => TranscriptionMode.LocalFirst,
            _ => TranscriptionMode.LocalOnly
        };

        if (mode == TranscriptionMode.LocalOnly)
        {
            return await _local.TranscribeAsync(audio, options, cancellationToken).ConfigureAwait(false);
        }

        if (mode == TranscriptionMode.CloudOnly)
        {
            _onCloudUpload?.Invoke("当前模式为 CloudOnly，音频将直接上传云端。");
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
        var localWasSilent = false;

        try
        {
            await using var localAudio = new MemoryStream(bytes, writable: false);
            var localResult = await _local.TranscribeAsync(localAudio, options, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(localResult.Text))
            {
                return localResult;
            }

            // 空文本是「识别到安静」，不是「识别失败」。默认不因此把整段音频外发。
            localWasSilent = true;
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

        if (localWasSilent && !settings.FallbackOnEmptyResult)
        {
            return new TranscriptResult(string.Empty, null);
        }

        _onCloudUpload?.Invoke(localWasSilent
            ? "本地识别返回空结果，已按 FallbackOnEmptyResult 设置回退云端，音频将上传。"
            : $"本地识别失败：{Describe(localError)}。已按 LocalFirst 设置回退云端，音频将上传。");

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
                $"本地识别失败：{Describe(localError)}；云端回退也失败：{Describe(cloudError)}",
                cloudError);
        }
    }

    // 只透出异常类型与截断消息，避免把用户本地路径等隐私信息原样写进日志/通知。
    private static string Describe(Exception? error) =>
        error is null ? "未知错误" : $"{error.GetType().Name}: {Truncate(error.Message, 200)}";

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}

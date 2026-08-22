using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;

namespace ClapBack.Core.Controller;

public sealed class VoiceInputController
{
    private readonly IAudioRecorder _recorder;
    private readonly ITranscriptionProvider _transcriptionProvider;
    private readonly ITextSender _textSender;
    private readonly Func<ClapBackSettings> _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _recordingTimeout;

    public VoiceInputController(
        IAudioRecorder recorder,
        ITranscriptionProvider transcriptionProvider,
        ITextSender textSender,
        Func<ClapBackSettings> settings)
    {
        _recorder = recorder;
        _transcriptionProvider = transcriptionProvider;
        _textSender = textSender;
        _settings = settings;
    }

    public ClapBackState State { get; private set; } = ClapBackState.Idle;
    public string? LastTranscript { get; private set; }
    public string? LastError { get; private set; }

    public event EventHandler<ClapBackState>? StateChanged;
    public event EventHandler<string>? NotificationRaised;

    public async Task HandleHotkeyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == ClapBackState.Idle || State == ClapBackState.Success || State == ClapBackState.Error)
            {
                await StartRecordingAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (State == ClapBackState.Recording)
            {
                await StopTranscribeAndSendAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                NotificationRaised?.Invoke(this, "正在处理上一条语音，请稍候");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartRecordingAsync(CancellationToken cancellationToken)
    {
        LastError = null;
        LastTranscript = null;
        if (_textSender is ITargetWindowAware targetWindowAware)
        {
            targetWindowAware.CaptureCurrentTarget();
        }

        try
        {
            await _recorder.StartAsync(cancellationToken).ConfigureAwait(false);
            SetState(ClapBackState.Recording);
            NotificationRaised?.Invoke(this, "开始录音，再按一次热键发送");

            _recordingTimeout?.Cancel();
            _recordingTimeout = new CancellationTokenSource();
            _ = StopWhenRecordingExpiresAsync(_recordingTimeout.Token);
        }
        catch (Exception ex)
        {
            await ReportErrorAsync("无法开始录音", ex).ConfigureAwait(false);
        }
    }

    private async Task StopTranscribeAndSendAsync(CancellationToken cancellationToken)
    {
        _recordingTimeout?.Cancel();
        SetState(ClapBackState.Transcribing);

        try
        {
            using var audio = await _recorder.StopAsync(cancellationToken).ConfigureAwait(false);
            var settings = _settings();
            var result = await _transcriptionProvider.TranscribeAsync(
                audio.Content,
                new TranscriptionOptions(settings.Model, settings.Language),
                cancellationToken).ConfigureAwait(false);

            var text = result.Text.Trim();
            if (text.Length == 0)
            {
                throw new InvalidOperationException("没有识别到有效语音");
            }

            LastTranscript = text;
            if (!settings.AutoSend)
            {
                SetState(ClapBackState.Success);
                NotificationRaised?.Invoke(this, $"识别完成：{text}");
                return;
            }

            SetState(ClapBackState.Sending);
            await _textSender.SendAsync(text, cancellationToken).ConfigureAwait(false);
            SetState(ClapBackState.Success);
            NotificationRaised?.Invoke(this, "语音文字已发送");
        }
        catch (OperationCanceledException)
        {
            try
            {
                await _recorder.CancelAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Cancellation cleanup must not hide the original cancellation.
            }

            SetState(ClapBackState.Idle);
            NotificationRaised?.Invoke(this, "已取消本次语音");
        }
        catch (Exception ex)
        {
            await ReportErrorAsync("语音处理失败", ex).ConfigureAwait(false);
        }
    }

    private async Task StopWhenRecordingExpiresAsync(CancellationToken cancellationToken)
    {
        var seconds = Math.Clamp(_settings().MaxRecordingSeconds, 1, 300);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            await HandleHotkeyAsync(CancellationToken.None).ConfigureAwait(false);
            NotificationRaised?.Invoke(this, $"已达到最长录音时长（{seconds} 秒）");
        }
        catch (OperationCanceledException)
        {
            // Normal path: manual stop cancels the timeout task.
        }
    }

    private async Task ReportErrorAsync(string prefix, Exception exception)
    {
        LastError = exception.Message;
        SetState(ClapBackState.Error);
        NotificationRaised?.Invoke(this, $"{prefix}：{exception.Message}");
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        SetState(ClapBackState.Idle);
    }

    private void SetState(ClapBackState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }
}

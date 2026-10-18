using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;
using ClapBack.Core.Services;

namespace ClapBack.Core.Controller;

public sealed class VoiceInputController
{
    private readonly IAudioRecorder _recorder;
    private readonly ITranscriptionProvider _transcriptionProvider;
    private readonly ITextSender _textSender;
    private readonly IClipboardService _clipboard;
    private readonly Func<ClapBackSettings> _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _recordingTimeout;
    private CancellationTokenSource? _sessionCancellation;
    private ClapBackSettings? _recordingSettings;

    public VoiceInputController(
        IAudioRecorder recorder,
        ITranscriptionProvider transcriptionProvider,
        ITextSender textSender,
        IClipboardService clipboard,
        Func<ClapBackSettings> settings)
    {
        _recorder = recorder;
        _transcriptionProvider = transcriptionProvider;
        _textSender = textSender;
        _clipboard = clipboard;
        _settings = settings;
    }

    public ClapBackState State { get; private set; } = ClapBackState.Idle;
    public string? LastTranscript { get; private set; }
    public string? LastError { get; private set; }

    public event EventHandler<ClapBackState>? StateChanged;
    public event EventHandler<string>? NotificationRaised;

    public async Task HandleHotkeyAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            NotificationRaised?.Invoke(this, "正在处理上一条语音，请稍候");
            return;
        }

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

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        _sessionCancellation?.Cancel();
        _recordingTimeout?.Cancel();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recorder.IsRecording)
                await _recorder.CancelAsync(cancellationToken).ConfigureAwait(false);
            SetState(ClapBackState.Idle);
            NotificationRaised?.Invoke(this, "已取消本次语音");
        }
        finally { _gate.Release(); }
    }

    private async Task StartRecordingAsync(CancellationToken cancellationToken)
    {
        LastError = null;
        LastTranscript = null;
        _recordingSettings = _settings();
        _sessionCancellation?.Dispose();
        _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_textSender is ITargetWindowAware targetWindowAware)
        {
            targetWindowAware.CaptureCurrentTarget();
        }

        try
        {
            await _recorder.StartAsync(_sessionCancellation.Token).ConfigureAwait(false);
            SetState(ClapBackState.Recording);
            NotificationRaised?.Invoke(this, "开始录音，再按一次结束并识别");

            _recordingTimeout?.Cancel();
            _recordingTimeout = new CancellationTokenSource();
            _ = StopWhenRecordingExpiresAsync(_recordingTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            await _recorder.CancelAsync(CancellationToken.None).ConfigureAwait(false);
            SetState(ClapBackState.Idle);
        }
        catch (Exception ex)
        {
            try { await _recorder.CancelAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Keep the original recording error. */ }
            await ReportErrorAsync("无法开始录音", ex).ConfigureAwait(false);
        }
    }

    private async Task StopTranscribeAndSendAsync(CancellationToken cancellationToken)
    {
        _recordingTimeout?.Cancel();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _sessionCancellation?.Token ?? CancellationToken.None);
        cancellationToken = operation.Token;
        SetState(ClapBackState.Transcribing);

        try
        {
            using var audio = await _recorder.StopAsync(cancellationToken).ConfigureAwait(false);
            var settings = _recordingSettings ?? _settings();
            var result = await _transcriptionProvider.TranscribeAsync(
                audio.Content,
                new TranscriptionOptions(settings.Model, settings.Language),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var text = result.Text.Trim();
            if (text.Length == 0)
            {
                throw new InvalidOperationException("没有识别到有效语音");
            }

            // 敏感字同音替换必须发生在写剪贴板/发送之前，这样玩家按 Ctrl+V 粘出去的
            // 就已经是替换后的文字。字库可在 GUI 的「字库」页里增删改。
            text = HomophoneSubstitution.Apply(settings, text);

            LastTranscript = text;

            // 剪贴板是主要交付通道：识别出来的文字必须留在这里，玩家在游戏里自己按 Ctrl+V。
            // 这一步无条件执行——「不自动发送」不等于「什么都不给」。
            cancellationToken.ThrowIfCancellationRequested();
            await _clipboard.SetTextAsync(text, cancellationToken).ConfigureAwait(false);

            if (!settings.AutoSend)
            {
                SetState(ClapBackState.Success);
                // 默认不把识别原文放进通知气泡：气泡会留在屏幕上，
                // 可能被截图、录屏或旁人看到。需要预览时打开 ShowTranscriptInNotification。
                NotificationRaised?.Invoke(this, settings.ShowTranscriptInNotification
                    ? $"已复制到剪贴板：{text}"
                    : $"已复制到剪贴板（{text.Length} 字），在游戏中按 Ctrl+V 粘贴");
                return;
            }

            SetState(ClapBackState.Sending);
            await _textSender.SendAsync(text, cancellationToken).ConfigureAwait(false);
            SetState(ClapBackState.Success);
            NotificationRaised?.Invoke(this, "已发送，文字也留在剪贴板里");
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
        var seconds = Math.Clamp((_recordingSettings ?? _settings()).MaxRecordingSeconds, 1, 300);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            // Check the session only after acquiring the same gate as manual
            // stop/cancel. An expired callback must never toggle a new recording.
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (cancellationToken.IsCancellationRequested || State != ClapBackState.Recording)
                    return;
                NotificationRaised?.Invoke(this, $"已达到最长录音时长（{seconds} 秒）");
                await StopTranscribeAndSendAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
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

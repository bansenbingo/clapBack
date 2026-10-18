using ClapBack.Core.Abstractions;
using ClapBack.Core.Controller;
using ClapBack.Core.Models;
using ClapBack.Stt;
using Xunit;

namespace ClapBack.Tests;

public sealed class VoiceInputControllerTests
{
    [Fact]
    public async Task HotkeyTogglesRecordingAndSendsTranscript()
    {
        var recorder = new FakeRecorder();
        var provider = new FakeProvider("你好，集合！");
        var sender = new FakeSender();
        var clipboard = new FakeClipboard();
        var controller = new VoiceInputController(
            recorder, provider, sender, clipboard,
            () => new ClapBackSettings { AutoSend = true });

        await controller.HandleHotkeyAsync();
        Assert.Equal(ClapBackState.Recording, controller.State);
        Assert.True(recorder.IsRecording);

        await controller.HandleHotkeyAsync();

        Assert.Equal(ClapBackState.Success, controller.State);
        Assert.Equal("你好，集合！", sender.LastText);
        Assert.False(recorder.IsRecording);
    }

    /// <summary>
    /// 主流程：识别完文字进剪贴板，玩家自己在游戏里按 Ctrl+V。
    /// 默认设置下<b>不</b>该注入任何按键，但剪贴板里必须有字。
    /// </summary>
    [Fact]
    public async Task TranscriptIsCopiedToClipboardAndNotAutoSentByDefault()
    {
        var recorder = new FakeRecorder();
        var provider = new FakeProvider("去B点！");
        var sender = new FakeSender();
        var clipboard = new FakeClipboard();
        var controller = new VoiceInputController(
            recorder, provider, sender, clipboard,
            () => new ClapBackSettings());

        await controller.HandleHotkeyAsync();
        await controller.HandleHotkeyAsync();

        Assert.Equal("去B点！", clipboard.Text);
        Assert.Null(sender.LastText);
        Assert.Equal(ClapBackState.Success, controller.State);
    }

    [Fact]
    public async Task ClipboardIsWrittenEvenWhenAutoSendIsOn()
    {
        var recorder = new FakeRecorder();
        var provider = new FakeProvider("集合");
        var sender = new FakeSender();
        var clipboard = new FakeClipboard();
        var controller = new VoiceInputController(
            recorder, provider, sender, clipboard,
            () => new ClapBackSettings { AutoSend = true });

        await controller.HandleHotkeyAsync();
        await controller.HandleHotkeyAsync();

        // 自动发送也要把文字留在剪贴板：玩家可能想自己再粘一次。
        Assert.Equal("集合", clipboard.Text);
        Assert.Equal("集合", sender.LastText);
    }

    [Fact]
    public async Task EmptyTranscriptDoesNotSend()
    {
        var recorder = new FakeRecorder();
        var provider = new FakeProvider("   ");
        var sender = new FakeSender();
        var clipboard = new FakeClipboard();
        var controller = new VoiceInputController(
            recorder, provider, sender, clipboard,
            () => new ClapBackSettings { AutoSend = true });

        await controller.HandleHotkeyAsync();
        await controller.HandleHotkeyAsync();

        Assert.Null(sender.LastText);
        Assert.Null(clipboard.Text);
        Assert.Equal(ClapBackState.Idle, controller.State);
        Assert.Contains("没有识别到有效语音", controller.LastError ?? string.Empty);
    }

    [Fact]
    public async Task LocalOnlyModeDoesNotInvokeCloudProvider()
    {
        var local = new CountingProvider(new TranscriptResult("本地结果"));
        var cloud = new CountingProvider(new TranscriptResult("云端结果"));
        var provider = new CompositeTranscriptionProvider(
            local,
            cloud,
            () => new ClapBackSettings { TranscriptionMode = TranscriptionMode.LocalOnly });

        await using var audio = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await provider.TranscribeAsync(audio, new TranscriptionOptions("glm-asr-2512", "zh"));

        Assert.Equal("本地结果", result.Text);
        Assert.Equal(1, local.CallCount);
        Assert.Equal(0, cloud.CallCount);
    }

    [Fact]
    public async Task LocalFirstModeRewindsAudioBeforeCloudFallback()
    {
        var local = new ThrowingProvider(new InvalidOperationException("本地模型不可用"));
        var cloud = new RecordingProvider(new TranscriptResult("云端结果"));
        var provider = new CompositeTranscriptionProvider(
            local,
            cloud,
            () => new ClapBackSettings { TranscriptionMode = TranscriptionMode.LocalFirst });

        await using var audio = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await provider.TranscribeAsync(audio, new TranscriptionOptions("glm-asr-2512", "zh"));

        Assert.Equal("云端结果", result.Text);
        Assert.Equal(new byte[] { 1, 2, 3 }, cloud.AudioBytes);
    }

    private sealed class FakeRecorder : IAudioRecorder
    {
        public bool IsRecording { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = true;
            return Task.CompletedTask;
        }

        public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            return Task.FromResult(new RecordedAudio(new MemoryStream(new byte[] { 1, 2, 3 }), "test.wav"));
        }

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProvider(string text) : ITranscriptionProvider
    {
        public Task<TranscriptResult> TranscribeAsync(
            Stream audio,
            TranscriptionOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranscriptResult(text));
    }

    private sealed class FakeSender : ITextSender
    {
        public string? LastText { get; private set; }

        public Task SendAsync(string text, CancellationToken cancellationToken = default)
        {
            LastText = text;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingProvider(TranscriptResult result) : ITranscriptionProvider
    {
        public int CallCount { get; private set; }

        public Task<TranscriptResult> TranscribeAsync(
            Stream audio,
            TranscriptionOptions options,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingProvider(Exception exception) : ITranscriptionProvider
    {
        public Task<TranscriptResult> TranscribeAsync(
            Stream audio,
            TranscriptionOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TranscriptResult>(exception);
    }

    private sealed class RecordingProvider(TranscriptResult result) : ITranscriptionProvider
    {
        public byte[]? AudioBytes { get; private set; }

        public async Task<TranscriptResult> TranscribeAsync(
            Stream audio,
            TranscriptionOptions options,
            CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await audio.CopyToAsync(copy, cancellationToken);
            AudioBytes = copy.ToArray();
            return result;
        }
    }
}

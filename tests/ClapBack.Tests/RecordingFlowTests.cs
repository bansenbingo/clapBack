using ClapBack.Core.Abstractions;
using ClapBack.Core.Controller;
using ClapBack.Core.Models;
using Xunit;

namespace ClapBack.Tests;

public sealed class RecordingFlowTests
{
    [Theory]
    [InlineData("CapsLock", "CapsLock", 0x14, 0)]
    [InlineData("ctrl + shift + space", "Ctrl+Shift+Space", 0x20, 6)]
    [InlineData("Alt+F24", "Alt+F24", 0x87, 1)]
    [InlineData("Control+K", "Ctrl+K", 0x4B, 2)]
    public void Hotkey_ParsesSupportedBindings(string input, string name, int key, int modifiers)
    {
        Assert.True(HotkeyBinding.TryParse(input, out var binding));
        Assert.Equal(name, binding.Name);
        Assert.Equal(key, binding.VirtualKey);
        Assert.Equal((uint)modifiers, binding.Modifiers);
    }

    [Theory]
    [InlineData("Ctrl+Ctrl+K")]
    [InlineData("F25")]
    [InlineData("K")]
    [InlineData("Ctrl+")]
    [InlineData("")]
    public void Hotkey_RejectsInvalidBindings(string input) => Assert.False(HotkeyBinding.TryParse(input, out _));

    [Fact]
    public async Task CancelDuringRecording_DiscardsAudioAndAllowsAnotherRecording()
    {
        var recorder = new Recorder();
        var clipboard = new Clipboard();
        var controller = new VoiceInputController(recorder, new Provider(), new Sender(), clipboard, () => new());
        await controller.HandleHotkeyAsync();
        await controller.CancelAsync();
        Assert.Equal(ClapBackState.Idle, controller.State);
        Assert.False(recorder.IsRecording);
        Assert.Null(clipboard.Text);
        await controller.HandleHotkeyAsync();
        Assert.Equal(2, recorder.Starts);
        Assert.True(recorder.IsRecording);
        await controller.CancelAsync();
    }

    [Fact]
    public async Task CancelDuringTranscription_DoesNotCopyLateProviderResult()
    {
        var provider = new DelayedProvider();
        var clipboard = new Clipboard();
        var controller = new VoiceInputController(new Recorder(), provider, new Sender(), clipboard, () => new());
        await controller.HandleHotkeyAsync();
        var stop = controller.HandleHotkeyAsync();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var cancel = controller.CancelAsync();
        // A provider that ignores cancellation still must not deliver its result.
        provider.Result.SetResult(new TranscriptResult("过期结果"));
        await Task.WhenAll(stop, cancel).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ClapBackState.Idle, controller.State);
        Assert.Null(clipboard.Text);
        Assert.Null(controller.LastTranscript);
    }

    [Fact]
    public async Task Recording_UsesSettingsCapturedAtStart()
    {
        var settings = new ClapBackSettings { AutoSend = false, EnableHomophoneSubstitution = false, Language = "zh" };
        var provider = new Provider();
        var sender = new Sender();
        var clipboard = new Clipboard();
        var controller = new VoiceInputController(new Recorder(), provider, sender, clipboard, () => settings);
        await controller.HandleHotkeyAsync();
        settings = settings with { AutoSend = true, Language = "en" };
        await controller.HandleHotkeyAsync();
        Assert.Equal("zh", provider.Language);
        Assert.Null(sender.Text);
        Assert.Equal("集合", clipboard.Text);
    }

    [Fact]
    public async Task ClipboardFailure_PreservesTranscriptForManualRetry()
    {
        var sender = new Sender();
        var controller = new VoiceInputController(new Recorder(), new Provider(), sender, new Clipboard { Fail = true }, () => new());
        await controller.HandleHotkeyAsync();
        await controller.HandleHotkeyAsync();
        Assert.Equal("集合", controller.LastTranscript);
        Assert.Contains("剪贴板占用", controller.LastError);
        Assert.Null(sender.Text);
        Assert.Equal(ClapBackState.Idle, controller.State);
    }

    [Fact]
    public async Task RecordingLimit_StopsAndCopiesWithoutStartingAnotherRecording()
    {
        var recorder = new Recorder();
        var clipboard = new Clipboard();
        var controller = new VoiceInputController(recorder, new Provider(), new Sender(), clipboard,
            () => new ClapBackSettings { MaxRecordingSeconds = 1 });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StateChanged += (_, state) => { if (state == ClapBackState.Success) done.TrySetResult(); };
        await controller.HandleHotkeyAsync();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, recorder.Starts);
        Assert.False(recorder.IsRecording);
        Assert.Equal("集合", clipboard.Text);
        await controller.CancelAsync();
    }

    private sealed class Recorder : IAudioRecorder
    {
        public bool IsRecording { get; private set; }
        public int Starts { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken = default)
        { IsRecording = true; Starts++; return Task.CompletedTask; }
        public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
        { IsRecording = false; return Task.FromResult(new RecordedAudio(new MemoryStream([1, 2, 3]), "test.wav")); }
        public Task CancelAsync(CancellationToken cancellationToken = default)
        { IsRecording = false; return Task.CompletedTask; }
    }

    private sealed class Provider : ITranscriptionProvider
    {
        public string? Language { get; private set; }
        public Task<TranscriptResult> TranscribeAsync(Stream audio, TranscriptionOptions options, CancellationToken cancellationToken = default)
        { Language = options.Language; return Task.FromResult(new TranscriptResult("集合")); }
    }

    private sealed class DelayedProvider : ITranscriptionProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TranscriptResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TranscriptResult> TranscribeAsync(Stream audio, TranscriptionOptions options, CancellationToken cancellationToken = default)
        { Started.TrySetResult(); return Result.Task; }
    }

    private sealed class Sender : ITextSender
    {
        public string? Text { get; private set; }
        public Task SendAsync(string text, CancellationToken cancellationToken = default)
        { Text = text; return Task.CompletedTask; }
    }

    private sealed class Clipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public bool Fail { get; init; }
        public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        { if (Fail) throw new IOException("剪贴板占用"); Text = text; return Task.CompletedTask; }
    }
}

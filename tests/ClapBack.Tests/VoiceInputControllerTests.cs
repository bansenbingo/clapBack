using ClapBack.Core.Abstractions;
using ClapBack.Core.Controller;
using ClapBack.Core.Models;
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
        var controller = new VoiceInputController(recorder, provider, sender, () => new ClapBackSettings());

        await controller.HandleHotkeyAsync();
        Assert.Equal(ClapBackState.Recording, controller.State);
        Assert.True(recorder.IsRecording);

        await controller.HandleHotkeyAsync();

        Assert.Equal(ClapBackState.Success, controller.State);
        Assert.Equal("你好，集合！", sender.LastText);
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public async Task EmptyTranscriptDoesNotSend()
    {
        var recorder = new FakeRecorder();
        var provider = new FakeProvider("   ");
        var sender = new FakeSender();
        var controller = new VoiceInputController(recorder, provider, sender, () => new ClapBackSettings());

        await controller.HandleHotkeyAsync();
        await controller.HandleHotkeyAsync();

        Assert.Null(sender.LastText);
        Assert.Equal(ClapBackState.Idle, controller.State);
        Assert.Contains("没有识别到有效语音", controller.LastError ?? string.Empty);
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
}

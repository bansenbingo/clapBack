using ClapBack.Core.Abstractions;
using ClapBack.Core.Controller;
using ClapBack.Core.Models;
using ClapBack.Core.Services;
using Xunit;

namespace ClapBack.Tests;

/// <summary>
/// 「句末不要句号」这条需求的护栏：模型补的句号必须被去掉，但问号/感叹号/句中标点要留着。
/// </summary>
public sealed class TranscriptCleaningTests
{
    [Theory]
    [InlineData("今天天气不错，我们一起去打游戏吧。", "今天天气不错，我们一起去打游戏吧")]
    [InlineData("好。。", "好")]
    [InlineData("好。 ", "好")]
    [InlineData("真的吗？", "真的吗？")]
    [InlineData("太好了！", "太好了！")]
    [InlineData("前半句。后半句。", "前半句。后半句")]
    [InlineData("版本 3.14", "版本 3.14")]
    [InlineData("", "")]
    [InlineData("。", "")]
    public void StripTrailingPeriod_RemovesOnlySentenceEndingPeriods(string input, string expected)
        => Assert.Equal(expected, TranscriptCleaner.StripTrailingPeriod(input));

    [Fact]
    public void NewSettings_DefaultToTheRequestedBehaviour()
    {
        var settings = new ClapBackSettings();

        Assert.True(settings.TrimTrailingPunctuation);
        Assert.True(settings.MinimizeToTray);
        Assert.True(settings.RunAsAdministrator);
        Assert.True(settings.ShowOverlay);
        Assert.Equal(30, settings.OverlayFontSize);
        Assert.Equal(0.85, settings.OverlayOpacity);
        Assert.Equal(6, settings.OverlayDurationSeconds);
        // 没拖过气泡时用 null 表示「按程序默认位置显示」，不能是 0（0,0 会顶到屏幕左上角）。
        // 也不能用 double.NaN：那是非法 JSON，会让「保存设置」直接抛异常。
        Assert.Null(settings.OverlayLeft);
        Assert.Null(settings.OverlayTop);
    }

    [Fact]
    public async Task Controller_CopiesTextWithoutTrailingPeriod()
    {
        var clipboard = new Clipboard();
        var controller = new VoiceInputController(new Recorder(), new Provider("集合。"), new Sender(), clipboard,
            () => new ClapBackSettings());

        await controller.HandleHotkeyAsync();
        await controller.HandleHotkeyAsync();

        Assert.Equal("集合", clipboard.Text);
        Assert.Equal("集合", controller.LastTranscript);
    }

    [Fact]
    public async Task Controller_KeepsTrailingPeriodWhenDisabled()
    {
        var clipboard = new Clipboard();
        var controller = new VoiceInputController(new Recorder(), new Provider("集合。"), new Sender(), clipboard,
            () => new ClapBackSettings { TrimTrailingPunctuation = false });

        await controller.HandleHotkeyAsync();
        await controller.HandleHotkeyAsync();

        Assert.Equal("集合。", clipboard.Text);
    }

    [Fact]
    public void OverlayPosition_RoundTripsThroughSettingsJson()
    {
        // 和 JsonSettingsStore 用同一套序列化选项：这里挂了说明「保存设置」会挂。
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

        var untouched = System.Text.Json.JsonSerializer.Deserialize<ClapBackSettings>(
            System.Text.Json.JsonSerializer.Serialize(new ClapBackSettings(), options), options);
        Assert.Null(untouched!.OverlayLeft);
        Assert.Null(untouched.OverlayTop);

        var moved = new ClapBackSettings
        {
            OverlayLeft = 1234.5,
            OverlayTop = 678.25,
            OverlayFontSize = 42,
            OverlayOpacity = 0.5,
            OverlayDurationSeconds = 12,
        };
        var back = System.Text.Json.JsonSerializer.Deserialize<ClapBackSettings>(
            System.Text.Json.JsonSerializer.Serialize(moved, options), options)!;

        Assert.Equal(1234.5, back.OverlayLeft);
        Assert.Equal(678.25, back.OverlayTop);
        Assert.Equal(42, back.OverlayFontSize);
        Assert.Equal(0.5, back.OverlayOpacity);
        Assert.Equal(12, back.OverlayDurationSeconds);
    }

    private sealed class Recorder : IAudioRecorder
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
            return Task.FromResult(new RecordedAudio(new MemoryStream([1, 2, 3]), "test.wav"));
        }

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            return Task.CompletedTask;
        }
    }

    private sealed class Provider(string text) : ITranscriptionProvider
    {
        public Task<TranscriptResult> TranscribeAsync(Stream audio, TranscriptionOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TranscriptResult(text));
    }

    private sealed class Sender : ITextSender
    {
        public string? Text { get; private set; }

        public Task SendAsync(string text, CancellationToken cancellationToken = default)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }

    private sealed class Clipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }
}

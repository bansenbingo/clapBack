using System.Text.Json;
using ClapBack.Core.Controller;
using ClapBack.Core.Models;
using ClapBack.Core.Services;
using Xunit;

namespace ClapBack.Tests;

/// <summary>
/// 敏感字同音替换：核心诉求是「表达原意但不直接骂人」，
/// 所以既要换得掉，也绝不能把正常词换坏、或者把替换结果再换一次。
/// </summary>
public sealed class HomophoneSubstitutionTests
{
    private static HomophoneSubstitutor Substitutor(params HomophoneEntry[] entries)
        => new(entries, new Random(0));

    private static HomophoneEntry Rule(string source, params string[] alternatives)
        => new(source, alternatives);

    [Fact]
    public void ReplacesSourceWordWithOneOfItsAlternatives()
    {
        var substitutor = Substitutor(Rule("傻逼", "沙币", "啥波一", "艾斯比"));

        var result = substitutor.Replace("你个傻逼");

        Assert.Equal("你个", result[..2]);
        Assert.Contains(result[2..], new[] { "沙币", "啥波一", "艾斯比" });
    }

    [Fact]
    public void AllOccurrencesAreReplaced()
    {
        var substitutor = Substitutor(Rule("傻逼", "沙币"));

        Assert.Equal("沙币和沙币", substitutor.Replace("傻逼和傻逼"));
    }

    /// <summary>
    /// 最长优先：「他妈的」必须整条命中，不能被「妈的」从中间切开。
    /// </summary>
    [Fact]
    public void LongerRuleWinsOverShorterOne()
    {
        var substitutor = Substitutor(
            Rule("妈的", "嘛的"),
            Rule("他妈的", "踏马的"));

        Assert.Equal("你踏马的", substitutor.Replace("你他妈的"));
    }

    /// <summary>
    /// 替换结果不能再次参与匹配：否则候选词里含另一个敏感词会被换两次。
    /// </summary>
    [Fact]
    public void ReplacementIsNotRescanned()
    {
        var substitutor = Substitutor(
            Rule("脏改", "二次"),
            Rule("脏", "脏改"));

        Assert.Equal("脏改", substitutor.Replace("脏"));
    }

    [Theory]
    [InlineData("你是sb", "你是艾斯比")]
    [InlineData("你是SB", "你是艾斯比")]
    [InlineData("Sb", "艾斯比")]
    public void LatinRuleMatchesCaseInsensitively(string input, string expected)
    {
        var substitutor = Substitutor(Rule("sb", "艾斯比"));

        Assert.Equal(expected, substitutor.Replace(input));
    }

    /// <summary>
    /// 纯字母词必须按整词匹配：<c>usb</c> 里的 <c>sb</c> 不是脏话，不能动。
    /// </summary>
    [Theory]
    [InlineData("usb stick")]
    [InlineData("sbtest")]
    [InlineData("tsb")]
    public void LatinRuleDoesNotMatchInsideAnotherWord(string input)
    {
        var substitutor = Substitutor(Rule("sb", "艾斯比"));

        Assert.Equal(input, substitutor.Replace(input));
    }

    [Fact]
    public void TextWithoutAnySourceWordIsUntouched()
    {
        var substitutor = Substitutor(Rule("傻逼", "沙币"));

        Assert.Equal("集合准备进攻", substitutor.Replace("集合准备进攻"));
    }

    [Fact]
    public void EmptyOrMissingEntriesReturnTextUnchanged()
    {
        Assert.Equal("随便", new HomophoneSubstitutor(null).Replace("随便"));
        Assert.Equal("随便", new HomophoneSubstitutor(Array.Empty<HomophoneEntry>()).Replace("随便"));
    }

    [Fact]
    public void DisabledSettingLeavesTextUnchanged()
    {
        var settings = new ClapBackSettings
        {
            EnableHomophoneSubstitution = false,
            HomophoneEntries = new[] { Rule("傻逼", "沙币") }
        };

        Assert.Equal("傻逼", HomophoneSubstitution.Apply(settings, "傻逼"));
    }

    [Fact]
    public void EnabledSettingReplacesThroughTheFacade()
    {
        var settings = new ClapBackSettings
        {
            EnableHomophoneSubstitution = true,
            HomophoneEntries = new[] { Rule("傻逼", "沙币") }
        };

        Assert.Equal("沙币", HomophoneSubstitution.Apply(settings, "傻逼"));
    }

    // ------------------------------------------------------------ 内置字库的护栏

    /// <summary>
    /// 字库必须能跟着设置一起存下来、再读回来——GUI 里的增删改就靠这条路径持久化。
    /// 两个设置存储（Windows 的 <c>JsonSettingsStore</c> 和 Desktop 的 <c>DesktopSettingsStore</c>）
    /// 用的都是 <see cref="JsonSerializerDefaults.Web"/>，这里按同样格式验证。
    /// </summary>
    [Fact]
    public void DictionaryRoundTripsThroughTheSettingsJsonFormat()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var settings = new ClapBackSettings
        {
            HomophoneEntries = new[] { new HomophoneEntry("傻逼", new[] { "沙币", "啥波一" }) }
        };

        var restored = JsonSerializer.Deserialize<ClapBackSettings>(
            JsonSerializer.Serialize(settings, options), options);

        Assert.NotNull(restored);
        var entry = Assert.Single(restored.HomophoneEntries);
        Assert.Equal("傻逼", entry.Source);
        Assert.Equal(new[] { "沙币", "啥波一" }, entry.Alternatives);
        Assert.True(restored.EnableHomophoneSubstitution);
    }

    /// <summary>
    /// 老版本的设置文件里没有这个字段，读出来应该落到内置字库，而不是变成空字库（那等于功能悄悄失效）。
    /// </summary>
    [Fact]
    public void SettingsWithoutTheDictionaryFallBackToBuiltInDefaults()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<ClapBackSettings>("{}", options);

        Assert.NotNull(restored);
        Assert.True(restored.EnableHomophoneSubstitution);
        Assert.NotEmpty(restored.HomophoneEntries);
    }

    /// <summary>
    /// 单字规则会误伤「操作」「操场」这类正常词，所以默认字库只收两字及以上。
    /// </summary>
    [Fact]
    public void BuiltInDictionaryHasNoSingleCharacterSources()
    {
        foreach (var entry in HomophoneDefaults.All)
        {
            Assert.True(entry.Source.Length >= 2,
                $"默认字库不接受单字规则：「{entry.Source}」");
        }
    }

    [Fact]
    public void BuiltInDictionaryEntriesAreWellFormed()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in HomophoneDefaults.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Source));
            Assert.True(seen.Add(entry.Source), $"默认字库有重复条目：「{entry.Source}」");
            Assert.NotEmpty(entry.Alternatives);
            foreach (var alternative in entry.Alternatives)
            {
                Assert.False(string.IsNullOrWhiteSpace(alternative));
                // 换了等于没换，说明这条规则没写对。
                Assert.NotEqual(entry.Source, alternative);
            }
        }
    }

    /// <summary>
    /// 用户给的样例是硬需求：「傻逼」要能换成本字库点名的那几种写法。
    /// </summary>
    [Fact]
    public void BuiltInDictionaryCoversTheCanonicalExample()
    {
        var entry = Assert.Single(HomophoneDefaults.All, e => e.Source == "傻逼");
        foreach (var expected in new[] { "啥必", "沙币", "煞比", "艾斯比", "埃斯比", "啥波一" })
        {
            Assert.Contains(expected, entry.Alternatives);
        }
    }

    /// <summary>
    /// 端到端：识别出来的脏字必须在写剪贴板之前就已经换掉，
    /// 这样玩家按 Ctrl+V 粘出去的是替换后的文字。
    /// </summary>
    [Fact]
    public async Task TranscriptIsReplacedBeforeItReachesTheClipboard()
    {
        var clipboard = new CapturingClipboard();
        var sender = new NoOpSender();
        var controller = new VoiceInputController(
            new IdleRecorder(),
            new FixedProvider("你个傻逼"),
            sender,
            clipboard,
            () => new ClapBackSettings
            {
                HomophoneEntries = new[] { Rule("傻逼", "沙币") }
            });

        await controller.HandleHotkeyAsync();
        await controller.HandleHotkeyAsync();

        Assert.Equal("你个沙币", clipboard.Text);
        Assert.Equal("你个沙币", controller.LastTranscript);
    }

    private sealed class CapturingClipboard : ClapBack.Core.Abstractions.IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpSender : ClapBack.Core.Abstractions.ITextSender
    {
        public Task SendAsync(string text, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class IdleRecorder : ClapBack.Core.Abstractions.IAudioRecorder
    {
        public bool IsRecording { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = true;
            return Task.CompletedTask;
        }

        public Task<ClapBack.Core.Models.RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            return Task.FromResult(new RecordedAudio(new MemoryStream(new byte[] { 1 }), "clapback.wav"));
        }

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedProvider(string text) : ClapBack.Core.Abstractions.ITranscriptionProvider
    {
        public Task<TranscriptResult> TranscribeAsync(
            Stream audio,
            ClapBack.Core.Models.TranscriptionOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TranscriptResult(text));
    }
}

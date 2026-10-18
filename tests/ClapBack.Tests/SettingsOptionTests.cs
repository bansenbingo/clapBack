using ClapBack.Core.Services;
using Xunit;

namespace ClapBack.Tests;

/// <summary>
/// 设置里「从本机检测 + 下拉选择」的护栏：候选列表可能带 .exe、大小写不一、有多余分隔符，
/// 落盘前必须整理成统一的白名单格式，否则 OverwatchTextSender 会认不出目标进程。
/// </summary>
public sealed class SettingsOptionTests
{
    [Theory]
    [InlineData("Overwatch,Overwatch2", "Overwatch,Overwatch2")]
    [InlineData(" Overwatch.exe , Overwatch2.EXE ", "Overwatch,Overwatch2")]
    [InlineData("Overwatch;overwatch2", "Overwatch,overwatch2")]
    [InlineData("Overwatch，Overwatch2", "Overwatch,Overwatch2")]
    [InlineData("Overwatch,Overwatch,OVERWATCH", "Overwatch")]
    [InlineData("守望先锋", "守望先锋")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void NormalizeList_ProducesCanonicalWhitelist(string? input, string expected)
        => Assert.Equal(expected, LocalProcessCatalog.NormalizeList(input));

    [Fact]
    public void WindowProcessNames_RunsWithoutThrowing()
    {
        // 这台机器上可能一个窗口进程都枚举不到（CI/服务会话），关键是不能抛异常把设置窗口带崩。
        var names = LocalProcessCatalog.ListWindowProcessNames();

        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

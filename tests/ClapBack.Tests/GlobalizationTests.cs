using System.Globalization;
using Xunit;

namespace ClapBack.Tests;

/// <summary>
/// 运行时不变量全球化（InvariantGlobalization）的护栏。
/// </summary>
/// <remarks>
/// 打开这个开关会让 WPF 的 <c>DataGrid</c> 在行校验时崩掉：
/// <c>DataGridRow.DelayedValidateWithoutUpdate</c> → <c>BindingGroup.CheckValidationRules</c>
/// → <c>BindingGroup.GetCulture</c> → <c>XmlLanguage.GetSpecificCulture()</c>，
/// 而 ICU 被裁掉之后这一步会抛
/// <c>InvalidOperationException: Cannot find non-neutral culture related to 'en-us'</c>。
/// 用户看到的现象是「点开『字库』标签页，整个程序消失」。
///
/// 这条测试盯的是根因（开关本身），因为 WPF 控件树没法在 net8.0 的测试工程里搭起来；
/// 谁再往 Directory.Build.props 里加回 <c>&lt;InvariantGlobalization&gt;true</c>，这里就会红。
/// </remarks>
public sealed class GlobalizationTests
{
    [Fact]
    public void Runtime_DoesNotUseInvariantGlobalization()
    {
        Assert.False(
            AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant) && invariant,
            "InvariantGlobalization 会让 WPF DataGrid 的行校验抛 XmlLanguage『Cannot find non-neutral culture』，"
            + "打开「字库」标签页时整个程序会崩。请在 Directory.Build.props 里保持 InvariantGlobalization=false。");
    }

    [Fact]
    public void Runtime_ResolvesSpecificCultures()
    {
        // 同一个根因的行为面：不变量模式下 GetCultureInfo("en-US") 只会回退成 InvariantCulture。
        var culture = CultureInfo.GetCultureInfo("en-US");

        Assert.Equal("en-US", culture.Name);
        Assert.NotEqual(CultureInfo.InvariantCulture.Name, culture.Name);
    }
}

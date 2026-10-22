using ClapBack.Core.Models;
using ClapBack.Core.Services;
using Xunit;

namespace ClapBack.Tests;

/// <summary>
/// 自动发送的三步流程：打开聊天 → Ctrl+V 粘贴 → 按发送键发出。
/// 第一步和第三步都是设置里手打的单键，守望先锋的聊天框是游戏自己绘制、用 Raw Input 读键的，
/// 等待必须比普通窗口长，否则会出现「文字没粘进去」或「粘进去了没发出去」。
/// </summary>
public sealed class ChatSendPlannerTests
{
    private const ushort VkT = 0x54;

    [Fact]
    public void Default_Settings_Open_With_Enter_And_Send_With_Enter()
    {
        var settings = new ClapBackSettings();

        Assert.Equal("Enter", settings.ChatOpenKey);
        Assert.Equal("Enter", settings.SendKey);
    }

    [Fact]
    public void Overwatch_Uses_ChatKey_Then_Paste_Then_SendKey()
    {
        var plan = ChatSendPlanner.Plan(ChatSendPlanner.VkEnter, ChatSendPlanner.VkEnter, "Overwatch");

        Assert.Equal(3, plan.Count);
        Assert.Equal(ChatSendPlanner.VkEnter, plan[0].VirtualKey);
        Assert.Equal((ushort)0, plan[0].Modifier);
        Assert.Equal(ChatSendPlanner.VkV, plan[1].VirtualKey);
        Assert.Equal(ChatSendPlanner.VkControl, plan[1].Modifier);
        Assert.Equal(ChatSendPlanner.VkEnter, plan[2].VirtualKey);
        Assert.Equal((ushort)0, plan[2].Modifier);
    }

    [Fact]
    public void Custom_Keys_Replace_First_And_Third_Step()
    {
        // 「手打 T 呼出聊天，Enter 发送」——用户给的例子。
        var plan = ChatSendPlanner.Plan(VkT, ChatSendPlanner.VkEnter, "Overwatch");

        Assert.Equal(VkT, plan[0].VirtualKey);
        Assert.Equal(ChatSendPlanner.VkV, plan[1].VirtualKey);
        Assert.Equal(ChatSendPlanner.VkEnter, plan[2].VirtualKey);
    }

    [Fact]
    public void Paste_Step_Ignores_Custom_Keys()
    {
        // 第二步永远是 Ctrl+V，不跟着设置走。
        var plan = ChatSendPlanner.Plan(VkT, VkT, "Overwatch");

        Assert.Equal(VkT, plan[0].VirtualKey);
        Assert.Equal(ChatSendPlanner.VkControl, plan[1].Modifier);
        Assert.Equal(ChatSendPlanner.VkV, plan[1].VirtualKey);
        Assert.Equal(VkT, plan[2].VirtualKey);
    }

    [Fact]
    public void Overwatch_Waits_Longer_Than_A_Plain_Window()
    {
        var game = ChatSendPlanner.Plan(ChatSendPlanner.VkEnter, ChatSendPlanner.VkEnter, "Overwatch");
        var window = ChatSendPlanner.Plan(ChatSendPlanner.VkEnter, ChatSendPlanner.VkEnter, "notepad");

        for (var index = 0; index < 3; index++)
        {
            Assert.True(game[index].WaitAfterMs > window[index].WaitAfterMs);
        }

        // 开聊天之后必须留出游戏跑完一帧 UI 的时间。
        Assert.True(game[0].WaitAfterMs >= 200);
    }

    [Theory]
    [InlineData("T", 0x54, "T")]
    [InlineData("t", 0x54, "T")]
    [InlineData("  t  ", 0x54, "T")]
    [InlineData("7", 0x37, "7")]
    [InlineData("Enter", 0x0D, "Enter")]
    [InlineData("return", 0x0D, "Enter")]
    [InlineData("回车", 0x0D, "Enter")]
    [InlineData("space", 0x20, "Space")]
    [InlineData("空格", 0x20, "Space")]
    [InlineData("esc", 0x1B, "Escape")]
    [InlineData("F2", 0x71, "F2")]
    [InlineData("f24", 0x87, "F24")]
    [InlineData("PgUp", 0x21, "PageUp")]
    public void TryParseKey_Accepts_Single_Keys(string text, int expectedKey, string expectedName)
    {
        Assert.True(ChatSendPlanner.TryParseKey(text, out var virtualKey, out var name));
        Assert.Equal(expectedKey, virtualKey);
        Assert.Equal(expectedName, name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+V")]
    [InlineData("T+Enter")]
    [InlineData("F25")]
    [InlineData("不存在的键")]
    public void TryParseKey_Rejects_Anything_Else(string? text)
    {
        Assert.False(ChatSendPlanner.TryParseKey(text, out _, out _));
    }

    [Theory]
    [InlineData("T", 0x54)]
    [InlineData("enter", 0x0D)]
    [InlineData("F3", 0x72)]
    public void ChatOpenVirtualKey_Follows_Settings(string? name, int expected)
        => Assert.Equal(expected, ChatSendPlanner.ChatOpenVirtualKey(name));

    [Theory]
    [InlineData("T", 0x54)]
    [InlineData("SPACE", 0x20)]
    public void SendVirtualKey_Follows_Settings(string? name, int expected)
        => Assert.Equal(expected, ChatSendPlanner.SendVirtualKey(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl+V")]
    [InlineData("不存在的键")]
    public void Broken_Setting_Falls_Back_To_Enter(string? name)
    {
        // 配置写坏了不能让自动发送整体失效。
        Assert.Equal(ChatSendPlanner.VkEnter, ChatSendPlanner.ChatOpenVirtualKey(name));
        Assert.Equal(ChatSendPlanner.VkEnter, ChatSendPlanner.SendVirtualKey(name));
    }

    [Theory]
    [InlineData("Overwatch", true)]
    [InlineData("Overwatch2", true)]
    [InlineData("overwatch.exe", true)]
    [InlineData(" OVERWATCH ", true)]
    [InlineData("notepad", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGameTarget_Recognizes_Overwatch_Only(string? processName, bool expected)
        => Assert.Equal(expected, ChatSendPlanner.IsGameTarget(processName));
}

namespace ClapBack.Core.Services;

/// <summary>
/// 「打开聊天框 → Ctrl+V 粘贴 → 发送」里的任何一步。
/// </summary>
/// <param name="VirtualKey">要按下的主键（SendInput 用的虚拟键码）。</param>
/// <param name="Modifier">需要同时按住的修饰键；<c>0</c> 表示没有。</param>
/// <param name="WaitAfterMs">这一步按完之后等多久再做下一步（毫秒）。</param>
public readonly record struct ChatSendStep(ushort VirtualKey, ushort Modifier, int WaitAfterMs);

/// <summary>
/// 把「识别结果送进游戏聊天框」固定成三步，并给出每一步之后该等多久。
/// <para>
/// 守望先锋的聊天框不是 Win32 控件，而是游戏自己绘制、用 Raw Input 读键盘的界面：
/// 从按下开聊天键到输入框真正能接受输入之间要跑完一帧 UI，Ctrl+V 之后游戏还要自己去读一次剪贴板，
/// 所以游戏内每一步的等待都比普通窗口长。等待太短的表现是「文字没粘进去」或「粘进去了但没发出去」。
/// </para>
/// <para>
/// 第一步和第三步的按键都可以在设置里改（例如 <c>T</c> 呼出聊天、<c>Enter</c> 发送），
/// 第二步永远是 Ctrl+V。
/// </para>
/// </summary>
public static class ChatSendPlanner
{
    /// <summary>Enter：默认的发送键，也是绝大多数游戏默认的开聊天键。</summary>
    public const ushort VkEnter = 0x0D;

    /// <summary>Ctrl：粘贴时的修饰键。</summary>
    public const ushort VkControl = 0x11;

    /// <summary>V：粘贴键。</summary>
    public const ushort VkV = 0x56;

    // 游戏：聊天框要跑完一帧 UI 才吃键，粘贴后要等游戏读一次剪贴板，发送后留一帧收尾。
    private const int GameChatOpenWaitMs = 260;
    private const int GamePasteWaitMs = 200;
    private const int GameSendWaitMs = 140;

    // 普通窗口：控件是现成的，短等待就够。
    private const int WindowChatOpenWaitMs = 90;
    private const int WindowPasteWaitMs = 120;
    private const int WindowSendWaitMs = 100;

    private static readonly string[] GameProcessNames = { "Overwatch", "Overwatch2" };

    /// <summary>目标进程是不是守望先锋（一代或二代都用 Overwatch.exe）。</summary>
    public static bool IsGameTarget(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return GameProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析一个「单键」名字，例如 <c>T</c>、<c>7</c>、<c>Enter</c>、<c>Space</c>、<c>F2</c>。
    /// <para>
    /// 只认单键，不接受 <c>Ctrl+V</c> 这类组合：呼出聊天和确认发送原本就是各按一个键，
    /// 组合键在这里没有意义，放开只会让设置里出现一堆用不上的写法。
    /// </para>
    /// </summary>
    /// <param name="text">用户手打或从配置里读到的按键名。</param>
    /// <param name="virtualKey">解析出的虚拟键码。</param>
    /// <param name="name">规范化后的显示名，例如输入 <c>t</c> 得到 <c>T</c>、输入 <c>esc</c> 得到 <c>Escape</c>。</param>
    public static bool TryParseKey(string? text, out ushort virtualKey, out string name)
    {
        virtualKey = 0;
        name = string.Empty;

        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (trimmed.Length == 1)
        {
            var letter = char.ToUpperInvariant(trimmed[0]);
            if (letter is >= 'A' and <= 'Z')
            {
                virtualKey = (ushort)(0x41 + (letter - 'A'));
                name = letter.ToString();
                return true;
            }

            if (letter is >= '0' and <= '9')
            {
                virtualKey = (ushort)(0x30 + (letter - '0'));
                name = letter.ToString();
                return true;
            }
        }

        if (KeyAliases.TryGetValue(trimmed, out var alias))
        {
            virtualKey = alias.VirtualKey;
            name = alias.Name;
            return true;
        }

        return false;
    }

    /// <summary>呼出聊天框的按键。配置里写了不认识的键就退回 Enter，绝不因为一个破配置让发送整体失效。</summary>
    public static ushort ChatOpenVirtualKey(string? chatOpenKey) =>
        TryParseKey(chatOpenKey, out var virtualKey, out _) ? virtualKey : VkEnter;

    /// <summary>确认发送的按键。配置里写了不认识的键就退回 Enter。</summary>
    public static ushort SendVirtualKey(string? sendKey) =>
        TryParseKey(sendKey, out var virtualKey, out _) ? virtualKey : VkEnter;

    /// <summary>
    /// 三步流程：打开聊天 → Ctrl+V → 发送。
    /// </summary>
    /// <param name="chatOpenVirtualKey">第一步要按的键，通常来自 <see cref="ChatOpenVirtualKey"/>。</param>
    /// <param name="sendVirtualKey">第三步要按的键，通常来自 <see cref="SendVirtualKey"/>。</param>
    /// <param name="targetProcessName">当前前台的目标进程名，用来决定等待长度。</param>
    public static IReadOnlyList<ChatSendStep> Plan(ushort chatOpenVirtualKey, ushort sendVirtualKey, string? targetProcessName)
    {
        var game = IsGameTarget(targetProcessName);
        return new[]
        {
            new ChatSendStep(chatOpenVirtualKey, 0, game ? GameChatOpenWaitMs : WindowChatOpenWaitMs),
            new ChatSendStep(VkV, VkControl, game ? GamePasteWaitMs : WindowPasteWaitMs),
            new ChatSendStep(sendVirtualKey, 0, game ? GameSendWaitMs : WindowSendWaitMs)
        };
    }

    /// <summary>
    /// 钥匙名 → 虚拟键码。别名（<c>esc</c>、<c>回车</c>、<c>PgUp</c>）都指回同一个规范名，
    /// 免得配置里出现三种写法的同一个键。
    /// </summary>
    private static readonly Dictionary<string, (string Name, ushort VirtualKey)> KeyAliases = BuildKeyAliases();

    private static Dictionary<string, (string Name, ushort VirtualKey)> BuildKeyAliases()
    {
        var map = new Dictionary<string, (string Name, ushort VirtualKey)>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, ushort virtualKey, params string[] aliases)
        {
            map[name] = (name, virtualKey);
            foreach (var alias in aliases)
            {
                map[alias] = (name, virtualKey);
            }
        }

        Add("Enter", VkEnter, "Return", "回车");
        Add("Space", 0x20, "空格");
        Add("Tab", 0x09);
        Add("Escape", 0x1B, "Esc");
        Add("Backspace", 0x08, "退格");
        Add("CapsLock", 0x14);
        Add("Insert", 0x2D);
        Add("Delete", 0x2E, "Del");
        Add("Home", 0x24);
        Add("End", 0x23);
        Add("PageUp", 0x21, "PgUp");
        Add("PageDown", 0x22, "PgDn");
        Add("Up", 0x26, "↑");
        Add("Down", 0x28, "↓");
        Add("Left", 0x25, "←");
        Add("Right", 0x27, "→");

        for (var index = 1; index <= 24; index++)
        {
            Add("F" + index, (ushort)(0x70 + index - 1));
        }

        return map;
    }
}

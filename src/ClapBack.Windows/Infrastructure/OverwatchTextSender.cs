using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;
using ClapBack.Core.Services;

namespace ClapBack.Windows.Infrastructure;

/// <summary>
/// 通过剪贴板 + 模拟按键把识别文本送进游戏聊天框。
///
/// <para>这是整条链路里唯一会「向别的程序注入输入」的地方，因此做了硬性限制：</para>
/// <list type="bullet">
/// <item>只有前台窗口属于 <c>TargetWindowProcessNames</c> 白名单里的进程才允许注入；
/// 否则直接抛错，<b>一个键都不发</b>。没有这个限制，误触发的录音就可能把文本
/// 打进终端、浏览器地址栏等任意位置（在终端里等同于执行命令）。</item>
/// <item><c>SetForegroundWindow</c> 的返回值必须为真，且每次按键前都重新校验前台窗口，
/// 防止切换窗口后把按键打到别处。</item>
/// <item>恢复剪贴板前先确认剪贴板里仍是本次写入的内容，避免覆盖用户刚复制的新东西。</item>
/// </list>
/// </summary>
public sealed class OverwatchTextSender : ITextSender, ITargetWindowAware
{
    private readonly WindowsClipboardService _clipboard;
    private readonly Func<ushort> _chatOpenKey;
    private readonly Func<ushort> _sendKey;
    private readonly Func<bool> _restoreClipboard;
    private readonly Func<ClapBackSettings> _settings;
    private IntPtr _targetWindow;

    public OverwatchTextSender(
        WindowsClipboardService clipboard,
        Func<ushort> chatOpenKey,
        Func<ushort> sendKey,
        Func<bool> restoreClipboard,
        Func<ClapBackSettings> settings)
    {
        _clipboard = clipboard;
        _chatOpenKey = chatOpenKey;
        _sendKey = sendKey;
        _restoreClipboard = restoreClipboard;
        _settings = settings;
    }

    public void CaptureCurrentTarget()
    {
        var window = GetForegroundWindow();
        // 记录时就校验一次：不允许把一个非目标窗口存起来留待稍后注入。
        _targetWindow = IsAllowed(window) ? window : IntPtr.Zero;
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("发送文本不能为空", nameof(text));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var settings = _settings();
        var allowedNames = ParseAllowedProcesses(settings.TargetWindowProcessNames);
        if (settings.RequireTargetWindowMatch && allowedNames.Count == 0)
        {
            throw new InvalidOperationException(
                "目标窗口白名单为空，拒绝发送文本。请在设置 TargetWindowProcessNames 中填入允许注入的进程名，例如 Overwatch,Overwatch2。");
        }

        if (_targetWindow != IntPtr.Zero)
        {
            await ActivateAsync(_targetWindow, cancellationToken).ConfigureAwait(false);
        }

        // 真正要注入的是「此刻」的前台窗口：切换窗口后必须重新校验。
        var current = EnsureForeground(allowedNames, settings.RequireTargetWindowMatch);

        var snapshot = await _clipboard.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var sentText = text;
        try
        {
            await _clipboard.SetTextAsync(sentText, cancellationToken).ConfigureAwait(false);

            // 写进剪贴板后先给游戏一侧留出读取时间，再开始敲键。
            await Task.Delay(60, cancellationToken).ConfigureAwait(false);

            // 固定三步：打开聊天 → Ctrl+V 粘贴 → 按发送键发出。
            // 两个按键都由设置决定，目标进程是守望先锋时每一步的等待更长，见 ChatSendPlanner。
            var plan = ChatSendPlanner.Plan(_chatOpenKey(), _sendKey(), DescribeProcess(current));
            foreach (var step in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 每一步之前都重新确认前台窗口：中途被切走就立刻停手，不把按键打到别处。
                EnsureForeground(allowedNames, settings.RequireTargetWindowMatch);

                KeyboardInput.Press(step.VirtualKey, step.Modifier);
                await Task.Delay(step.WaitAfterMs, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_restoreClipboard())
            {
                await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
                await RestoreClipboardSafelyAsync(snapshot, sentText).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 只有剪贴板内容仍是本次写入的文本时才恢复快照；
    /// 用户在发送间隙复制了新内容时就跳过，避免把新内容冲掉。
    /// 恢复失败不影响已经成功的发送。
    /// </summary>
    private async Task RestoreClipboardSafelyAsync(ClipboardSnapshot snapshot, string sentText)
    {
        try
        {
            var current = await _clipboard.GetTextAsync(CancellationToken.None).ConfigureAwait(false);
            if (!string.Equals(current, sentText, StringComparison.Ordinal))
            {
                return;
            }

            await _clipboard.RestoreAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ExternalException)
        {
            // 剪贴板被其它进程锁定：恢复是尽力而为，不能把已经发出的文本算作失败。
            // （COMException 派生自 ExternalException，已被本分支覆盖。）
        }
    }

    /// <summary>
    /// 把目标窗口切到前台。全屏独占的游戏经常第一次 <c>SetForegroundWindow</c> 不生效，
    /// 所以先还原窗口再重试几次，最后以「前台窗口确实是它」为准，而不是看 API 的返回值。
    /// </summary>
    private static async Task ActivateAsync(IntPtr window, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (GetForegroundWindow() == window)
            {
                return;
            }

            ShowWindow(window, SwRestore);
            SetForegroundWindow(window);
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }

        if (GetForegroundWindow() != window)
        {
            throw new InvalidOperationException(
                "无法切换到目标窗口，已取消发送。全屏独占的游戏可能拒绝被切到前台，可改成「无边框窗口」再试。");
        }
    }

    /// <summary>确认前台窗口属于白名单，返回该窗口句柄；不属于就抛错，一个键都不发。</summary>
    private static IntPtr EnsureForeground(IReadOnlyCollection<string> allowedNames, bool requireMatch)
    {
        var window = GetForegroundWindow();
        if (!IsAllowed(window, allowedNames, requireMatch))
        {
            throw new InvalidOperationException(
                $"前台窗口不是目标进程，当前：{DescribeProcess(window)}。已取消发送，未注入任何按键。");
        }

        return window;
    }

    private static bool IsAllowed(IntPtr window) =>
        IsAllowed(window, null, requireMatch: true);

    private static bool IsAllowed(IntPtr window, IReadOnlyCollection<string>? allowedNames, bool requireMatch)
    {
        if (!requireMatch)
        {
            return window != IntPtr.Zero;
        }

        if (window == IntPtr.Zero)
        {
            return false;
        }

        var name = DescribeProcess(window);
        var names = allowedNames ?? ParseAllowedProcesses(null);
        if (names.Count == 0)
        {
            return false;
        }

        return names.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyCollection<string> ParseAllowedProcesses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split(new[] { ',', ';', '，', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>返回窗口所属进程名（不含 .exe）；取不到时返回空字符串。</summary>
    private static string DescribeProcess(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return string.Empty;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // 进程在校验期间退出。
            return string.Empty;
        }
        catch (Win32Exception)
        {
            // 无权查询该进程。
            return string.Empty;
        }
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

internal static class KeyboardInput
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint Scancode = 0x0008;
    private const uint ExtendedKey = 0x0001;
    private const uint MapVkToVsc = 0;

    /// <summary>
    /// 按下到松开的间隔。游戏按帧采样，同一个消息批次里按下立刻松开会整帧漏掉，
    /// 所以这里让键「按住」一小会儿再松开。
    /// </summary>
    private const int HoldMs = 22;

    /// <summary>
    /// 按一次单键或组合键：<c>Press(0x0D)</c> 是回车，<c>Press(0x56, 0x11)</c> 是 Ctrl+V。
    /// </summary>
    /// <param name="virtualKey">主键的虚拟键码。</param>
    /// <param name="modifier">要同时按住的修饰键；<c>0</c> 表示没有。</param>
    public static void Press(ushort virtualKey, ushort modifier = 0)
    {
        if (modifier != 0)
        {
            Send(modifier, keyUp: false);
        }

        Send(virtualKey, keyUp: false);
        Thread.Sleep(HoldMs);
        Send(virtualKey, keyUp: true);

        if (modifier != 0)
        {
            Send(modifier, keyUp: true);
        }
    }

    private static void Send(ushort virtualKey, bool keyUp)
    {
        // 完整的硬件事件要带扫描码：守望先锋这类用 Raw Input 读键盘的游戏只看扫描码，
        // 只填虚拟键码的 SendInput 它收不到。
        var scancode = (ushort)MapVirtualKey(virtualKey, MapVkToVsc);
        var flags = keyUp ? KeyUp : 0u;
        if (scancode != 0)
        {
            flags |= Scancode;
            if ((scancode & 0xFF00) == 0xE000)
            {
                // 0xE0 前缀的扩展键（例如小键盘回车）要额外标记，扫描码只取低字节。
                flags |= ExtendedKey;
                scancode &= 0x00FF;
            }
        }

        var input = new Input
        {
            Type = InputKeyboard,
            Keyboard = new KeyboardInputData
            {
                VirtualKey = virtualKey,
                ScanCode = scancode,
                Flags = flags,
                Time = 0,
                // 不带 GetMessageExtraInfo()：注入事件应带 0，免得继承别的工具留下的标记。
                ExtraInfo = IntPtr.Zero
            }
        };

        if (SendInput(1, new[] { input }, Marshal.SizeOf<Input>()) == 0)
        {
            throw new InvalidOperationException(
                $"Windows 输入发送失败（错误码 {Marshal.GetLastWin32Error()}）。");
        }
    }

    /// <summary>
    /// 与 Win32 的 <c>INPUT</c> 逐字节对齐：x64 上是 40 字节。
    /// <para>
    /// 这里原来只放了键盘成员，结构体只有 8 + 24 = 32 字节；而 <c>SendInput</c> 要求 cbSize
    /// 恰好等于 <c>sizeof(INPUT)</c>（x64 = 40），于是它每次都返回 0、代码抛「输入发送失败」——
    /// 自动发送其实一个键都没发出去过。显式对齐到 40 字节后按键才真正生效。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}

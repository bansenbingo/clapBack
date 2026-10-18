using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;

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
    private readonly Func<bool> _restoreClipboard;
    private readonly Func<ClapBackSettings> _settings;
    private IntPtr _targetWindow;

    public OverwatchTextSender(
        WindowsClipboardService clipboard,
        Func<ushort> chatOpenKey,
        Func<bool> restoreClipboard,
        Func<ClapBackSettings> settings)
    {
        _clipboard = clipboard;
        _chatOpenKey = chatOpenKey;
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
                "目标窗口白名单为空，拒绝发送文本。请在设置 TargetWindowProcessNames 中填入允许注入的进程名（例如 Overwatch,Overwatch2）。");
        }

        if (_targetWindow != IntPtr.Zero && !SetForegroundWindow(_targetWindow))
        {
            throw new InvalidOperationException("无法切换到目标窗口，已取消发送。");
        }

        await Task.Delay(100, cancellationToken).ConfigureAwait(false);

        // 真正要注入的是「此刻」的前台窗口：切换窗口后必须重新校验。
        var current = GetForegroundWindow();
        if (!IsAllowed(current, allowedNames, settings.RequireTargetWindowMatch))
        {
            throw new InvalidOperationException(
                $"前台窗口不是目标进程（当前：{DescribeProcess(current)}），已取消发送，未注入任何按键。");
        }

        var snapshot = await _clipboard.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var sentText = text;
        try
        {
            await _clipboard.SetTextAsync(sentText, cancellationToken).ConfigureAwait(false);
            KeyboardInput.Press(_chatOpenKey());
            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            KeyboardInput.Press(0x11, 0x56); // Ctrl+V
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            KeyboardInput.Press(0x0D); // Enter
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

    public static void Press(params ushort[] keys)
    {
        foreach (var key in keys)
        {
            Send(key, false);
        }

        for (var index = keys.Length - 1; index >= 0; index--)
        {
            Send(keys[index], true);
        }
    }

    private static void Send(ushort key, bool keyUp)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Keyboard = new KeyboardInputData
            {
                VirtualKey = key,
                ScanCode = 0,
                Flags = keyUp ? KeyUp : 0,
                Time = 0,
                ExtraInfo = GetMessageExtraInfo()
            }
        };
        if (SendInput(1, new[] { input }, Marshal.SizeOf<Input>()) == 0)
        {
            throw new InvalidOperationException("Windows 输入发送失败");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
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
    private static extern IntPtr GetMessageExtraInfo();
}

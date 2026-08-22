using System.Runtime.InteropServices;
using ClapBack.Core.Abstractions;

namespace ClapBack.Windows.Infrastructure;

public sealed class OverwatchTextSender : ITextSender, ITargetWindowAware
{
    private readonly WindowsClipboardService _clipboard;
    private readonly Func<ushort> _chatOpenKey;
    private readonly Func<bool> _restoreClipboard;
    private IntPtr _targetWindow;

    public OverwatchTextSender(
        WindowsClipboardService clipboard,
        Func<ushort> chatOpenKey,
        Func<bool> restoreClipboard)
    {
        _clipboard = clipboard;
        _chatOpenKey = chatOpenKey;
        _restoreClipboard = restoreClipboard;
    }

    public void CaptureCurrentTarget() => _targetWindow = GetForegroundWindow();

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("发送文本不能为空", nameof(text));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_targetWindow != IntPtr.Zero)
        {
            SetForegroundWindow(_targetWindow);
        }

        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        var snapshot = await _clipboard.CaptureAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _clipboard.SetTextAsync(text, cancellationToken).ConfigureAwait(false);
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
                await _clipboard.RestoreAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
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

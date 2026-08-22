using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MessageBox = System.Windows.MessageBox;

namespace ClapBack.Windows;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0xCAFE;
    private const uint WmHotkey = 0x0312;
    private readonly uint _hotkey;
    private HwndSource? _source;
    private IntPtr _handle;

    public MainWindow(ushort hotkey)
    {
        _hotkey = hotkey;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    public event EventHandler? HotkeyPressed;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
        if (!RegisterHotKey(_handle, HotkeyId, 0, _hotkey))
        {
            MessageBox.Show("CapsLock 热键注册失败，请在设置中选择其他热键。", "ClapBack", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public bool TrySetHotkey(ushort hotkey)
    {
        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        UnregisterHotKey(_handle, HotkeyId);
        return RegisterHotKey(_handle, HotkeyId, 0, hotkey);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        UnregisterHotKey(_handle, HotkeyId);
        _source?.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClapBack.Core.Models;

namespace ClapBack.Windows;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0xCAFE;
    private const int CandidateHotkeyId = HotkeyId + 1;
    private const int WmHotkey = 0x0312;
    private HotkeyBinding _hotkey;
    private int _registeredId = HotkeyId;
    private bool _registered;
    private bool _allowClose;
    private HwndSource? _source;
    private IntPtr _handle;
    private ClapBackState _state;
    private bool _preparing = true;
    private int _maxSeconds = 30;
    private readonly Stopwatch _recordingClock = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public MainWindow(HotkeyBinding hotkey)
    {
        _hotkey = hotkey;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += OnClosed;
        _timer.Tick += (_, _) => RecordingTimeText.Text = $"已录音 {_recordingClock.Elapsed.TotalSeconds:0.0} 秒 / {_maxSeconds} 秒";
    }

    public event EventHandler? HotkeyPressed;
    public event EventHandler? CancelRequested;
    public event EventHandler? CopyRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? PrepareRequested;
    public event EventHandler? ExitRequested;
    public bool HotkeyRegistered => _registered;

    public void ApplySettings(ClapBackSettings settings)
    {
        _maxSeconds = Math.Clamp(settings.MaxRecordingSeconds, 1, 300);
        HotkeyText.Text = $"{_hotkey.Name} 开始 / 结束录音，最长 {_maxSeconds} 秒";
        ModeText.Text = settings.TranscriptionMode switch
        {
            TranscriptionMode.CloudOnly => "仅云端识别：录音将上传到设置中的服务。",
            TranscriptionMode.LocalFirst => "本地优先：本地失败时上传云端。",
            _ => "仅本地识别，默认复制文字后在游戏中按 Ctrl+V。"
        };
        if (settings.AutoSend) ModeText.Text += " 已开启游戏自动发送。";
        if (!_registered) MessageText.Text = "全局热键不可用，可在设置中更换；录音按钮仍可使用。";
    }

    public void UpdateState(ClapBackState state, string? transcript, string? error)
    {
        if (state == ClapBackState.Recording && _state != state)
        {
            _recordingClock.Restart();
            _timer.Start();
        }
        if (state != ClapBackState.Recording) { _timer.Stop(); _recordingClock.Stop(); }
        _state = state;
        StateText.Text = state switch
        {
            ClapBackState.Recording => "正在听你说",
            ClapBackState.Transcribing => "正在识别",
            ClapBackState.Sending => "正在发送到游戏",
            ClapBackState.Success => "识别完成",
            ClapBackState.Error => "这次没能完成",
            _ => "准备录音"
        };
        RecordingTimeText.Visibility = state == ClapBackState.Recording ? Visibility.Visible : Visibility.Collapsed;
        if (state == ClapBackState.Recording) TranscriptBox.Clear();
        if (!string.IsNullOrEmpty(transcript)) TranscriptBox.Text = transcript;
        CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(TranscriptBox.Text);
        if (!string.IsNullOrWhiteSpace(error)) MessageText.Text = error;
        UpdateControls();
    }

    public void SetPreparing(bool preparing, string detail)
    {
        _preparing = preparing;
        EnvironmentText.Text = detail;
        UpdateControls();
    }

    public void SetMessage(string message) => MessageText.Text = message;
    public string Transcript => TranscriptBox.Text;

    private void UpdateControls()
    {
        var busy = _state is ClapBackState.Recording or ClapBackState.Transcribing or ClapBackState.Sending;
        RecordButton.IsEnabled = !_preparing && _state is not (ClapBackState.Transcribing or ClapBackState.Sending);
        RecordButton.Content = _state == ClapBackState.Recording ? "结束并识别" : "开始录音";
        SettingsButton.IsEnabled = !busy;
        PrepareButton.IsEnabled = !_preparing && !busy;
        CancelButton.Content = _preparing ? "取消准备" : "取消本次语音";
        CancelButton.Visibility = busy || _preparing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
        _registered = RegisterHotKey(_handle, _registeredId, _hotkey.Modifiers | 0x4000, _hotkey.VirtualKey);
    }

    public bool TrySetHotkey(HotkeyBinding hotkey)
    {
        if (_handle == IntPtr.Zero) return false;
        if (_registered && hotkey.VirtualKey == _hotkey.VirtualKey && hotkey.Modifiers == _hotkey.Modifiers) return true;
        var candidateId = _registeredId == HotkeyId ? CandidateHotkeyId : HotkeyId;
        if (!RegisterHotKey(_handle, candidateId, hotkey.Modifiers | 0x4000, hotkey.VirtualKey)) return false;
        if (_registered) UnregisterHotKey(_handle, _registeredId);
        _registeredId = candidateId;
        _hotkey = hotkey;
        _registered = true;
        return true;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void CloseForExit() { _allowClose = true; Close(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_registered) UnregisterHotKey(_handle, _registeredId);
        _source?.RemoveHook(WndProc);
    }
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == _registeredId)
        { handled = true; HotkeyPressed?.Invoke(this, EventArgs.Empty); }
        return IntPtr.Zero;
    }
    private void RecordClick(object sender, RoutedEventArgs e) => HotkeyPressed?.Invoke(this, EventArgs.Empty);
    private void CancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
    private void CopyClick(object sender, RoutedEventArgs e) => CopyRequested?.Invoke(this, EventArgs.Empty);
    private void SettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void PrepareClick(object sender, RoutedEventArgs e) => PrepareRequested?.Invoke(this, EventArgs.Empty);
    private void HideClick(object sender, RoutedEventArgs e) => Hide();
    private void ExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

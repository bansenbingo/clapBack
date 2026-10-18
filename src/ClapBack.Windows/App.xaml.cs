using System.Drawing;
using System.Net.Http;
using System.Windows;
using ClapBack.Core;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Controller;
using ClapBack.Core.Models;
using ClapBack.Stt;
using ClapBack.Stt.Audio;
using ClapBack.Windows.Infrastructure;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace ClapBack.Windows;

public partial class App : System.Windows.Application
{
    private readonly JsonSettingsStore _settingsStore = new();
    private readonly WindowsCredentialStore _credentialStore = new();
    private readonly WindowsMicrophoneCatalog _microphones = new();
    private readonly WindowsClipboardService _clipboard = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private ClapBackSettings _settings = new();
    private Forms.NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private VoiceInputController? _controller;
    private SettingsWindow? _settingsWindow;
    private Task _preparation = Task.CompletedTask;
    private CancellationTokenSource? _preparationCancellation;
    private bool _preparing;
    private bool _ready;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            AppPaths.EnsureDirectories();
            _settings = await _settingsStore.LoadAsync(_lifetime.Token);
            if (!HotkeyBinding.TryParse(_settings.Hotkey, out var hotkey))
            { HotkeyBinding.TryParse("CapsLock", out hotkey); _settings = _settings with { Hotkey = hotkey.Name }; }
            _mainWindow = new MainWindow(hotkey);
            var recorder = new WavAudioRecorder(() => _microphones.ResolveIndex(_settings.MicrophoneDeviceId) ?? 0);
            var sender = new OverwatchTextSender(_clipboard,
                () => _settings.ChatOpenKey.Equals("F2", StringComparison.OrdinalIgnoreCase) ? (ushort)0x71 : (ushort)0x0D,
                () => false, () => _settings);
            var cloud = new WhisperCompatibleTranscriptionProvider(_http, () => _settings,
                token => _credentialStore.GetAsync("ClapBack.Stt.ApiKey", token));
            var local = new LocalSenseVoiceTranscriptionProvider(() => _settings);
            var provider = new CompositeTranscriptionProvider(local, cloud, () => _settings,
                message => Dispatcher.BeginInvoke(() => ShowMessage(message, true)));
            _controller = new VoiceInputController(recorder, provider, sender, _clipboard, () => _settings);
            _controller.StateChanged += (_, state) => Dispatcher.BeginInvoke(() =>
            {
                if (_exiting) return;
                _mainWindow.UpdateState(state, _controller.LastTranscript, _controller.LastError);
                if (_notifyIcon is not null) _notifyIcon.Text = state == ClapBackState.Recording
                    ? $"ClapBack：录音中，再按 {_settings.Hotkey}" : "ClapBack：" + state;
            });
            _controller.NotificationRaised += (_, message) => Dispatcher.BeginInvoke(() => { if (!_exiting) ShowMessage(message, true); });
            _mainWindow.HotkeyPressed += async (_, _) => await HandleRecordingAsync();
            _mainWindow.CancelRequested += async (_, _) => await CancelRecordingAsync();
            _mainWindow.CopyRequested += async (_, _) => await CopyResultAsync();
            _mainWindow.SettingsRequested += async (_, _) => await OpenSettingsAsync();
            _mainWindow.PrepareRequested += (_, _) => StartPreparation();
            _mainWindow.ExitRequested += async (_, _) => await ShutdownApplicationAsync();
            MainWindow = _mainWindow;
            CreateTrayIcon();
            _mainWindow.Show();
            _mainWindow.ApplySettings(_settings);
            _mainWindow.UpdateState(ClapBackState.Idle, null, null);
            StartPreparation();
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法启动 ClapBack：" + ex.Message + "\n请把程序放在有写入权限的文件夹中。", "ClapBack", MessageBoxButton.OK, MessageBoxImage.Error);
            _notifyIcon?.Dispose();
            Shutdown(1);
        }
    }

    private bool Busy => _controller?.State is ClapBackState.Recording or ClapBackState.Transcribing or ClapBackState.Sending;
    private async Task HandleRecordingAsync()
    {
        if (_exiting) return;
        if (_settingsWindow is not null) { ShowMessage("请先关闭设置窗口。"); return; }
        if (_preparing) { ShowMessage("正在准备识别环境，请稍候。"); return; }
        if (!_ready && _settings.TranscriptionMode != TranscriptionMode.LocalFirst)
        { _mainWindow?.ShowFromTray(); ShowMessage("识别环境尚未就绪，请查看环境面板或调整设置。"); return; }
        try { if (_controller is not null) await _controller.HandleHotkeyAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowMessage(ex.Message); }
    }
    private async Task CancelRecordingAsync()
    {
        if (_preparing) { _preparationCancellation?.Cancel(); return; }
        try { if (_controller is not null) await _controller.CancelAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowMessage("取消失败：" + ex.Message); }
    }
    private async Task CopyResultAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_mainWindow?.Transcript)) return;
            await _clipboard.SetTextAsync(_mainWindow.Transcript, _lifetime.Token);
            ShowMessage("已复制到剪贴板，在聊天框按 Ctrl+V 粘贴。");
        }
        catch (Exception ex) { ShowMessage("复制失败，文字已保留，请重试：" + ex.Message); }
    }
    private void StartPreparation()
    {
        if (_preparing || Busy || _exiting) return;
        _preparationCancellation?.Dispose();
        _preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _preparation = ProvisionEnvironmentAsync(_preparationCancellation.Token);
    }
    private async Task ProvisionEnvironmentAsync(CancellationToken cancellationToken)
    {
        _preparing = true;
        _mainWindow?.SetPreparing(true, "正在检查所需组件…");
        try
        {
            var progress = new Progress<string>(message => { if (!_exiting && !cancellationToken.IsCancellationRequested) _mainWindow?.SetPreparing(true, message); });
            // Checking existing large models must not block the WPF dispatcher.
            var settings = _settings;
            var report = await Task.Run(() => new RuntimeProvisioner().EnsureAsync(settings, progress, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _ready = report.IsReady;
            var summary = string.Join(Environment.NewLine, report.Steps.Select(step => $"{step.Name}：{step.Detail}"));
            _mainWindow?.SetPreparing(false, summary);
            if (_settings.TranscriptionMode == TranscriptionMode.CloudOnly)
                ShowMessage("云端模式已选定，请确认服务 URL、模型和 API Key。");
            else if (!_ready && _settings.TranscriptionMode == TranscriptionMode.LocalFirst)
                ShowMessage("本地环境未就绪；仍可使用云端回退，录音会上传到配置的服务。");
            else ShowMessage(_ready ? "识别环境已就绪，可以开始录音。" : "识别环境未就绪，查看进度详情并在设置中调整后重试。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ready = false;
            if (!_exiting)
            {
                _mainWindow?.SetPreparing(false, "准备已取消，可修改设置或重新检测。");
                ShowMessage("已取消环境准备。");
            }
        }
        catch (Exception ex)
        {
            _ready = false;
            _mainWindow?.SetPreparing(false, "环境准备失败：" + ex.Message);
            ShowMessage("环境准备失败，修改设置后可重新检测。");
        }
        finally { _preparing = false; }
    }
    private void ShowMessage(string message, bool notify = false)
    {
        if (_exiting) return;
        _mainWindow?.SetMessage(message);
        if (!notify) return;
        try { _notifyIcon?.ShowBalloonTip(2000, "ClapBack", message, Forms.ToolTipIcon.Info); }
        catch { /* Notifications do not block recording. */ }
    }
    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => _mainWindow?.ShowFromTray());
        menu.Items.Add("设置与字库", null, async (_, _) => await OpenSettingsAsync());
        menu.Items.Add("退出", null, async (_, _) => await ShutdownApplicationAsync());
        _notifyIcon = new Forms.NotifyIcon { Icon = SystemIcons.Application, Visible = true,
            Text = "ClapBack", ContextMenuStrip = menu };
        _notifyIcon.DoubleClick += (_, _) => _mainWindow?.ShowFromTray();
    }
    private async Task OpenSettingsAsync()
    {
        if (_exiting) return;
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        if (Busy) { _mainWindow?.ShowFromTray(); ShowMessage("请先结束当前录音。"); return; }
        try
        {
            // 打开设置需要先让出下载/校验，否则用户改不了正在进行的端点。
            var preparationPaused = _preparing;
            if (preparationPaused)
            {
                _preparationCancellation?.Cancel();
                await _preparation;
                if (_exiting || _settingsWindow is not null) return;
            }
            _mainWindow?.ShowFromTray();
            var key = await _credentialStore.GetAsync("ClapBack.Stt.ApiKey", _lifetime.Token) ?? string.Empty;
            _settingsWindow = new SettingsWindow(_settings, _microphones, key, SaveSettingsAsync) { Owner = _mainWindow };
            var saved = _settingsWindow.ShowDialog() == true;
            _settingsWindow = null;
            // 无论是否保存都要恢复准备：取消对话框也不能让环境停在被中断的状态。
            if (saved || preparationPaused) StartPreparation();
        }
        catch (Exception ex) { _settingsWindow = null; ShowMessage("无法打开设置：" + ex.Message); }
    }
    private async Task SaveSettingsAsync(ClapBackSettings candidate, string apiKey)
    {
        if (_exiting) throw new OperationCanceledException();
        if (!HotkeyBinding.TryParse(candidate.Hotkey, out var hotkey) || _mainWindow?.TrySetHotkey(hotkey) != true)
            throw new InvalidOperationException("热键已被占用，设置尚未应用，请选择其他按键。");
        try
        {
            if (string.IsNullOrWhiteSpace(apiKey)) await _credentialStore.DeleteAsync("ClapBack.Stt.ApiKey", _lifetime.Token);
            else await _credentialStore.SetAsync("ClapBack.Stt.ApiKey", apiKey, _lifetime.Token);
            await _settingsStore.SaveAsync(candidate, _lifetime.Token);
            _settings = candidate;
            _mainWindow.ApplySettings(_settings);
            ShowMessage("设置和字库已保存。");
        }
        catch
        {
            if (HotkeyBinding.TryParse(_settings.Hotkey, out var previous)) _mainWindow?.TrySetHotkey(previous);
            throw;
        }
    }
    private async Task ShutdownApplicationAsync()
    {
        if (_exiting) return;
        if (_settingsWindow is not null) { _settingsWindow.Activate(); ShowMessage("请先关闭设置窗口再退出。"); return; }
        _exiting = true;
        _lifetime.Cancel();
        try
        {
            if (_controller is not null) await _controller.CancelAsync();
            await _preparation;
        }
        catch (OperationCanceledException) { }
        finally
        {
            _notifyIcon?.Dispose();
            _mainWindow?.CloseForExit();
            _http.Dispose();
            _lifetime.Dispose();
            Shutdown();
        }
    }
}

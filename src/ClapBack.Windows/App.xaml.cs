using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Security.Principal;
using System.Windows;
using ClapBack.Core;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Controller;
using ClapBack.Core.Models;
using ClapBack.Core.Services;
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
    private OverlayWindow? _overlay;
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
            // 默认以管理员身份启动：当前进程没有提权就用 runas 重新拉起自己。
            // 提权失败（用户点了「否」）不算错误，继续以普通权限运行。
            if (_settings.RunAsAdministrator
                && !e.Args.Contains("--elevated", StringComparer.OrdinalIgnoreCase)
                && !IsElevated()
                && TryRelaunchElevated())
            {
                Shutdown();
                return;
            }

            if (!HotkeyBinding.TryParse(_settings.Hotkey, out var hotkey))
            { HotkeyBinding.TryParse("CapsLock", out hotkey); _settings = _settings with { Hotkey = hotkey.Name }; }
            _mainWindow = new MainWindow(hotkey);
            _overlay = new OverlayWindow();
            // 拖动气泡后把新位置写回设置，下次仍从那里弹出。
            _overlay.PositionChanged += (_, position) => { _ = SaveOverlayPositionAsync(position); };
            var recorder = new WavAudioRecorder(() => _microphones.ResolveIndex(_settings.MicrophoneDeviceId) ?? 0);
            var sender = new OverwatchTextSender(_clipboard,
                () => ChatSendPlanner.ChatOpenVirtualKey(_settings.ChatOpenKey),
                () => ChatSendPlanner.SendVirtualKey(_settings.SendKey),
                () => _settings.RestoreClipboard, () => _settings);
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
                UpdateOverlay(state, _controller.LastTranscript, _controller.LastError);
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
    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // 拿不到令牌就当没提权：宁可多问一次 UAC，也不要假装自己已经是管理员。
            return false;
        }
    }

    private static bool TryRelaunchElevated()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            // 用固定参数做标记，避免提权后的新进程再次把自己拉起来（无限循环）。
            startInfo.ArgumentList.Add("--elevated");
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED：用户在 UAC 上点了「否」。提权只是可选项，不该挡住程序。
            MessageBox.Show("已取消管理员权限，ClapBack 将以普通权限继续运行，识别功能不受影响。",
                "ClapBack", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法以管理员身份重新启动：" + ex.Message + "\n将以普通权限运行。",
                "ClapBack", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>托盘图标直接用 exe 自带的图标，避免「托盘一个样、任务栏另一个样」。</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                var icon = Icon.ExtractAssociatedIcon(executable);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
            // 图标提取失败不能连累托盘出现。
        }

        return SystemIcons.Application;
    }

    private void UpdateOverlay(ClapBackState state, string? transcript, string? error)
    {
        if (_exiting || _overlay is null)
        {
            return;
        }

        if (!_settings.ShowOverlay)
        {
            _overlay.HideNow();
            return;
        }

        switch (state)
        {
            case ClapBackState.Recording:
                _overlay.ShowMessage("正在听你说…", _settings);
                break;
            case ClapBackState.Transcribing:
                _overlay.ShowMessage("正在识别…", _settings);
                break;
            case ClapBackState.Sending:
                _overlay.ShowMessage("正在发送到游戏…", _settings);
                break;
            case ClapBackState.Success when !string.IsNullOrWhiteSpace(transcript):
                // 这是气泡的主用途：把识别结果直接摆在游戏画面上。
                _overlay.ShowMessage(transcript, _settings);
                break;
            case ClapBackState.Error when !string.IsNullOrWhiteSpace(error):
                _overlay.ShowMessage(error, _settings);
                break;
        }
    }

    private void ShowOverlayPreview(double fontSize, double opacity, int durationSeconds)
    {
        if (_exiting || _overlay is null)
        {
            return;
        }

        _overlay.ShowMessage("气泡预览：识别结果会这样显示", _settings with
        {
            OverlayFontSize = fontSize,
            OverlayOpacity = opacity,
            OverlayDurationSeconds = durationSeconds,
        }, showDragHint: true);
    }

    private void ResetOverlayPosition()
    {
        _overlay?.HideNow();
        _ = SaveOverlayPositionAsync(new OverlayPosition(double.NaN, double.NaN));
    }

    private async Task SaveOverlayPositionAsync(OverlayPosition position)
    {
        // 复位时传进来的是 NaN，落盘要写成 null（JSON 不允许 NaN）。
        _settings = _settings with
        {
            OverlayLeft = double.IsNaN(position.Left) ? null : position.Left,
            OverlayTop = double.IsNaN(position.Top) ? null : position.Top,
        };
        try
        {
            if (!_exiting)
            {
                await _settingsStore.SaveAsync(_settings, _lifetime.Token).ConfigureAwait(true);
            }
        }
        catch
        {
            // 位置记不住只影响下次弹出的位置，不影响识别本身。
        }
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => _mainWindow?.ShowFromTray());
        menu.Items.Add("设置与字库", null, async (_, _) => await OpenSettingsAsync());
        menu.Items.Add("退出", null, async (_, _) => await ShutdownApplicationAsync());
        _notifyIcon = new Forms.NotifyIcon { Icon = LoadAppIcon(), Visible = true,
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
            _settingsWindow = new SettingsWindow(_settings, _microphones, key, SaveSettingsAsync,
                ShowOverlayPreview, ResetOverlayPosition) { Owner = _mainWindow };
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
        // 气泡位置是直接拖动写进内存设置的，设置对话框里没有对应输入框；保存时用最新值覆盖，
        // 否则「开着设置对话框拖气泡 → 点保存」会把刚拖好的位置又冲回旧值。
        candidate = candidate with { OverlayLeft = _settings.OverlayLeft, OverlayTop = _settings.OverlayTop };
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
            _overlay?.HideNow();
            _mainWindow?.CloseForExit();
            _http.Dispose();
            _lifetime.Dispose();
            Shutdown();
        }
    }
}

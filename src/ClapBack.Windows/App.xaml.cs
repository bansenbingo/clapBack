using System.Drawing;
using System.Net.Http;
using System.Windows;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Controller;
using ClapBack.Core.Models;
using ClapBack.Stt;
using ClapBack.Windows.Infrastructure;
using Forms = System.Windows.Forms;

namespace ClapBack.Windows;

public partial class App : System.Windows.Application
{
    private readonly JsonSettingsStore _settingsStore = new();
    private readonly WindowsCredentialStore _credentialStore = new();
    private ClapBackSettings _settings = new();
    private Forms.NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private VoiceInputController? _controller;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = await _settingsStore.LoadAsync();
        _mainWindow = new MainWindow(ParseVirtualKey(_settings.Hotkey));
        _mainWindow.Show();
        _mainWindow.Hide();

        var recorder = new WavAudioRecorder(() => _settings.InputDeviceNumber);
        var sender = new OverwatchTextSender(
            new WindowsClipboardService(),
            () => ParseVirtualKey(_settings.ChatOpenKey),
            () => _settings.RestoreClipboard);
        var cloudProvider = new WhisperCompatibleTranscriptionProvider(
            new HttpClient { Timeout = TimeSpan.FromSeconds(45) },
            () => _settings,
            cancellationToken => _credentialStore.GetAsync("ClapBack.Stt.ApiKey", cancellationToken));
        var localProvider = new LocalSenseVoiceTranscriptionProvider(() => _settings);
        var provider = new CompositeTranscriptionProvider(localProvider, cloudProvider, () => _settings);

        _controller = new VoiceInputController(recorder, provider, sender, () => _settings);
        _controller.StateChanged += ControllerOnStateChanged;
        _controller.NotificationRaised += ControllerOnNotificationRaised;
        _mainWindow.HotkeyPressed += (_, _) => _ = _controller.HandleHotkeyAsync();

        CreateTrayIcon();
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("设置", null, (_, _) => OpenSettings());
        menu.Items.Add("退出", null, (_, _) => ShutdownApplication());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = $"ClapBack：按 {_settings.Hotkey} 开始录音",
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();
    }

    private void ControllerOnStateChanged(object? sender, ClapBackState state)
    {
        var text = state switch
        {
            ClapBackState.Recording => $"ClapBack：录音中，再按 {_settings.Hotkey}",
            ClapBackState.Transcribing => "ClapBack：识别中",
            ClapBackState.Sending => "ClapBack：发送中",
            ClapBackState.Error => "ClapBack：发生错误",
            _ => $"ClapBack：按 {_settings.Hotkey} 开始录音"
        };
        Dispatcher.Invoke(() =>
        {
            if (_notifyIcon is not null)
            {
                _notifyIcon.Text = text;
            }
        });
    }

    private void ControllerOnNotificationRaised(object? sender, string message)
    {
        Dispatcher.Invoke(() => _notifyIcon?.ShowBalloonTip(1800, "ClapBack", message, Forms.ToolTipIcon.Info));
    }

    private async void OpenSettings()
    {
        var window = new SettingsWindow(_settings, _settingsStore, _credentialStore)
        {
            Owner = _mainWindow
        };
        if (window.ShowDialog() == true)
        {
            _settings = window.Settings;
            await _settingsStore.SaveAsync(_settings);
            var hotkeyUpdated = _mainWindow?.TrySetHotkey(ParseVirtualKey(_settings.Hotkey)) == true;
            _notifyIcon?.ShowBalloonTip(
                1500,
                "ClapBack",
                hotkeyUpdated ? "设置已保存" : "设置已保存，但热键注册失败",
                hotkeyUpdated ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
        }
    }

    private void ShutdownApplication()
    {
        _notifyIcon?.Dispose();
        _mainWindow?.Close();
        Shutdown();
    }

    private static ushort ParseVirtualKey(string key)
    {
        if (key.Equals("CapsLock", StringComparison.OrdinalIgnoreCase))
        {
            return 0x14;
        }

        if (key.Equals("Enter", StringComparison.OrdinalIgnoreCase))
        {
            return 0x0D;
        }

        return key.StartsWith("F", StringComparison.OrdinalIgnoreCase)
               && ushort.TryParse(key[1..], out var functionNumber)
               && functionNumber is >= 1 and <= 24
            ? (ushort)(0x70 + functionNumber - 1)
            : (ushort)0x0D;
    }
}

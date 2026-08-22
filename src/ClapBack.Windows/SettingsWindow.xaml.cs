using System.Windows;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;

namespace ClapBack.Windows;

public partial class SettingsWindow : Window
{
    private readonly ISettingsStore _settingsStore;
    private readonly ICredentialStore _credentialStore;

    public SettingsWindow(
        ClapBackSettings settings,
        ISettingsStore settingsStore,
        ICredentialStore credentialStore)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _credentialStore = credentialStore;
        Settings = settings;
        BaseUrlTextBox.Text = settings.SttBaseUrl;
        ModelTextBox.Text = settings.Model;
        LanguageTextBox.Text = settings.Language;
        MaxSecondsTextBox.Text = settings.MaxRecordingSeconds.ToString();
        HotkeyTextBox.Text = settings.Hotkey;
        InputDeviceTextBox.Text = settings.InputDeviceNumber.ToString();
        ApiKeyBox.Password = _credentialStore.GetAsync("ClapBack.Stt.ApiKey").GetAwaiter().GetResult() ?? string.Empty;
    }

    public ClapBackSettings Settings { get; private set; }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxSecondsTextBox.Text, out var maxSeconds) || maxSeconds is < 1 or > 300)
        {
            MessageBox.Show("最长录音时长必须是 1 到 300 之间的整数。", "ClapBack", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(InputDeviceTextBox.Text, out var inputDeviceNumber) || inputDeviceNumber < 0)
        {
            MessageBox.Show("麦克风设备编号必须是 0 或更大的整数。", "ClapBack", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Settings = Settings with
        {
            SttBaseUrl = BaseUrlTextBox.Text.Trim(),
            Model = ModelTextBox.Text.Trim(),
            Language = LanguageTextBox.Text.Trim(),
            MaxRecordingSeconds = maxSeconds,
            InputDeviceNumber = inputDeviceNumber,
            Hotkey = HotkeyTextBox.Text.Trim().ToUpperInvariant()
        };
        await _settingsStore.SaveAsync(Settings);
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            await _credentialStore.DeleteAsync("ClapBack.Stt.ApiKey");
        }
        else
        {
            await _credentialStore.SetAsync("ClapBack.Stt.ApiKey", ApiKeyBox.Password);
        }

        DialogResult = true;
        Close();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

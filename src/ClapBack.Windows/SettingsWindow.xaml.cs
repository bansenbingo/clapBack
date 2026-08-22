using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
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
        AutoSendCheckBox.IsChecked = settings.AutoSend;
        RestoreClipboardCheckBox.IsChecked = settings.RestoreClipboard;
        LocalModelVariantComboBox.SelectedIndex = (int)settings.LocalModelVariant;
        TranscriptionModeComboBox.Items.Add("本地优先（失败时云端回退）");
        TranscriptionModeComboBox.Items.Add("仅本地");
        TranscriptionModeComboBox.Items.Add("仅云端");
        TranscriptionModeComboBox.SelectedIndex = (int)settings.TranscriptionMode;
        LocalRuntimeTextBox.Text = settings.LocalSttRuntimePath;
        LocalModelTextBox.Text = settings.LocalModelPath;
        LocalVadModelTextBox.Text = settings.LocalVadModelPath;
        LocalSha256TextBox.Text = settings.LocalModelSha256;
        LocalThreadsTextBox.Text = settings.LocalSttThreads.ToString();
        LocalTimeoutTextBox.Text = settings.LocalSttTimeoutSeconds.ToString();
        SelectBackend(settings.LocalSttBackend);
        BaseUrlTextBox.Text = settings.SttBaseUrl;
        CloudProtocolTextBox.Text = settings.CloudProtocol;
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

        if (!int.TryParse(LocalThreadsTextBox.Text, out var localThreads) || localThreads is < 1 or > 64)
        {
            MessageBox.Show("本地线程数必须是 1 到 64 之间的整数。", "ClapBack", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(LocalTimeoutTextBox.Text, out var localTimeout) || localTimeout is < 1 or > 300)
        {
            MessageBox.Show("本地识别超时必须是 1 到 300 之间的整数。", "ClapBack", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (TranscriptionModeComboBox.SelectedIndex is < 0 or > 2)
        {
            MessageBox.Show("请选择识别模式。", "ClapBack", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Settings = Settings with
        {
            TranscriptionMode = (TranscriptionMode)TranscriptionModeComboBox.SelectedIndex,
            LocalModelVariant = (LocalModelVariant)Math.Max(0, LocalModelVariantComboBox.SelectedIndex),
            LocalSttRuntimePath = LocalRuntimeTextBox.Text.Trim(),
            LocalModelPath = LocalModelTextBox.Text.Trim(),
            LocalVadModelPath = LocalVadModelTextBox.Text.Trim(),
            LocalModelSha256 = LocalSha256TextBox.Text.Trim(),
            LocalSttBackend = GetSelectedBackend(),
            LocalSttThreads = localThreads,
            LocalSttTimeoutSeconds = localTimeout,
            SttBaseUrl = BaseUrlTextBox.Text.Trim(),
            CloudProtocol = CloudProtocolTextBox.Text.Trim(),
            Model = ModelTextBox.Text.Trim(),
            Language = LanguageTextBox.Text.Trim(),
            MaxRecordingSeconds = maxSeconds,
            InputDeviceNumber = inputDeviceNumber,
            AutoSend = AutoSendCheckBox.IsChecked == true,
            RestoreClipboard = RestoreClipboardCheckBox.IsChecked == true,
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

    private void SelectBackend(string backend)
    {
        var normalized = backend.Trim().ToLowerInvariant();
        for (var i = 0; i < LocalBackendComboBox.Items.Count; i++)
        {
            if (LocalBackendComboBox.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                LocalBackendComboBox.SelectedIndex = i;
                return;
            }
        }

        LocalBackendComboBox.SelectedIndex = 0;
    }

    private string GetSelectedBackend() =>
        (LocalBackendComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cpu";
}

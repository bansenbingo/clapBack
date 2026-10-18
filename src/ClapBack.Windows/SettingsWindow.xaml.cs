using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;
using ClapBack.Stt;

namespace ClapBack.Windows;

public partial class SettingsWindow : Window
{
    private readonly IAudioInputDeviceCatalog _devices;
    private readonly Func<ClapBackSettings, string, Task> _save;
    private readonly ObservableCollection<DictionaryRow> _entries = [];
    private bool _loading = true;
    private bool _saving;
    private string? _previousModelDefault;

    public SettingsWindow(ClapBackSettings settings, IAudioInputDeviceCatalog devices,
        string apiKey, Func<ClapBackSettings, string, Task> save)
    {
        InitializeComponent();
        Settings = settings;
        _devices = devices;
        _save = save;
        AutoSendCheckBox.IsChecked = settings.AutoSend;
        TranscriptNotificationCheckBox.IsChecked = settings.ShowTranscriptInNotification;
        HomophoneCheckBox.IsChecked = settings.EnableHomophoneSubstitution;
        AutoProvisionCheckBox.IsChecked = settings.AutoProvisionOnStartup;
        InstallVadCheckBox.IsChecked = settings.InstallVadModel;
        TranscriptionModeComboBox.SelectedIndex = Enum.IsDefined(settings.TranscriptionMode) ? (int)settings.TranscriptionMode : 1;
        LocalModelVariantComboBox.ItemsSource = Enum.GetValues<LocalModelVariant>().Select(LocalModelCatalog.Get).ToArray();
        LocalModelVariantComboBox.SelectedItem = LocalModelCatalog.Get(Enum.IsDefined(settings.LocalModelVariant) ? settings.LocalModelVariant : LocalModelVariant.SenseVoiceSmall);
        _previousModelDefault = ((LocalModelArtifact)LocalModelVariantComboBox.SelectedItem).DefaultLocalPath;
        LocalRuntimeTextBox.Text = settings.LocalSttRuntimePath;
        LocalModelTextBox.Text = settings.LocalModelPath;
        LocalVadModelTextBox.Text = settings.LocalVadModelPath;
        LocalThreadsTextBox.Text = settings.LocalSttThreads.ToString();
        LocalTimeoutTextBox.Text = settings.LocalSttTimeoutSeconds.ToString();
        LocalBackendComboBox.SelectedIndex = settings.LocalSttBackend.ToLowerInvariant() switch { "vulkan" => 1, "cuda" => 2, _ => 0 };
        BaseUrlTextBox.Text = settings.SttBaseUrl;
        ModelTextBox.Text = settings.Model;
        LanguageTextBox.Text = settings.Language;
        MaxSecondsTextBox.Text = settings.MaxRecordingSeconds.ToString();
        HotkeyTextBox.Text = settings.Hotkey;
        TargetProcessesTextBox.Text = settings.TargetWindowProcessNames;
        ChatKeyComboBox.SelectedIndex = settings.ChatOpenKey.Equals("F2", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        HuggingFaceEndpointTextBox.Text = settings.HuggingFaceEndpoint;
        RuntimeEndpointTextBox.Text = settings.RuntimeDownloadEndpoint;
        ApiKeyBox.Password = apiKey;
        DictionaryGrid.ItemsSource = _entries;
        LoadEntries(settings.HomophoneEntries);
        RefreshDevices(settings.MicrophoneDeviceId);
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
        _loading = false;
    }

    public ClapBackSettings Settings { get; private set; }
    public sealed class DictionaryRow
    {
        public string Source { get; set; } = string.Empty;
        public string Alternatives { get; set; } = string.Empty;
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        try
        {
            ErrorText.Text = string.Empty;
            if (!HotkeyBinding.TryParse(HotkeyTextBox.Text, out var hotkey))
                throw new InvalidOperationException("热键格式无效，例如 CapsLock、F8、Ctrl+Shift+Space。");
            var maxSeconds = Number(MaxSecondsTextBox.Text, 1, 300, "最长录音");
            var threads = Number(LocalThreadsTextBox.Text, 1, 64, "线程数");
            var timeout = Number(LocalTimeoutTextBox.Text, 1, 300, "识别超时");
            var mode = (TranscriptionMode)TranscriptionModeComboBox.SelectedIndex;
            if (!Enum.IsDefined(mode)) throw new InvalidOperationException("请选择识别模式。");
            if (mode != TranscriptionMode.LocalOnly)
            {
                if (!Uri.TryCreate(BaseUrlTextBox.Text.Trim(), UriKind.Absolute, out var uri)
                    || (uri.Scheme != "https" && !(uri.IsLoopback && uri.Scheme == "http")))
                    throw new InvalidOperationException("云端服务 URL 需要 HTTPS，本机服务可使用 HTTP。");
                if (string.IsNullOrWhiteSpace(ModelTextBox.Text)) throw new InvalidOperationException("请填写云端模型名称。");
            }
            if (AutoSendCheckBox.IsChecked == true && string.IsNullOrWhiteSpace(TargetProcessesTextBox.Text))
                throw new InvalidOperationException("自动发送需要填写游戏目标进程，例如 Overwatch,Overwatch2。");
            DictionaryGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var rules = new List<HomophoneEntry>();
            var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _entries)
            {
                var source = entry.Source.Trim();
                var alternatives = entry.Alternatives.Split([',', '，', ';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
                if (source.Length == 0 && alternatives.Length == 0) continue;
                if (source.Length == 0 || alternatives.Length == 0) throw new InvalidOperationException("字库每条规则都需要原词和至少一个候选。");
                if (!sources.Add(source)) throw new InvalidOperationException($"字库原词重复：{source}");
                rules.Add(new(source, alternatives));
            }
            var device = MicrophoneComboBox.SelectedItem as AudioInputDevice;
            var candidate = Settings with
            {
                Hotkey = hotkey.Name, MaxRecordingSeconds = maxSeconds,
                MicrophoneDeviceId = device?.Id ?? string.Empty, InputDeviceNumber = Math.Max(0, device?.Index ?? 0),
                TranscriptionMode = mode, LocalModelVariant = ((LocalModelArtifact)LocalModelVariantComboBox.SelectedItem).Variant,
                LocalSttRuntimePath = LocalRuntimeTextBox.Text.Trim(), LocalModelPath = LocalModelTextBox.Text.Trim(),
                LocalVadModelPath = LocalVadModelTextBox.Text.Trim(), LocalSttThreads = threads, LocalSttTimeoutSeconds = timeout,
                LocalSttBackend = (LocalBackendComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cpu",
                SttBaseUrl = BaseUrlTextBox.Text.Trim(), Model = ModelTextBox.Text.Trim(), Language = LanguageTextBox.Text.Trim(),
                CloudProtocol = "WhisperCompatible", AutoSend = AutoSendCheckBox.IsChecked == true,
                // The transcript is copied before sending, so the sender cannot restore an earlier user clipboard.
                RestoreClipboard = false, TargetWindowProcessNames = TargetProcessesTextBox.Text.Trim(),
                ChatOpenKey = ChatKeyComboBox.SelectedIndex == 1 ? "F2" : "Enter",
                ShowTranscriptInNotification = TranscriptNotificationCheckBox.IsChecked == true,
                EnableHomophoneSubstitution = HomophoneCheckBox.IsChecked == true, HomophoneEntries = rules,
                AutoProvisionOnStartup = AutoProvisionCheckBox.IsChecked == true, InstallVadModel = InstallVadCheckBox.IsChecked == true,
                HuggingFaceEndpoint = HuggingFaceEndpointTextBox.Text.Trim(), RuntimeDownloadEndpoint = RuntimeEndpointTextBox.Text.Trim()
            };
            _saving = true;
            SaveButton.IsEnabled = false;
            SettingsTabs.IsEnabled = false;
            await _save(candidate, ApiKeyBox.Password);
            Settings = candidate;
            _saving = false;
            DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
        finally { _saving = false; SaveButton.IsEnabled = true; SettingsTabs.IsEnabled = true; }
    }

    private static int Number(string value, int min, int max, string name) =>
        int.TryParse(value, out var result) && result >= min && result <= max ? result
            : throw new InvalidOperationException($"{name}必须是 {min} 到 {max} 之间的整数。");
    private void CancelClick(object sender, RoutedEventArgs e) { if (!_saving) DialogResult = false; }
    private void ModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LocalModelVariantComboBox.SelectedItem is not LocalModelArtifact model) return;
        if (string.IsNullOrWhiteSpace(LocalModelTextBox.Text) || LocalModelTextBox.Text == _previousModelDefault)
            LocalModelTextBox.Text = model.DefaultLocalPath;
        _previousModelDefault = model.DefaultLocalPath;
    }
    private void RefreshDevicesClick(object sender, RoutedEventArgs e) => RefreshDevices((MicrophoneComboBox.SelectedItem as AudioInputDevice)?.Id ?? Settings.MicrophoneDeviceId);
    private void RefreshDevices(string selectedId)
    {
        try
        {
            var devices = new List<AudioInputDevice> { new(string.Empty, "系统默认输入设备", -1) };
            devices.AddRange(_devices.List());
            var selected = devices.FirstOrDefault(device => device.Id == selectedId);
            if (selected is null && selectedId.Length > 0)
            {
                selected = new(selectedId, $"{selectedId}（未连接，录音时回退默认）", -1);
                devices.Add(selected);
            }
            MicrophoneComboBox.ItemsSource = devices;
            MicrophoneComboBox.SelectedItem = selected ?? devices[0];
        }
        catch (Exception ex) { ErrorText.Text = "无法枚举麦克风：" + ex.Message; }
    }
    private void LoadEntries(IReadOnlyList<HomophoneEntry> entries)
    {
        _entries.Clear();
        foreach (var entry in entries) _entries.Add(new() { Source = entry.Source, Alternatives = string.Join("，", entry.Alternatives) });
    }
    private void DeleteEntryClick(object sender, RoutedEventArgs e) { if (DictionaryGrid.SelectedItem is DictionaryRow row) _entries.Remove(row); }
    private void ResetDictionaryClick(object sender, RoutedEventArgs e) => LoadEntries(HomophoneDefaults.All);
}

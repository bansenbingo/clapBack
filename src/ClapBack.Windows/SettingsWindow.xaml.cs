using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;
using ClapBack.Core.Services;
using ClapBack.Stt;

namespace ClapBack.Windows;

public partial class SettingsWindow : Window
{
    private readonly IAudioInputDeviceCatalog _devices;
    private readonly Func<ClapBackSettings, string, Task> _save;
    private readonly Action<double, double, int>? _previewOverlay;
    private readonly Action? _resetOverlayPosition;
    private readonly ObservableCollection<DictionaryRow> _entries = [];
    private bool _loading = true;
    private bool _saving;
    private bool _overlayPositionReset;
    private string? _previousModelDefault;
    private readonly string _hotkeyHintDefault;
    private bool _winKeyDown;

    public SettingsWindow(ClapBackSettings settings, IAudioInputDeviceCatalog devices,
        string apiKey, Func<ClapBackSettings, string, Task> save,
        Action<double, double, int>? previewOverlay = null, Action? resetOverlayPosition = null)
    {
        InitializeComponent();
        _hotkeyHintDefault = HotkeyHintText.Text;
        Settings = settings;
        _devices = devices;
        _save = save;
        _previewOverlay = previewOverlay;
        _resetOverlayPosition = resetOverlayPosition;
        AutoSendCheckBox.IsChecked = settings.AutoSend;
        TranscriptNotificationCheckBox.IsChecked = settings.ShowTranscriptInNotification;
        MinimizeToTrayCheckBox.IsChecked = settings.MinimizeToTray;
        RunAsAdministratorCheckBox.IsChecked = settings.RunAsAdministrator;
        TrimTrailingPunctuationCheckBox.IsChecked = settings.TrimTrailingPunctuation;
        ShowOverlayCheckBox.IsChecked = settings.ShowOverlay;
        OverlayFontSizeTextBox.Text = settings.OverlayFontSize.ToString("0.#");
        OverlayOpacityTextBox.Text = settings.OverlayOpacity.ToString("0.##");
        OverlayDurationTextBox.Text = settings.OverlayDurationSeconds.ToString();
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
        LoadModelOptions(settings.Model);
        LoadLanguageOptions(settings.Language);
        MaxSecondsTextBox.Text = settings.MaxRecordingSeconds.ToString();
        HotkeyTextBox.Text = settings.Hotkey;
        RefreshTargetProcesses(settings.TargetWindowProcessNames);
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
            var overlayFontSize = Decimal(OverlayFontSizeTextBox.Text, 12, 72, "气泡字号");
            var overlayOpacity = Decimal(OverlayOpacityTextBox.Text, 0.2, 1.0, "气泡不透明度");
            var overlayDuration = Number(OverlayDurationTextBox.Text, 1, 60, "气泡显示时长");
            var mode = (TranscriptionMode)TranscriptionModeComboBox.SelectedIndex;
            if (!Enum.IsDefined(mode)) throw new InvalidOperationException("请选择识别模式。");
            if (mode != TranscriptionMode.LocalOnly)
            {
                if (!Uri.TryCreate(BaseUrlTextBox.Text.Trim(), UriKind.Absolute, out var uri)
                    || (uri.Scheme != "https" && !(uri.IsLoopback && uri.Scheme == "http")))
                    throw new InvalidOperationException("云端服务 URL 需要 HTTPS，本机服务可使用 HTTP。");
                if (string.IsNullOrWhiteSpace(ModelComboBox.Text)) throw new InvalidOperationException("请填写云端模型名称。");
            }
            if (AutoSendCheckBox.IsChecked == true && LocalProcessCatalog.NormalizeList(TargetProcessesTextBox.Text).Length == 0)
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
                SttBaseUrl = BaseUrlTextBox.Text.Trim(), Model = ModelComboBox.Text.Trim(),
                Language = (LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? Settings.Language,
                CloudProtocol = "WhisperCompatible", AutoSend = AutoSendCheckBox.IsChecked == true,
                // The transcript is copied before sending, so the sender cannot restore an earlier user clipboard.
                RestoreClipboard = false, TargetWindowProcessNames = LocalProcessCatalog.NormalizeList(TargetProcessesTextBox.Text),
                ChatOpenKey = ChatKeyComboBox.SelectedIndex == 1 ? "F2" : "Enter",
                ShowTranscriptInNotification = TranscriptNotificationCheckBox.IsChecked == true,
                MinimizeToTray = MinimizeToTrayCheckBox.IsChecked == true,
                RunAsAdministrator = RunAsAdministratorCheckBox.IsChecked == true,
                TrimTrailingPunctuation = TrimTrailingPunctuationCheckBox.IsChecked == true,
                ShowOverlay = ShowOverlayCheckBox.IsChecked == true,
                OverlayFontSize = overlayFontSize, OverlayOpacity = overlayOpacity,
                OverlayDurationSeconds = overlayDuration,
                OverlayLeft = _overlayPositionReset ? null : Settings.OverlayLeft,
                OverlayTop = _overlayPositionReset ? null : Settings.OverlayTop,
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

    /// <summary>语言用下拉选，避免手打 "zh-cn" / "Chinese" 这类运行时并不认的写法。</summary>
    private void LoadLanguageOptions(string current)
    {
        var options = new (string Code, string Display)[]
        {
            ("zh", "中文（zh）"),
            ("en", "英语（en）"),
            ("ja", "日语（ja）"),
            ("ko", "韩语（ko）"),
            ("yue", "粤语（yue）"),
            ("auto", "自动检测（auto）"),
        };

        LanguageComboBox.Items.Clear();
        ComboBoxItem? selected = null;
        foreach (var option in options)
        {
            var item = new ComboBoxItem { Content = option.Display, Tag = option.Code };
            LanguageComboBox.Items.Add(item);
            if (string.Equals(option.Code, current, StringComparison.OrdinalIgnoreCase))
            {
                selected = item;
            }
        }

        if (selected is null && !string.IsNullOrWhiteSpace(current))
        {
            // 配置里是自定义语言码：保留下来，不能因为下拉里没有就把用户的设置吞掉。
            selected = new ComboBoxItem { Content = current + "（当前配置）", Tag = current };
            LanguageComboBox.Items.Add(selected);
        }

        LanguageComboBox.SelectedItem = selected ?? LanguageComboBox.Items[0];
    }

    /// <summary>云端模型名常见值给个下拉，仍需允许手输（兼容自建网关）。</summary>
    private void LoadModelOptions(string current)
    {
        var known = new[] { "glm-asr-2512", "whisper-1", "gpt-4o-transcribe", "whisper-large-v3" };
        ModelComboBox.Items.Clear();
        foreach (var name in known)
        {
            ModelComboBox.Items.Add(name);
        }

        ModelComboBox.Text = current;
    }

    /// <summary>
    /// 热键栏改成“按键盘捕获”：按下的键直接变成热键名，避免手打 CapsLock / Ctrl+Shift+Space 出错。
    /// 判定规则与 HotkeyBinding.TryParse 完全一致——CapsLock / Enter / Space / F1–F24 可单独使用，
    /// 字母和数字必须带 Ctrl/Alt/Shift/Win，否则会被 TryParse 拒绝。
    /// </summary>
    private void HotkeyBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _winKeyDown = false;
        HotkeyHintText.Text = _hotkeyHintDefault;
    }

    private void HotkeyBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _winKeyDown = false;
        HotkeyHintText.Text = _hotkeyHintDefault;
        // 热键栏只会被写成合法名字；万一不是，就退回已保存的热键，不让脏值流进 SaveClick。
        if (!HotkeyBinding.TryParse(HotkeyTextBox.Text, out _)) HotkeyTextBox.Text = Settings.Hotkey;
    }

    private void HotkeyBoxPreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LWin or Key.RWin) _winKeyDown = false;
        if (IsModifierKey(key)) HotkeyHintText.Text = _hotkeyHintDefault;
    }

    private void HotkeyBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab or Key.Escape) return; // 留给窗口导航，别把焦点锁死在热键栏里
        if (key is Key.LWin or Key.RWin)
        {
            _winKeyDown = true;
            ShowPendingHotkey();
            e.Handled = true;
            return;
        }

        if (IsModifierKey(key))
        {
            ShowPendingHotkey();
            e.Handled = true;
            return;
        }

        var keyName = DescribeHotkeyKey(key);
        if (keyName is null)
        {
            HotkeyHintText.Text = "这个键不能当热键：CapsLock、Enter、Space、F1–F24 可单独使用；字母和数字需要配合 Ctrl/Alt/Shift/Win。";
            e.Handled = true;
            return;
        }

        var candidate = string.Join('+', ModifierNames().Append(keyName));
        if (HotkeyBinding.TryParse(candidate, out var binding))
        {
            HotkeyTextBox.Text = binding.Name;
            HotkeyHintText.Text = $"已捕获 {binding.Name}；点“保存并应用”后生效，注册失败会保留原热键。";
        }
        else
        {
            HotkeyHintText.Text = $"“{candidate}”不能当热键：字母和数字需要配合 Ctrl/Alt/Shift/Win，例如 Ctrl+{keyName}。";
        }

        e.Handled = true;
    }

    private void ShowPendingHotkey()
    {
        var names = ModifierNames().ToList();
        HotkeyHintText.Text = names.Count == 0
            ? "请直接按下要使用的按键或组合键。"
            : string.Join('+', names) + "+ … 继续按下一个键。";
    }

    private IEnumerable<string> ModifierNames()
    {
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) yield return "Ctrl";
        if (modifiers.HasFlag(ModifierKeys.Alt)) yield return "Alt";
        if (modifiers.HasFlag(ModifierKeys.Shift)) yield return "Shift";
        if (_winKeyDown || modifiers.HasFlag(ModifierKeys.Windows)) yield return "Win";
    }

    private static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string? DescribeHotkeyKey(Key key)
    {
        var code = (int)key;
        if (key == Key.CapsLock) return "CapsLock";
        if (key == Key.Enter) return "Enter";
        if (key == Key.Space) return "Space";
        if (code >= (int)Key.F1 && code <= (int)Key.F24) return "F" + (code - (int)Key.F1 + 1);
        if (code >= (int)Key.A && code <= (int)Key.Z) return ((char)('A' + code - (int)Key.A)).ToString();
        if (code >= (int)Key.D0 && code <= (int)Key.D9) return ((char)('0' + code - (int)Key.D0)).ToString();
        return null;
    }

    private void RefreshProcessesClick(object sender, RoutedEventArgs e)
        => RefreshTargetProcesses(TargetProcessesTextBox.Text);

    /// <summary>
    /// 目标进程候选：先放常见游戏（本机没启动也能选），再接上本机当前有窗口的进程。
    /// 手填项仍然保留，因为白名单可以写多个、也可能想填还没装的游戏。
    /// </summary>
    private void RefreshTargetProcesses(string current)
    {
        var previous = TargetProcessComboBox.SelectedItem as string;
        var candidates = new List<string> { "Overwatch", "Overwatch2" };
        candidates.AddRange(LocalProcessCatalog.ListWindowProcessNames());
        var distinct = candidates
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        TargetProcessComboBox.ItemsSource = distinct;
        TargetProcessComboBox.SelectedItem = previous is not null
            ? distinct.FirstOrDefault(name => string.Equals(name, previous, StringComparison.OrdinalIgnoreCase))
              ?? distinct[0]
            : distinct[0];
        TargetProcessesTextBox.Text = LocalProcessCatalog.NormalizeList(
            string.IsNullOrWhiteSpace(current) ? TargetProcessesTextBox.Text : current);
    }

    private void AddTargetProcessClick(object sender, RoutedEventArgs e)
    {
        if (TargetProcessComboBox.SelectedItem is not string name || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var existing = LocalProcessCatalog.NormalizeList(TargetProcessesTextBox.Text);
        var tokens = existing.Length == 0
            ? new List<string>()
            : existing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!tokens.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            tokens.Add(name);
        }

        TargetProcessesTextBox.Text = LocalProcessCatalog.NormalizeList(string.Join(",", tokens));
        ErrorText.Text = $"已添加目标进程：{name}";
    }

    private void ClearTargetProcessesClick(object sender, RoutedEventArgs e)
    {
        TargetProcessesTextBox.Text = string.Empty;
        ErrorText.Text = "已清空目标进程；开启自动发送前需要重新填写。";
    }

    private static int Number(string value, int min, int max, string name) =>
        int.TryParse(value, out var result) && result >= min && result <= max ? result
            : throw new InvalidOperationException($"{name}必须是 {min} 到 {max} 之间的整数。");

    private static double Decimal(string value, double min, double max, string name) =>
        double.TryParse(value, out var result) && result >= min && result <= max ? result
            : throw new InvalidOperationException($"{name}必须是 {min} 到 {max} 之间的数字。");

    private void PreviewOverlayClick(object sender, RoutedEventArgs e)
    {
        if (_previewOverlay is null)
        {
            ErrorText.Text = "当前入口不支持气泡预览。";
            return;
        }

        try
        {
            ErrorText.Text = string.Empty;
            _previewOverlay(
                Decimal(OverlayFontSizeTextBox.Text, 12, 72, "气泡字号"),
                Decimal(OverlayOpacityTextBox.Text, 0.2, 1.0, "气泡不透明度"),
                Number(OverlayDurationTextBox.Text, 1, 60, "气泡显示时长"));
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }

    private void ResetOverlayPositionClick(object sender, RoutedEventArgs e)
    {
        _overlayPositionReset = true;
        _resetOverlayPosition?.Invoke();
        ErrorText.Text = "气泡位置已复位，下次会显示在屏幕底部居中。";
    }
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

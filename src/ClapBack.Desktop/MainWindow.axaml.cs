using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;
using ClapBack.Core.Services;
using ClapBack.Stt;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ClapBack.Desktop;

public partial class MainWindow : Window
{
    private readonly App _app;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly DispatcherTimer _recordingTimer;
    private readonly Stopwatch _recordingClock = new();
    private readonly List<HomophoneEntry> _homophoneEntries = new();
    private string _lastTranscript = string.Empty;
    private ClapBackSettings? _recordingSettings;
    private Task _activeWork = Task.CompletedTask;
    private bool _provisioning;
    private bool _starting;
    private bool _recording;
    private bool _processing;
    private bool _closing;
    private bool _allowClose;

    // Avalonia's runtime XAML loader requires a public parameterless constructor.
    public MainWindow()
        : this((App?)Application.Current
               ?? throw new InvalidOperationException("ClapBack 应用尚未初始化"))
    {
    }

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _recordingTimer.Tick += RecordingTimerTick;
        Closing += WindowClosing;
        LoadSettings(app.Settings);
        LoadMicrophones(app.Settings.MicrophoneDeviceId);
        PythonPathLabel.IsVisible = OperatingSystem.IsMacOS();
        PythonPathTextBox.IsVisible = OperatingSystem.IsMacOS();
        ModeComboBox.SelectionChanged += (_, _) => UpdateModeHint();
        UpdateModeHint();
        UpdateControls();
        _activeWork = RunProvisioningAsync(app.Settings);
    }

    private bool IsWorkInProgress => _closing || _starting || _recording || _processing || _provisioning;

    private void UpdateControls()
    {
        if (_closing)
        {
            return;
        }

        var editable = !IsWorkInProgress;
        SettingsPanel.IsEnabled = editable;
        HomophonePanel.IsEnabled = editable;
        ProvisionButton.IsEnabled = editable;
        RecordButton.IsEnabled = !_starting && !_processing && !_provisioning && _app.AudioRecorder is not null;
        RecordButton.Content = _starting ? "正在启动…"
            : _processing ? "处理中…"
            : _recording ? "停止并识别"
            : "开始录音";
        CopyButton.IsEnabled = editable && !string.IsNullOrWhiteSpace(_lastTranscript);
    }

    // ---------------------------------------------------------------- 麦克风

    private void LoadMicrophones(string? selectedDeviceId)
    {
        MicrophoneComboBox.Items.Clear();
        MicrophoneComboBox.Items.Add(new ComboBoxItem
        {
            Content = "系统默认输入设备",
            Tag = string.Empty
        });

        foreach (var device in _app.MicrophoneCatalog.List())
        {
            MicrophoneComboBox.Items.Add(new ComboBoxItem
            {
                Content = device.Name,
                Tag = device.Id
            });
        }

        MicrophoneComboBox.SelectedIndex = 0;
        for (var index = 0; index < MicrophoneComboBox.ItemCount; index++)
        {
            if (MicrophoneComboBox.Items[index] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), selectedDeviceId, StringComparison.Ordinal))
            {
                MicrophoneComboBox.SelectedIndex = index;
                break;
            }
        }
    }

    private void RefreshMicrophonesClick(object? sender, RoutedEventArgs e)
    {
        if (IsWorkInProgress)
        {
            return;
        }

        try
        {
            var current = (MicrophoneComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
            LoadMicrophones(current);
            StatusText.Text = MicrophoneComboBox.ItemCount > 1
                ? $"已找到 {MicrophoneComboBox.ItemCount - 1} 个麦克风"
                : "没有枚举到麦克风，将使用系统默认输入设备";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"无法刷新麦克风：{ex.Message}";
        }
    }

    // ---------------------------------------------------------------- 录音

    private async void RecordClick(object? sender, RoutedEventArgs e)
    {
        if (_closing || _provisioning || _starting || _processing)
        {
            return;
        }

        if (_app.AudioRecorder is not { } recorder)
        {
            StatusText.Text = "当前平台未启用麦克风录音";
            return;
        }

        var operation = _recording
            ? StopAndTranscribeAsync(recorder, timedOut: false)
            : StartRecordingAsync(recorder);
        _activeWork = operation;
        await operation;
    }

    private async Task StartRecordingAsync(IAudioRecorder recorder)
    {
        // Set the state before the first await so repeated clicks cannot start twice.
        _starting = true;
        UpdateControls();
        try
        {
            if (!TryApplySettings(out var settings))
            {
                return;
            }

            // Providers read App.Settings through delegates. Lock every mutation
            // entry point until stop, recognition and clipboard copy finish.
            _recordingSettings = settings;
            await recorder.StartAsync(_lifetimeCancellation.Token);
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            _recording = true;
            _recordingClock.Restart();
            _recordingTimer.Start();
            StatusText.Text = $"录音中（最长 {settings.MaxRecordingSeconds} 秒）";
            TranscriptText.Text = "请说话，完成后再次点击按钮。到时会自动停止并识别。";
            UpdateModeHint();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_closing)
            {
                StatusText.Text = "无法开始录音，请检查麦克风权限与设备后重试";
            }
        }
        finally
        {
            if (!_recording)
            {
                _recordingSettings = null;
                await CancelRecorderAsync();
            }

            _starting = false;
            UpdateControls();
        }
    }

    private async Task StopAndTranscribeAsync(IAudioRecorder recorder, bool timedOut)
    {
        if (_closing || _starting || _processing || !_recording)
        {
            return;
        }

        _processing = true;
        _recording = false;
        _recordingTimer.Stop();
        _recordingClock.Stop();
        UpdateControls();
        StatusText.Text = timedOut ? "录音到时限，正在识别…" : "正在结束录音并识别…";
        try
        {
            using var recorded = await recorder.StopAsync(_lifetimeCancellation.Token);
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            await TranscribeAsync(recorded.Content, _recordingSettings ?? _app.Settings);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_closing)
            {
                StatusText.Text = "录音或识别失败，请检查运行环境和云端服务设置后重试";
                TranscriptText.Text = string.IsNullOrWhiteSpace(_lastTranscript)
                    ? "本次未获得识别结果，请检查麦克风和运行环境后重试。"
                    : _lastTranscript;
            }
        }
        finally
        {
            await CancelRecorderAsync();
            _recordingSettings = null;
            _recordingClock.Reset();
            _processing = false;
            UpdateControls();
        }
    }

    private void RecordingTimerTick(object? sender, EventArgs e)
    {
        if (_closing || _processing || !_recording || _app.AudioRecorder is not { } recorder)
        {
            _recordingTimer.Stop();
            return;
        }

        var limit = (_recordingSettings ?? _app.Settings).MaxRecordingSeconds;
        var elapsed = _recordingClock.Elapsed.TotalSeconds;
        if (elapsed >= limit || !recorder.IsRecording)
        {
            // macOS can end capture at its own limit before this tick. Collect
            // that recording instead of treating the next click as a new start.
            _activeWork = StopAndTranscribeAsync(recorder, timedOut: elapsed >= limit);
            return;
        }

        StatusText.Text = $"录音中（还剩 {Math.Max(1, (int)Math.Ceiling(limit - elapsed))} 秒）";
    }

    private async Task TranscribeAsync(Stream stream, ClapBackSettings settings)
    {
        var result = await _app.TranscriptionProvider.TranscribeAsync(
            stream,
            new TranscriptionOptions(settings.Model, settings.Language),
            _lifetimeCancellation.Token);
        _lifetimeCancellation.Token.ThrowIfCancellationRequested();
        // Preview and clipboard must contain the same substituted text.
        var cleaned = settings.TrimTrailingPunctuation
            ? TranscriptCleaner.StripTrailingPeriod(result.Text.Trim())
            : result.Text.Trim();
        _lastTranscript = HomophoneSubstitution.Apply(settings, cleaned);
        TranscriptText.Text = string.IsNullOrWhiteSpace(_lastTranscript)
            ? "没有识别到有效语音。"
            : _lastTranscript;
        if (string.IsNullOrWhiteSpace(_lastTranscript))
        {
            StatusText.Text = "没有识别到有效语音，剪贴板未更改";
            return;
        }

        await CopyTranscriptAsync("识别完成，已自动复制到剪贴板");
    }

    private async void CopyClick(object? sender, RoutedEventArgs e)
    {
        if (IsWorkInProgress || string.IsNullOrWhiteSpace(_lastTranscript))
        {
            return;
        }

        _processing = true;
        UpdateControls();
        var operation = CopyTranscriptAsync("已复制到剪贴板");
        _activeWork = operation;
        try
        {
            await operation;
        }
        finally
        {
            _processing = false;
            UpdateControls();
        }
    }

    private async Task CopyTranscriptAsync(string successStatus)
    {
        if (_closing || string.IsNullOrWhiteSpace(_lastTranscript))
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                ?? throw new InvalidOperationException("当前窗口没有可用的剪贴板");
            await clipboard.SetTextAsync(_lastTranscript).WaitAsync(_lifetimeCancellation.Token);
            if (!_closing)
            {
                StatusText.Text = successStatus;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_closing)
            {
                // Keep the result unchanged; UpdateControls re-enables retry.
                StatusText.Text = "写入剪贴板失败，结果已保留，请点击“复制结果”重试";
            }
        }
    }

    // ---------------------------------------------------------------- 运行环境

    private async void ProvisionClick(object? sender, RoutedEventArgs e)
    {
        if (IsWorkInProgress || !TryApplySettings(out var settings))
        {
            return;
        }

        var operation = RunProvisioningAsync(settings);
        _activeWork = operation;
        await operation;
    }

    private async Task RunProvisioningAsync(ClapBackSettings settings)
    {
        if (IsWorkInProgress)
        {
            return;
        }

        _provisioning = true;
        UpdateControls();
        StatusText.Text = "正在准备运行环境…";
        try
        {
            if (settings.TranscriptionMode == TranscriptionMode.CloudOnly)
            {
                ProvisionText.Text = "仅云端识别不需要本地识别模型。请先配置服务地址、模型和 CLAPBACK_STT_API_KEY；此处不验证云端连接。"
                    + (OperatingSystem.IsMacOS() ? " macOS 麦克风录音仍需 Python 环境与录音脚本。" : string.Empty);
                StatusText.Text = "仅云端模式：录音将上传到配置的服务";
                return;
            }

            ProvisionText.Text = "正在检测模型与运行时…";
            var progress = new Progress<string>(message =>
            {
                if (!_closing && _provisioning)
                {
                    ProvisionText.Text = message;
                }
            });
            var report = await Task.Run(
                () => _app.Provisioner.EnsureAsync(settings, progress, _lifetimeCancellation.Token),
                _lifetimeCancellation.Token);
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            ApplyProvisionReport(report);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_closing)
            {
                ProvisionText.Text = "环境准备失败，请检查网络与本地模型设置，然后点击“检查运行环境”重试。";
                StatusText.Text = "环境准备失败";
            }
        }
        finally
        {
            _provisioning = false;
            UpdateControls();
        }
    }

    private void ApplyProvisionReport(ProvisionReport report)
    {
        ProvisionText.Text = string.Join(Environment.NewLine, report.Steps.Select(step =>
        {
            var detail = step.Status switch
            {
                ProvisionStatus.Ok => "已就绪",
                ProvisionStatus.Installed => "准备完成",
                ProvisionStatus.Skipped => "当前配置不需要，已跳过",
                ProvisionStatus.OptionalUnavailable => "暂不可用；短语音仍可继续识别",
                _ when OperatingSystem.IsMacOS() => "请先运行 scripts/macos-funasr-setup.sh 准备 Python 依赖，并检查 Python 环境路径后重试",
                _ => "准备失败；请检查网络与模型设置后点击“检查运行环境”重试。若关闭了自动安装，请先准备所需本地文件"
            };
            return $"·{step.Name}：{detail}";
        }));
        StatusText.Text = report.IsReady
            ? "本地运行环境已就绪，可以开始录音"
            : "本地运行环境未就绪，请按上方提示准备后重试";
    }

    // ---------------------------------------------------------------- 设置

    private void SaveSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (IsWorkInProgress || !TryApplySettings(out _))
        {
            return;
        }

        StatusText.Text = "设置已保存";
        UpdateModeHint();
    }

    private bool TryApplySettings(out ClapBackSettings settings)
    {
        var previous = _app.Settings;
        try
        {
            settings = BuildSettingsFromUi(previous);
            _app.SaveSettings(settings);
            return true;
        }
        catch (Exception ex)
        {
            // App.SaveSettings only publishes after persistence succeeds.
            settings = previous;
            StatusText.Text = $"设置保存失败：{ex.Message}";
            return false;
        }
    }

    private ClapBackSettings BuildSettingsFromUi(ClapBackSettings current)
    {
        var mode = (ModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "LocalOnly";
        var model = (ModelComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "SenseVoiceSmall";
        var microphone = (MicrophoneComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        var parsedMode = Enum.TryParse<TranscriptionMode>(mode, out var modeValue) && Enum.IsDefined(modeValue)
            ? modeValue
            : TranscriptionMode.LocalOnly;
        var parsedModel = Enum.TryParse<LocalModelVariant>(model, out var modelValue) && Enum.IsDefined(modelValue)
            ? modelValue
            : LocalModelVariant.SenseVoiceSmall;
        var python = OperatingSystem.IsMacOS() ? PythonPathTextBox.Text?.Trim() : current.MacOsPythonPath;
        if (OperatingSystem.IsMacOS() && string.IsNullOrWhiteSpace(python))
        {
            throw new InvalidOperationException("请填写 macOS Python 环境路径");
        }

        if (RecordingLimitBox.Value is not { } maxSeconds ||
            maxSeconds < 1 || maxSeconds > 300 || decimal.Truncate(maxSeconds) != maxSeconds)
        {
            throw new InvalidOperationException("最长录音时长必须是 1–300 之间的整数秒");
        }

        var cloudUrl = CloudUrlBox.Text?.Trim() ?? string.Empty;
        var cloudModel = CloudModelBox.Text?.Trim() ?? string.Empty;
        if (parsedMode != TranscriptionMode.LocalOnly)
        {
            if (!Uri.TryCreate(cloudUrl, UriKind.Absolute, out var endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
                (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback && !current.AllowInsecureHttpEndpoint))
            {
                throw new InvalidOperationException("请填写有效的云端服务 URL（默认要求 HTTPS；仅本机回环地址可使用 HTTP）");
            }

            if (cloudModel.Length == 0)
            {
                throw new InvalidOperationException("请填写云端模型名称");
            }
        }

        return current with
        {
            TranscriptionMode = parsedMode,
            LocalModelVariant = parsedModel,
            MicrophoneDeviceId = microphone,
            MaxRecordingSeconds = (int)maxSeconds,
            SttBaseUrl = cloudUrl,
            Model = cloudModel,
            MacOsPythonPath = python ?? current.MacOsPythonPath,
            EnableHomophoneSubstitution = HomophoneEnabledCheckBox.IsChecked ?? true,
            HomophoneEntries = SanitizeEntries(_homophoneEntries).ToArray()
        };
    }

    private void LoadSettings(ClapBackSettings settings)
    {
        // Unknown mode values must fall back to LocalOnly, never CloudOnly.
        ModeComboBox.SelectedIndex = IsValidIndex((int)settings.TranscriptionMode, ModeComboBox.ItemCount)
            ? (int)settings.TranscriptionMode
            : (int)TranscriptionMode.LocalOnly;
        ModelComboBox.SelectedIndex = IsValidIndex((int)settings.LocalModelVariant, ModelComboBox.ItemCount)
            ? (int)settings.LocalModelVariant
            : (int)LocalModelVariant.SenseVoiceSmall;
        PythonPathTextBox.Text = settings.MacOsPythonPath;
        RecordingLimitBox.Value = Math.Clamp(settings.MaxRecordingSeconds, 1, 300);
        CloudUrlBox.Text = settings.SttBaseUrl;
        CloudModelBox.Text = settings.Model;
        HomophoneEnabledCheckBox.IsChecked = settings.EnableHomophoneSubstitution;
        _homophoneEntries.Clear();
        _homophoneEntries.AddRange(SanitizeEntries(settings.HomophoneEntries ?? HomophoneDefaults.All));
        RefreshHomophoneList();
    }

    private void UpdateModeHint()
    {
        if (_closing)
        {
            return;
        }

        var mode = (ModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var modeHint = mode switch
        {
            nameof(TranscriptionMode.CloudOnly) => "仅云端：录音会上传到已配置的语音服务。",
            nameof(TranscriptionMode.LocalFirst) => _app.Settings.FallbackOnEmptyResult
                ? "本地优先：本地异常或返回空结果时，会回退云端并上传录音。"
                : "本地优先：本地异常时会回退云端并上传录音；空结果不会触发上传。",
            _ => "仅本地：音频在本机识别，不会上传到云端。"
        };
        RecordHintText.Text = _app.AudioRecorder is null
            ? $"当前平台未提供麦克风录音。{modeHint}"
            : $"{modeHint}识别结果会自动复制；可点击“复制结果”重试。首次录音需要允许麦克风权限。";
    }

    private static IEnumerable<HomophoneEntry> SanitizeEntries(IEnumerable<HomophoneEntry>? entries) =>
        (entries ?? Enumerable.Empty<HomophoneEntry>())
        .Where(static entry => entry is not null
            && !string.IsNullOrWhiteSpace(entry.Source)
            && entry.Alternatives is not null
            && entry.Alternatives.Any(static alternative => !string.IsNullOrWhiteSpace(alternative)))
        .Select(static entry => new HomophoneEntry(
            entry.Source.Trim(),
            entry.Alternatives
                .Where(static alternative => !string.IsNullOrWhiteSpace(alternative))
                .Select(static alternative => alternative.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray()))
        .DistinctBy(static entry => entry.Source, StringComparer.Ordinal);

    private static bool IsValidIndex(int index, int count) => index >= 0 && index < count;

    // ---------------------------------------------------------------- 字库

    private void RefreshHomophoneList()
    {
        HomophoneList.ItemsSource = _homophoneEntries
            .Select(static entry => new HomophoneRow(entry.Source, string.Join("、", entry.Alternatives)))
            .ToList();
    }

    private static IReadOnlyList<string> SplitAlternatives(string? raw) =>
        (raw ?? string.Empty)
            .Split(new[] { '、', '，', ',', ';', '；', '/', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private void AddHomophoneClick(object? sender, RoutedEventArgs e)
    {
        if (IsWorkInProgress || !TryReadEditor(out var source, out var alternatives))
        {
            return;
        }

        if (_homophoneEntries.Any(entry => string.Equals(entry.Source, source, StringComparison.Ordinal)))
        {
            HomophoneStatusText.Text = $"「{source}」已在字库里；要改内容请先在下面选中它，再点「保存修改」。";
            return;
        }

        _homophoneEntries.Add(new HomophoneEntry(source, alternatives));
        PersistHomophones($"已新增「{source}」");
    }

    private void SaveHomophoneClick(object? sender, RoutedEventArgs e)
    {
        if (IsWorkInProgress)
        {
            return;
        }

        if (HomophoneList.SelectedItem is not HomophoneRow selected)
        {
            HomophoneStatusText.Text = "请先在下面的字库里选中一条，再点「保存修改」。";
            return;
        }

        if (!TryReadEditor(out var source, out var alternatives))
        {
            return;
        }

        var index = _homophoneEntries.FindIndex(entry => string.Equals(entry.Source, selected.Source, StringComparison.Ordinal));
        if (index < 0)
        {
            HomophoneStatusText.Text = "选中的条目已经不在字库里了，请重新选择。";
            return;
        }

        _homophoneEntries.RemoveAll(entry => string.Equals(entry.Source, source, StringComparison.Ordinal));
        index = _homophoneEntries.FindIndex(entry => string.Equals(entry.Source, selected.Source, StringComparison.Ordinal));
        if (index >= 0)
        {
            _homophoneEntries[index] = new HomophoneEntry(source, alternatives);
        }
        else
        {
            _homophoneEntries.Add(new HomophoneEntry(source, alternatives));
        }

        PersistHomophones($"已保存「{source}」");
    }

    private void DeleteHomophoneClick(object? sender, RoutedEventArgs e)
    {
        if (IsWorkInProgress)
        {
            return;
        }

        if (HomophoneList.SelectedItem is not HomophoneRow selected)
        {
            HomophoneStatusText.Text = "请先在下面的字库里选中一条，再点「删除选中」。";
            return;
        }

        _homophoneEntries.RemoveAll(entry => string.Equals(entry.Source, selected.Source, StringComparison.Ordinal));
        HomophoneSourceBox.Text = string.Empty;
        HomophoneAlternativesBox.Text = string.Empty;
        PersistHomophones($"已删除「{selected.Source}」");
    }

    private void ResetHomophonesClick(object? sender, RoutedEventArgs e)
    {
        if (IsWorkInProgress)
        {
            return;
        }

        _homophoneEntries.Clear();
        _homophoneEntries.AddRange(HomophoneDefaults.All);
        PersistHomophones($"已恢复默认字库（{HomophoneDefaults.All.Count} 条）");
    }

    private void HomophoneListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsWorkInProgress && HomophoneList.SelectedItem is HomophoneRow selected)
        {
            HomophoneSourceBox.Text = selected.Source;
            HomophoneAlternativesBox.Text = selected.AlternativesText;
        }
    }

    private bool TryReadEditor(out string source, out IReadOnlyList<string> alternatives)
    {
        source = HomophoneSourceBox.Text?.Trim() ?? string.Empty;
        alternatives = SplitAlternatives(HomophoneAlternativesBox.Text);
        if (source.Length == 0)
        {
            HomophoneStatusText.Text = "敏感词不能为空。";
            return false;
        }

        if (alternatives.Count == 0)
        {
            HomophoneStatusText.Text = "至少要有一个同音替换候选。";
            return false;
        }

        return true;
    }

    private void PersistHomophones(string status)
    {
        var saved = TryApplySettings(out _);
        RefreshHomophoneList();
        HomophoneStatusText.Text = saved ? status : "字库尚未保存，请检查设置和目录权限后重试保存";
    }

    // ---------------------------------------------------------------- 关闭

    private void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        _recordingTimer.Stop();
        _recordingTimer.Tick -= RecordingTimerTick;
        _recordingClock.Stop();
        SettingsPanel.IsEnabled = false;
        HomophonePanel.IsEnabled = false;
        RecordButton.IsEnabled = false;
        ProvisionButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        StatusText.Text = "正在取消本次工作并关闭…";
        _lifetimeCancellation.Cancel();
        _ = FinishCloseAsync(_activeWork);
    }

    private async Task FinishCloseAsync(Task activeWork)
    {
        // Return from the canceled Closing event before calling Close again,
        // including when no work or recorder cleanup needs to await.
        await Task.Yield();
        // Let StopAsync clean up its own process before CancelAsync touches it;
        // this avoids concurrent stop/dispose calls in the macOS recorder.
        try
        {
            await activeWork;
        }
        catch
        {
            // A failed operation must not prevent closing.
        }

        await CancelRecorderAsync();
        _lifetimeCancellation.Dispose();
        _allowClose = true;
        Close();
    }

    private async Task CancelRecorderAsync()
    {
        try
        {
            if (_app.AudioRecorder is not null)
            {
                await _app.AudioRecorder.CancelAsync();
            }
        }
        catch
        {
            // Best-effort cleanup also covers a partially failed recorder start.
        }
    }
}

/// <summary>字库列表里的一行显示。</summary>
internal sealed record HomophoneRow(string Source, string AlternativesText);

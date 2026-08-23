using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClapBack.Core.Models;

namespace ClapBack.Desktop;

public partial class MainWindow : Window
{
    private readonly App _app;
    private string _lastTranscript = string.Empty;

    // Avalonia's runtime XAML loader requires a public parameterless constructor.
    // The application lifetime constructor below remains the explicit path used
    // by App.OnFrameworkInitializationCompleted.
    public MainWindow()
        : this((App?)Application.Current
               ?? throw new InvalidOperationException("ClapBack 应用尚未初始化"))
    {
    }

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        LoadSettings(app.Settings);
        RecordButton.IsEnabled = app.AudioRecorder is not null;
        if (app.AudioRecorder is null)
        {
            RecordHintText.Text = "Windows：录音和全局热键由 WPF 托盘程序提供；这里可选择 WAV 文件测试识别。";
        }
    }

    private async void ChooseAudioClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要识别的音频",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("音频文件")
                {
                    // Both the GGUF executable and the FunASR adapter consume
                    // PCM WAV bytes directly; format conversion is a separate
                    // follow-up so the GUI does not silently mislabel audio.
                    Patterns = ["*.wav"]
                }
            ]
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        SetBusy(true, $"正在识别：{file.Name}");
        try
        {
            await using var stream = await file.OpenReadAsync();
            await TranscribeAsync(stream, file.Name);
        }
        catch (Exception ex)
        {
            StatusText.Text = "识别失败";
            TranscriptText.Text = ex.Message;
        }
        finally
        {
            SetBusy(false, StatusText.Text ?? "等待选择音频");
        }
    }

    private async void RecordClick(object? sender, RoutedEventArgs e)
    {
        var recorder = _app.AudioRecorder;
        if (recorder is null)
        {
            StatusText.Text = "当前平台未启用麦克风录音";
            return;
        }

        if (!recorder.IsRecording)
        {
            try
            {
                await recorder.StartAsync();
                RecordButton.Content = "停止并识别";
                ChooseAudioButton.IsEnabled = false;
                StatusText.Text = $"录音中（最长 {_app.Settings.MaxRecordingSeconds} 秒）";
                TranscriptText.Text = "请说话，完成后再次点击按钮。";
            }
            catch (Exception ex)
            {
                StatusText.Text = "无法开始录音";
                TranscriptText.Text = ex.Message;
            }

            return;
        }

        SetBusy(true, "正在结束录音…");
        try
        {
            using var recorded = await recorder.StopAsync();
            await TranscribeAsync(recorded.Content, recorded.FileName);
        }
        catch (Exception ex)
        {
            StatusText.Text = "录音或识别失败";
            TranscriptText.Text = ex.Message;
        }
        finally
        {
            RecordButton.Content = "开始录音";
            SetBusy(false, StatusText.Text ?? "等待选择音频");
        }
    }

    private async Task TranscribeAsync(Stream stream, string sourceName)
    {
        var settings = _app.Settings;
        var result = await _app.TranscriptionProvider.TranscribeAsync(
            stream,
            new TranscriptionOptions(settings.Model, settings.Language));
        _lastTranscript = result.Text.Trim();
        TranscriptText.Text = string.IsNullOrWhiteSpace(_lastTranscript)
            ? "没有识别到有效语音。"
            : _lastTranscript;
        StatusText.Text = $"识别完成：{sourceName}";
        CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastTranscript);
    }

    private async void CopyClick(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastTranscript))
        {
            await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(_lastTranscript);
            StatusText.Text = "已复制到剪贴板";
        }
    }

    private void SaveSettingsClick(object? sender, RoutedEventArgs e)
    {
        var current = _app.Settings;
        var mode = (ModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "LocalFirst";
        var model = (ModelComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "SenseVoiceSmall";
        var parsedMode = Enum.TryParse<TranscriptionMode>(mode, out var modeValue)
            ? modeValue
            : TranscriptionMode.LocalFirst;
        var parsedModel = Enum.TryParse<LocalModelVariant>(model, out var modelValue)
            ? modelValue
            : LocalModelVariant.SenseVoiceSmall;
        _app.SaveSettings(current with
        {
            TranscriptionMode = parsedMode,
            LocalModelVariant = parsedModel,
            MacOsPythonPath = PythonPathTextBox.Text?.Trim() ?? current.MacOsPythonPath,
            Hotkey = HotkeyTextBox.Text?.Trim() ?? current.Hotkey,
            AutoSend = AutoSendCheckBox.IsChecked ?? true
        });
        StatusText.Text = "设置已保存";
    }

    private void LoadSettings(ClapBackSettings settings)
    {
        ModeComboBox.SelectedIndex = (int)settings.TranscriptionMode;
        ModelComboBox.SelectedIndex = (int)settings.LocalModelVariant;
        PythonPathTextBox.Text = settings.MacOsPythonPath;
        HotkeyTextBox.Text = settings.Hotkey;
        AutoSendCheckBox.IsChecked = settings.AutoSend;
    }

    private void SetBusy(bool busy, string status)
    {
        ChooseAudioButton.IsEnabled = !busy;
        RecordButton.IsEnabled = !busy && _app.AudioRecorder is not null;
        StatusText.Text = status;
        if (busy) CopyButton.IsEnabled = false;
    }
}

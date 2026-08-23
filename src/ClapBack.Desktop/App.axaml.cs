using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;
using ClapBack.Stt;

namespace ClapBack.Desktop;

public partial class App : Application
{
    public ClapBackSettings Settings { get; private set; } = new();
    public ITranscriptionProvider TranscriptionProvider { get; private set; } = null!;
    public IAudioRecorder? AudioRecorder { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Settings = DesktopSettingsStore.Load();
        AudioRecorder = OperatingSystem.IsMacOS()
            ? new MacOsMicrophoneRecorder(() => Settings)
            : null;
        ITranscriptionProvider localProvider = OperatingSystem.IsMacOS()
            ? new MacOsFunAsrTranscriptionProvider(() => Settings)
            : new LocalSenseVoiceTranscriptionProvider(() => Settings);
        // The cross-platform shell does not persist secrets. Cloud mode can be
        // enabled for a development build by supplying CLAPBACK_STT_API_KEY;
        // Windows production uses its Credential Manager-backed WPF path.
        var cloudProvider = new WhisperCompatibleTranscriptionProvider(
            new HttpClient { Timeout = TimeSpan.FromSeconds(45) },
            () => Settings,
            _ => Task.FromResult(Environment.GetEnvironmentVariable("CLAPBACK_STT_API_KEY")));
        TranscriptionProvider = new CompositeTranscriptionProvider(
            localProvider,
            cloudProvider,
            () => Settings);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(this);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void SaveSettings(ClapBackSettings settings)
    {
        Settings = settings;
        DesktopSettingsStore.Save(settings);
    }
}

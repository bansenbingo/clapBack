using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;
using ClapBack.Stt;
using ClapBack.Stt.Audio;

namespace ClapBack.Desktop;

public partial class App : Application
{
    public ClapBackSettings Settings { get; private set; } = new();
    public ITranscriptionProvider TranscriptionProvider { get; private set; } = null!;
    public IAudioRecorder? AudioRecorder { get; private set; }

    /// <summary>麦克风设备枚举。没有能力枚举的平台会得到 <see cref="EmptyAudioInputDeviceCatalog"/>。</summary>
    public IAudioInputDeviceCatalog MicrophoneCatalog { get; private set; } = new EmptyAudioInputDeviceCatalog();

    /// <summary>启动时检测并安装运行环境（运行时 exe / 识别模型 / VAD）。</summary>
    public RuntimeProvisioner Provisioner { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 第一次运行 exe 就在它旁边把 models/、runtime/ 建出来。
        // 玩家应该当场看到 models 文件夹已经存在，而不是等到第一次下载才冒出来。
        global::ClapBack.Core.AppPaths.EnsureDirectories();

        Settings = DesktopSettingsStore.Load();
        Provisioner = new RuntimeProvisioner();

        if (OperatingSystem.IsWindows())
        {
            MicrophoneCatalog = new WindowsMicrophoneCatalog();
            AudioRecorder = new WavAudioRecorder(() => ResolveInputDevice(MicrophoneCatalog, Settings));
        }
        else if (OperatingSystem.IsMacOS())
        {
            MicrophoneCatalog = new MacOsMicrophoneCatalog(() => Settings);
            AudioRecorder = new MacOsMicrophoneRecorder(() => Settings, () => ResolveInputDevice(MicrophoneCatalog, Settings));
        }

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
            desktop.Exit += (_, _) =>
            {
                if (AudioRecorder is not null)
                {
                    AudioRecorder.CancelAsync().GetAwaiter().GetResult();
                }

                (TranscriptionProvider as IDisposable)?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 按设备名解析麦克风下标；匹配不到返回 <c>-1</c>，交给录音器去用系统默认设备。
    /// 绝不「找不到就随便挑一个」——用户选的麦克风拔了时悄悄换硬件比报错更糟。
    /// </summary>
    private static int ResolveInputDevice(IAudioInputDeviceCatalog catalog, ClapBackSettings settings)
        => catalog.ResolveIndex(settings.MicrophoneDeviceId) ?? -1;

    public void SaveSettings(ClapBackSettings settings)
    {
        DesktopSettingsStore.Save(settings);
        Settings = settings;
    }

}

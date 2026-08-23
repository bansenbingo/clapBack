namespace ClapBack.Core.Models;

public enum TranscriptionMode
{
    LocalFirst,
    LocalOnly,
    CloudOnly
}

public enum LocalModelVariant
{
    SenseVoiceSmall,
    FunAsrNano2512,
    ParaformerZh
}

public sealed record ClapBackSettings
{
    public string Hotkey { get; init; } = "CapsLock";
    public int InputDeviceNumber { get; init; }
    public int MaxRecordingSeconds { get; init; } = 30;
    public string Language { get; init; } = "zh";
    public TranscriptionMode TranscriptionMode { get; init; } = TranscriptionMode.LocalOnly;
    public LocalModelVariant LocalModelVariant { get; init; } = LocalModelVariant.SenseVoiceSmall;

    // Local SenseVoiceSmall GGUF defaults. Relative paths are resolved from the app directory.
    public string LocalSttRuntimePath { get; init; } = "runtime\\llama-funasr-sensevoice.exe";
    public string LocalModelPath { get; init; } = "models\\sensevoice-small-q8.gguf";
    public string LocalVadModelPath { get; init; } = string.Empty;
    public string LocalModelSha256 { get; init; } = string.Empty;
    // The GGUF model is downloaded on first local use and kept outside Git.
    public bool LocalModelAutoDownload { get; init; } = true;
    public string HuggingFaceRepository { get; init; } = "FunAudioLLM/SenseVoiceSmall-GGUF";
    public string HuggingFaceRevision { get; init; } = "main";
    public string HuggingFaceFile { get; init; } = "sensevoice-small-q8.gguf";
    public int HuggingFaceTimeoutSeconds { get; init; } = 20;
    public string LocalSttBackend { get; init; } = "cpu";
    public int LocalSttThreads { get; init; } = 2;
    public int LocalSttTimeoutSeconds { get; init; } = 20;

    // macOS experiment settings. The Python adapter is intentionally separate
    // from the Windows WPF runtime and uses FunASR's ModelScope hub directly.
    public string MacOsPythonPath { get; init; } = ".venv-funasr/bin/python";
    public string MacOsFunAsrScriptPath { get; init; } = "tools/macos_funasr_transcribe.py";
    public string MacOsFunAsrModel { get; init; } = "iic/SenseVoiceSmall";
    public string MacOsFunAsrDevice { get; init; } = "auto";
    public int MacOsSttTimeoutSeconds { get; init; } = 300;

    // Cloud settings are retained for an optional provider or explicit fallback.
    public string Model { get; init; } = "glm-asr-2512";
    public string SttBaseUrl { get; init; } = "https://api.openai.com/v1";
    public string CloudProtocol { get; init; } = "WhisperCompatible";
    public string ChatOpenKey { get; init; } = "Enter";
    public bool AutoSend { get; init; } = true;
    public bool RestoreClipboard { get; init; } = true;
    public bool PlaySounds { get; init; } = true;
    public bool StartWithWindows { get; init; }
}

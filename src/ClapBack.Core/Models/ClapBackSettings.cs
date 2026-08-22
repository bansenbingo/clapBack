namespace ClapBack.Core.Models;

public sealed record ClapBackSettings
{
    public string Hotkey { get; init; } = "F13";
    public int InputDeviceNumber { get; init; }
    public int MaxRecordingSeconds { get; init; } = 30;
    public string Language { get; init; } = "zh";
    public string Model { get; init; } = "whisper-1";
    public string SttBaseUrl { get; init; } = "https://api.openai.com/v1";
    public string ChatOpenKey { get; init; } = "Enter";
    public bool AutoSend { get; init; } = true;
    public bool RestoreClipboard { get; init; } = true;
    public bool PlaySounds { get; init; } = true;
    public bool StartWithWindows { get; init; }
}

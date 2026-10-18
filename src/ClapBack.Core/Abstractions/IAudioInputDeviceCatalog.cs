namespace ClapBack.Core.Abstractions;

/// <summary>
/// 一个可选的麦克风输入设备。<see cref="Id"/> 是设备名（重启后仍稳定），<see cref="Index"/> 是本次枚举里的下标。
/// 记设备名而不是下标：下标会随插拔、驱动更新而变，把下标写进配置会让用户换到别的麦克风上。
/// </summary>
public sealed record AudioInputDevice(string Id, string Name, int Index);

/// <summary>枚举本机可用的麦克风输入设备。</summary>
public interface IAudioInputDeviceCatalog
{
    /// <summary>列出当前可用的输入设备。没有能力枚举时返回空列表。</summary>
    IReadOnlyList<AudioInputDevice> List();

    /// <summary>
    /// 按设备名解析下标。找不到返回 <c>null</c>——调用方应回退系统默认设备，
    /// 而不是随便挑一个，避免「用户选的麦克风拔了，程序悄悄换到别的硬件」。
    /// </summary>
    int? ResolveIndex(string? deviceId);
}

/// <summary>没有设备枚举能力时的占位实现。</summary>
public sealed class EmptyAudioInputDeviceCatalog : IAudioInputDeviceCatalog
{
    public IReadOnlyList<AudioInputDevice> List() => Array.Empty<AudioInputDevice>();

    public int? ResolveIndex(string? deviceId) => null;
}

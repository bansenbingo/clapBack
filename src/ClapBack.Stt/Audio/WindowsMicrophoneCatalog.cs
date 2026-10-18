using ClapBack.Core.Abstractions;
using NAudio.Wave;

namespace ClapBack.Stt.Audio;

/// <summary>
/// Windows 麦克风输入设备枚举（NAudio / WaveInEvent）。
///
/// 只在 Windows 上调用；其他平台会返回空列表，由调用方回退到各自的实现。
/// 设备名直接取 NAudio 的 <c>ProductName</c>，并同时用它作为 <see cref="AudioInputDevice.Id"/>——
/// 配置里存名字而不是下标，插拔/驱动更新后仍能选回同一个麦克风。
/// </summary>
public sealed class WindowsMicrophoneCatalog : IAudioInputDeviceCatalog
{
    public IReadOnlyList<AudioInputDevice> List()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<AudioInputDevice>();
        }

        var devices = new List<AudioInputDevice>();
        for (var index = 0; index < WaveInEvent.DeviceCount; index++)
        {
            var capabilities = WaveInEvent.GetCapabilities(index);
            var name = string.IsNullOrWhiteSpace(capabilities.ProductName)
                ? $"输入设备 {index}"
                : capabilities.ProductName.Trim();
            devices.Add(new AudioInputDevice(name, name, index));
        }

        return devices;
    }

    public int? ResolveIndex(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        foreach (var device in List())
        {
            if (string.Equals(device.Id, deviceId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return device.Index;
            }
        }

        return null;
    }
}

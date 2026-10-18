using System.Diagnostics;
using System.Text;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;

namespace ClapBack.Desktop;

/// <summary>
/// macOS 麦克风输入设备枚举：调用 <c>tools/macos_funasr_record.py --list</c> 拿 sounddevice 的设备表。
///
/// 不直接引用 sounddevice——它装在仓库的 <c>.venv-funasr</c> 里，只能经由那个 Python 解释器访问。
/// 解析失败或解释器缺失时返回空列表，由界面回退「系统默认输入设备」，不会静默挑一个别的硬件。
/// </summary>
internal sealed class MacOsMicrophoneCatalog : IAudioInputDeviceCatalog
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<ClapBackSettings> _settings;

    public MacOsMicrophoneCatalog(Func<ClapBackSettings> settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<AudioInputDevice> List()
    {
        var settings = _settings();
        if (string.IsNullOrWhiteSpace(settings.MacOsPythonPath) ||
            string.IsNullOrWhiteSpace(settings.MacOsFunAsrRecordScriptPath))
        {
            return Array.Empty<AudioInputDevice>();
        }

        try
        {
            var script = ResolvePath(settings.MacOsFunAsrRecordScriptPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = settings.MacOsPythonPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add("--list");

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return Array.Empty<AudioInputDevice>();
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit((int)ListTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return Array.Empty<AudioInputDevice>();
            }

            return ClapBack.Stt.Audio.AudioDeviceListParser.Parse(stdout);
        }
        catch (Exception)
        {
            // 没有可枚举的设备时界面会回退到系统默认设备，因此这里安静返回空列表。
            return Array.Empty<AudioInputDevice>();
        }
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

    private static string ResolvePath(string path)
        => Path.IsPathRooted(path) ? path : Path.GetFullPath(path, AppContext.BaseDirectory);
}

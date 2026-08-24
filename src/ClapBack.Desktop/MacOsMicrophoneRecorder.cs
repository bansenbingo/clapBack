using System.Diagnostics;
using System.Text;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;

namespace ClapBack.Desktop;

/// <summary>
/// macOS microphone recorder backed by the repository's Python environment.
/// PortAudio handles the native microphone permission and device APIs; the
/// .NET side only controls the short-lived capture process.
/// </summary>
internal sealed class MacOsMicrophoneRecorder : IAudioRecorder
{
    private readonly Func<ClapBackSettings> _settings;
    private readonly object _sync = new();
    private Process? _process;
    private string? _audioPath;
    private Task<string>? _standardErrorTask;

    public MacOsMicrophoneRecorder(Func<ClapBackSettings> settings)
    {
        _settings = settings;
    }

    public bool IsRecording
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_process is { HasExited: false })
            {
                throw new InvalidOperationException("已经在录音");
            }

            var settings = _settings();
            var python = ResolvePath(settings.MacOsPythonPath);
            var script = ResolvePath("tools/macos_funasr_record.py");
            if (!File.Exists(python))
            {
                throw new FileNotFoundException("macOS FunASR Python 环境不存在，请先运行 scripts/macos-funasr-setup.sh", python);
            }

            if (!File.Exists(script))
            {
                throw new FileNotFoundException("macOS 麦克风录音脚本不存在", script);
            }

            _audioPath = Path.Combine(Path.GetTempPath(), $"clapback-macos-record-{Guid.NewGuid():N}.wav");
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(_audioPath);
            startInfo.ArgumentList.Add("--max-seconds");
            startInfo.ArgumentList.Add(Math.Clamp(settings.MaxRecordingSeconds, 1, 300).ToString());
            if (settings.InputDeviceNumber > 0)
            {
                startInfo.ArgumentList.Add("--device");
                startInfo.ArgumentList.Add(settings.InputDeviceNumber.ToString());
            }

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("无法启动 macOS 麦克风录音进程");
            }

            _process = process;
            // Drain both pipes immediately so a verbose PortAudio diagnostic
            // cannot block the child process; retain stderr for a useful GUI
            // error when permission or dependency checks fail.
            _ = process.StandardOutput.ReadToEndAsync();
            _standardErrorTask = process.StandardError.ReadToEndAsync();
        }

        return Task.CompletedTask;
    }

    public async Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
    {
        Process process;
        string audioPath;
        lock (_sync)
        {
            process = _process ?? throw new InvalidOperationException("当前没有进行中的录音");
            audioPath = _audioPath ?? throw new InvalidOperationException("录音文件路径未初始化");
        }

        try
        {
            await process.StandardInput.WriteLineAsync("stop").WaitAsync(cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var error = _standardErrorTask is null
                ? string.Empty
                : await _standardErrorTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(audioPath))
            {
                var details = string.IsNullOrWhiteSpace(error)
                    ? "请检查系统麦克风权限和 FunASR Python 依赖"
                    : error.Trim();
                throw new InvalidOperationException($"macOS 麦克风录音失败：{Sanitize(details)}");
            }

            var bytes = await File.ReadAllBytesAsync(audioPath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 44)
            {
                throw new InvalidDataException("录音文件为空或格式无效");
            }

            if (WavSignalInspector.IsSilent(bytes))
            {
                throw new InvalidOperationException("没有采集到有效语音信号，请检查 macOS 麦克风权限、输入设备和系统输入音量");
            }

            return new RecordedAudio(new MemoryStream(bytes, writable: false), "clapback-macos.wav");
        }
        finally
        {
            await CleanupAsync(process, audioPath).ConfigureAwait(false);
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        Process? process;
        string? audioPath;
        lock (_sync)
        {
            process = _process;
            audioPath = _audioPath;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (audioPath is not null)
            {
                TryDelete(audioPath);
            }

            lock (_sync)
            {
                _process = null;
                _audioPath = null;
                _standardErrorTask = null;
            }

            process.Dispose();
        }
    }

    private async Task CleanupAsync(Process process, string audioPath)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
        }
        TryDelete(audioPath);
        lock (_sync)
        {
            _process = null;
            _audioPath = null;
            _standardErrorTask = null;
        }

        process.Dispose();
        await Task.CompletedTask;
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var workingPath = Path.GetFullPath(path, Directory.GetCurrentDirectory());
        if (File.Exists(workingPath)) return workingPath;

        var appPath = Path.GetFullPath(path, AppContext.BaseDirectory);
        if (File.Exists(appPath)) return appPath;

        // A Finder-launched bundle normally has / as its working directory.
        // Walk up from Contents/MacOS so the development repository's
        // .venv-funasr can still be found when the app is built under dist/.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, path);
            if (File.Exists(candidate)) return candidate;
        }

        return appPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Sanitize(string value) =>
        value.Length <= 1000 ? value : value[..1000] + "…";

    private static class WavSignalInspector
    {
        public static bool IsSilent(byte[] wav)
        {
            const int headerSize = 44;
            if (wav.Length <= headerSize) return true;

            long sumSquares = 0;
            var sampleCount = 0;
            for (var offset = headerSize; offset + 1 < wav.Length; offset += 2)
            {
                var sample = (short)(wav[offset] | (wav[offset + 1] << 8));
                sumSquares += (long)sample * sample;
                sampleCount++;
            }

            if (sampleCount == 0) return true;
            var rms = Math.Sqrt(sumSquares / (double)sampleCount);
            return rms < 80;
        }
    }
}

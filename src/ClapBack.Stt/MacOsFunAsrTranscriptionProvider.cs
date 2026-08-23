using System.Diagnostics;
using System.Text;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;

namespace ClapBack.Stt;

/// <summary>
/// macOS experiment adapter. It keeps the production Windows path unchanged
/// and delegates one transcription request to the repository's Python FunASR
/// script, which downloads/loads the ModelScope SenseVoice model.
/// </summary>
public sealed class MacOsFunAsrTranscriptionProvider : ITranscriptionProvider
{
    private readonly Func<ClapBackSettings> _settings;

    public MacOsFunAsrTranscriptionProvider(Func<ClapBackSettings> settings)
    {
        _settings = settings;
    }

    public async Task<TranscriptResult> TranscribeAsync(
        Stream audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (!audio.CanRead)
        {
            throw new ArgumentException("音频流不可读", nameof(audio));
        }

        var settings = _settings();
        var pythonPath = ResolvePath(settings.MacOsPythonPath);
        var scriptPath = ResolvePath(settings.MacOsFunAsrScriptPath);
        if (!File.Exists(pythonPath))
        {
            throw new FileNotFoundException("macOS FunASR Python 环境不存在，请先运行 scripts/macos-funasr-setup.sh", pythonPath);
        }

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("macOS FunASR 适配脚本不存在", scriptPath);
        }

        var audioPath = Path.Combine(Path.GetTempPath(), $"clapback-macos-{Guid.NewGuid():N}.wav");
        try
        {
            if (audio.CanSeek)
            {
                audio.Position = 0;
            }

            await using (var output = File.Create(audioPath))
            {
                await audio.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add(audioPath);
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(settings.MacOsFunAsrModel);
            startInfo.ArgumentList.Add("--device");
            startInfo.ArgumentList.Add(settings.MacOsFunAsrDevice);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 macOS FunASR Python 适配器");
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.MacOsSttTimeoutSeconds, 1, 1800)));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException("macOS FunASR 识别超时");
            }

            var text = (await stdout.ConfigureAwait(false)).Trim();
            var error = (await stderr.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"macOS FunASR 退出码 {process.ExitCode}：{Sanitize(error)}");
            }

            return new TranscriptResult(text);
        }
        finally
        {
            TryDelete(audioPath);
        }
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var workingDirectoryPath = Path.GetFullPath(path, Directory.GetCurrentDirectory());
        if (File.Exists(workingDirectoryPath))
        {
            return workingDirectoryPath;
        }

        var appPath = Path.GetFullPath(path, AppContext.BaseDirectory);
        if (File.Exists(appPath))
        {
            return appPath;
        }

        // Finder launches apps with a non-project working directory. Search
        // bundle parents so a development .venv-funasr beside dist/ remains
        // usable without asking the user to start the app from a shell.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, path);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return appPath;
    }

    private static string Sanitize(string value) =>
        value.Length <= 1000 ? value : value[..1000] + "…";

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
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
}

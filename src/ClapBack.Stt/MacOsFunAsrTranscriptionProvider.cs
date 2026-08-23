using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;

namespace ClapBack.Stt;

/// <summary>
/// macOS FunASR provider. A single Python sidecar keeps SenseVoiceSmall loaded
/// between requests; this avoids paying the 936 MB model-load cost for every
/// recording. The sidecar communicates over newline-delimited JSON on stdin.
/// </summary>
public sealed class MacOsFunAsrTranscriptionProvider : ITranscriptionProvider, IDisposable
{
    private const string ProtocolPrefix = "CLAPBACK_JSON:";
    private static readonly JsonSerializerOptions ProtocolJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly Func<ClapBackSettings> _settings;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly object _processSync = new();
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private Task<string>? _standardErrorTask;

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
        var scriptPath = ResolvePath("tools/macos_funasr_server.py");
        if (!File.Exists(pythonPath))
        {
            throw new FileNotFoundException("macOS FunASR Python 环境不存在，请先运行 scripts/macos-funasr-setup.sh", pythonPath);
        }

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("macOS FunASR 常驻服务脚本不存在", scriptPath);
        }

        var audioPath = Path.Combine(Path.GetTempPath(), $"clapback-macos-{Guid.NewGuid():N}.wav");
        try
        {
            if (audio.CanSeek) audio.Position = 0;
            await using (var output = File.Create(audioPath))
            {
                await audio.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureServerAsync(pythonPath, scriptPath, settings, cancellationToken).ConfigureAwait(false);
                var request = JsonSerializer.Serialize(new ServerRequest(
                    audioPath,
                    string.IsNullOrWhiteSpace(options.Language) ? "zh" : options.Language),
                    ProtocolJsonOptions);
                await _input!.WriteLineAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false);
                await _input.FlushAsync(cancellationToken).ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.MacOsSttTimeoutSeconds, 1, 1800)));
                var responseLine = await ReadProtocolLineAsync(_output!, timeout.Token).ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<ServerResponse>(responseLine, ProtocolJsonOptions);
                if (response is null || !response.Ok)
                {
                    throw new InvalidOperationException($"macOS FunASR 识别失败：{Sanitize(response?.Error ?? "未知错误")}");
                }

                return new TranscriptResult((response.Text ?? string.Empty).Trim());
            }
            catch
            {
                // A broken sidecar must not poison subsequent requests. The
                // next call will start a fresh model process.
                StopServer();
                throw;
            }
            finally
            {
                _requestLock.Release();
            }
        }
        finally
        {
            TryDelete(audioPath);
        }
    }

    private async Task EnsureServerAsync(
        string pythonPath,
        string scriptPath,
        ClapBackSettings settings,
        CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _input is not null && _output is not null)
        {
            return;
        }

        StopServer();
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(settings.MacOsFunAsrModel);
        startInfo.ArgumentList.Add("--device");
        // MPS can be fast but has produced unstable results on some Apple
        // Silicon/PyTorch combinations. CPU is the correctness-first default;
        // users can explicitly set MacOsFunAsrDevice to mps after validation.
        var device = settings.MacOsFunAsrDevice.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "cpu"
            : settings.MacOsFunAsrDevice;
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(device) ? "cpu" : device);

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("无法启动 macOS FunASR 常驻服务");
        }

        _process = process;
        _input = process.StandardInput;
        _output = process.StandardOutput;
        _standardErrorTask = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.MacOsSttTimeoutSeconds, 10, 1800)));
        string readyLine;
        try
        {
            readyLine = await ReadProtocolLineAsync(_output, timeout.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            var error = _standardErrorTask is null ? string.Empty : await _standardErrorTask.ConfigureAwait(false);
            StopServer();
            throw new InvalidOperationException($"FunASR 模型加载失败：{Sanitize(error)}");
        }
        var ready = JsonSerializer.Deserialize<ReadyResponse>(readyLine, ProtocolJsonOptions);
        if (ready?.Ready != true)
        {
            StopServer();
            throw new InvalidOperationException($"FunASR 模型加载失败：{Sanitize(ready?.Error ?? readyLine)}");
        }
    }

    public void Dispose()
    {
        StopServer();
        _requestLock.Dispose();
    }

    private void StopServer()
    {
        lock (_processSync)
        {
            var process = _process;
            _process = null;
            _input = null;
            _output = null;
            _standardErrorTask = null;
            if (process is null) return;

            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var workingPath = Path.GetFullPath(path, Directory.GetCurrentDirectory());
        if (File.Exists(workingPath)) return workingPath;
        var appPath = Path.GetFullPath(path, AppContext.BaseDirectory);
        if (File.Exists(appPath)) return appPath;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, path);
            if (File.Exists(candidate)) return candidate;
        }

        return appPath;
    }

    private static string Sanitize(string value) =>
        value.Length <= 1000 ? value : value[..1000] + "…";

    private static async Task<string> ReadProtocolLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidOperationException("macOS FunASR 服务意外退出");
            }

            var marker = line.IndexOf(ProtocolPrefix, StringComparison.Ordinal);
            if (marker < 0)
            {
                // Ignore tqdm/model diagnostics accidentally written to stdout.
                continue;
            }

            var json = line[(marker + ProtocolPrefix.Length)..].Trim();
            if (json.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.GetRawText();
            }
            catch (JsonException)
            {
                // A partial progress line can contain the prefix; keep waiting
                // for the complete protocol message.
            }
        }
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

    private sealed record ServerRequest(string Audio, string Language);
    private sealed record ReadyResponse(bool Ready, string? Device = null, string? Error = null);
    private sealed record ServerResponse(bool Ok, string? Text = null, string? Error = null);
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;

namespace ClapBack.Stt;

/// <summary>
/// Runs the FunASR llama.cpp SenseVoiceSmall executable locally.
/// The executable and model are deliberately external artifacts so the WPF app
/// does not need a Python or GPU-specific .NET dependency.
/// </summary>
public sealed class LocalSenseVoiceTranscriptionProvider : ITranscriptionProvider
{
    private readonly Func<ClapBackSettings> _settings;
    private readonly HuggingFaceModelDownloader _modelDownloader;
    private readonly object _validationSync = new();
    private string? _validatedModelPath;
    private string? _validatedModelSha256;

    public LocalSenseVoiceTranscriptionProvider(
        Func<ClapBackSettings> settings,
        HuggingFaceModelDownloader? modelDownloader = null)
    {
        _settings = settings;
        _modelDownloader = modelDownloader ?? new HuggingFaceModelDownloader();
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
        var runtimePath = ResolvePath(settings.LocalSttRuntimePath);
        var artifact = LocalModelCatalog.Get(settings.LocalModelVariant);
        var configuredModelPath = settings.LocalModelPath;
        if (string.IsNullOrWhiteSpace(configuredModelPath)
            || (settings.LocalModelVariant != LocalModelVariant.SenseVoiceSmall
                && string.Equals(configuredModelPath, "models\\sensevoice-small-q8.gguf", StringComparison.OrdinalIgnoreCase)))
        {
            configuredModelPath = artifact.DefaultLocalPath;
        }

        var modelPath = ResolvePath(configuredModelPath);
        var vadPath = ResolveOptionalPath(settings.LocalVadModelPath);
        ValidateRuntime(runtimePath);
        if (settings.LocalModelAutoDownload)
        {
            // First local use downloads the configured SenseVoiceSmall GGUF from
            // Hugging Face into the ignored models/ directory.
            var download = await _modelDownloader.EnsureFileAsync(
                modelPath,
                artifact.HuggingFaceRepository,
                artifact.HuggingFaceRevision,
                artifact.HuggingFaceFile,
                artifact.ModelScopeRepository,
                artifact.ModelScopeRevision,
                artifact.ModelScopeFile,
                settings.HuggingFaceTimeoutSeconds,
                cancellationToken).ConfigureAwait(false);
            if (download.UsedModelScope && !artifact.SupportsCurrentGgufRuntime)
            {
                TryDelete(modelPath);
                throw new InvalidOperationException(
                    $"{artifact.DisplayName} 的 ModelScope 回退文件是 {artifact.ModelScopeFile}（{artifact.Format}），当前 llama.cpp 运行时仅支持 GGUF；请安装 FunASR/PyTorch 运行时或切换到 Hugging Face 可用网络。");
            }
        }

        await ValidateModelAsync(modelPath, settings.LocalModelSha256, cancellationToken).ConfigureAwait(false);
        if (vadPath is not null && !File.Exists(vadPath))
        {
            throw new FileNotFoundException("本地 VAD 模型不存在", vadPath);
        }

        var temporaryAudioPath = Path.Combine(
            Path.GetTempPath(),
            $"clapback-{Guid.NewGuid():N}.wav");
        try
        {
            await WriteAudioAsync(audio, temporaryAudioPath, cancellationToken).ConfigureAwait(false);
            var output = await RunRuntimeAsync(
                runtimePath,
                modelPath,
                vadPath,
                temporaryAudioPath,
                settings,
                cancellationToken).ConfigureAwait(false);
            return new TranscriptResult(output.Trim());
        }
        finally
        {
            TryDelete(temporaryAudioPath);
        }
    }

    private async Task<string> RunRuntimeAsync(
        string runtimePath,
        string modelPath,
        string? vadPath,
        string audioPath,
        ClapBackSettings settings,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = runtimePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add(modelPath);
        if (vadPath is not null)
        {
            process.StartInfo.ArgumentList.Add("--vad");
            process.StartInfo.ArgumentList.Add(vadPath);
        }

        process.StartInfo.ArgumentList.Add("-a");
        process.StartInfo.ArgumentList.Add(audioPath);
        var backend = NormalizeBackend(settings.LocalSttBackend);
        if (backend is not null)
        {
            process.StartInfo.ArgumentList.Add("--backend");
            process.StartInfo.ArgumentList.Add(backend);
        }

        var threads = Math.Clamp(settings.LocalSttThreads, 1, 64);
        // The FunASR binary uses ggml internally. These variables are harmless for
        // older builds and provide a resource cap for builds honoring OpenMP/ggml.
        process.StartInfo.Environment["OMP_NUM_THREADS"] = threads.ToString();
        process.StartInfo.Environment["GGML_NUM_THREADS"] = threads.ToString();

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动本地语音识别运行时");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"无法启动本地语音识别运行时：{runtimePath}", ex);
        }

        TryLowerPriority(process);
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        var timeoutSeconds = Math.Clamp(settings.LocalSttTimeoutSeconds, 1, 300);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"本地语音识别超过 {timeoutSeconds} 秒");
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKill(process);
            }
        }

        var output = await standardOutputTask.ConfigureAwait(false);
        var error = await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"本地语音识别运行时退出码 {process.ExitCode}：{Sanitize(error)}");
        }

        return output;
    }

    private async Task ValidateModelAsync(
        string modelPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("本地语音模型不存在", modelPath);
        }

        var fileInfo = new FileInfo(modelPath);
        if (fileInfo.Length == 0)
        {
            throw new InvalidDataException("本地语音模型文件为空");
        }

        var normalizedExpected = expectedSha256.Trim().ToUpperInvariant();
        lock (_validationSync)
        {
            if (string.Equals(_validatedModelPath, modelPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_validatedModelSha256, normalizedExpected, StringComparison.Ordinal))
            {
                return;
            }
        }

        if (normalizedExpected.Length > 0)
        {
            await using var stream = File.OpenRead(modelPath);
            var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actual, normalizedExpected, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"本地语音模型 SHA-256 校验失败，应为 {normalizedExpected}，实际为 {actual}");
            }
        }

        lock (_validationSync)
        {
            _validatedModelPath = modelPath;
            _validatedModelSha256 = normalizedExpected;
        }
    }

    private static async Task WriteAudioAsync(
        Stream audio,
        string path,
        CancellationToken cancellationToken)
    {
        if (audio.CanSeek)
        {
            audio.Position = 0;
        }

        await using var output = File.Create(path);
        await audio.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateRuntime(string runtimePath)
    {
        if (!File.Exists(runtimePath))
        {
            throw new FileNotFoundException("本地语音识别运行时不存在", runtimePath);
        }
    }

    private static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("未配置本地语音识别路径");
        }

        return Path.GetFullPath(path, AppContext.BaseDirectory);
    }

    private static string? ResolveOptionalPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? null : ResolvePath(path);

    private static string? NormalizeBackend(string backend) =>
        backend.Trim().ToLowerInvariant() switch
        {
            "" or "cpu" => null,
            "vulkan" => "vulkan",
            "cuda" => "cuda",
            _ => throw new InvalidOperationException("本地推理后端只能是 cpu、vulkan 或 cuda")
        };

    private static void TryLowerPriority(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (InvalidOperationException)
        {
            // Some platforms/runtimes do not allow changing child priority.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Lack of permission must not make an otherwise valid transcription fail.
        }
        catch (PlatformNotSupportedException)
        {
            // Priority control is optional on non-Windows test hosts.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best-effort cleanup only.
        }
        catch (PlatformNotSupportedException)
        {
            // Best-effort cleanup only.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Temporary audio cleanup must not hide the recognition result.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary audio cleanup must not hide the recognition result.
        }
    }

    private static string Sanitize(string value)
    {
        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 500 ? compact : compact[..500] + "…";
    }
}

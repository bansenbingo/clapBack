using System.Diagnostics;
using System.Text;
using ClapBack.Core;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;

namespace ClapBack.Stt;

/// <summary>
/// Runs the FunASR llama.cpp SenseVoiceSmall executable locally.
/// The executable and model are deliberately external artifacts so the WPF app
/// does not need a Python or GPU-specific .NET dependency.
///
/// 安全要点：
/// <list type="bullet">
/// <item>所有配置路径必须是<b>应用目录内的相对路径</b>，拒绝绝对路径与 <c>..</c> 逃逸
/// （否则 settings.json 被改就能指向任意可执行文件或覆盖任意文件）。</item>
/// <item>模型与运行时 exe 都走 SHA-256 完整性链路（见 <see cref="ModelIntegrity"/>）。</item>
/// <item>临时音频统一写在应用目录下的 audio-temp，且每次识别清理过期残留。</item>
/// <item>抛给上层的异常消息只含文件名，不含用户完整路径。</item>
/// </list>
/// </summary>
public sealed class LocalSenseVoiceTranscriptionProvider : ITranscriptionProvider
{
    private static readonly TimeSpan TempAudioRetention = TimeSpan.FromDays(1);

    private readonly Func<ClapBackSettings> _settings;
    private readonly HuggingFaceModelDownloader _modelDownloader;

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
        var modelPath = ResolveModelPath(settings);
        var expectedModelSha256 = artifact.ResolveExpectedSha256(settings.LocalModelSha256);

        // 运行时 exe：必须存在，且必须通过完整性校验（首次使用时建立指纹，之后每次复检）。
        if (!File.Exists(runtimePath))
        {
            throw new FileNotFoundException("本地语音识别运行时不存在", Path.GetFileName(runtimePath));
        }

        await _modelDownloader.VerifyExistingFileAsync(
            runtimePath,
            settings.LocalRuntimeSha256,
            requireIntegrity: settings.RequireModelIntegrity,
            allowFirstTrust: true,
            cancellationToken).ConfigureAwait(false);

        if (settings.LocalModelAutoDownload)
        {
            // First local use downloads the configured GGUF from Hugging Face into
            // the ignored models/ directory. Integrity is enforced inside the downloader.
            var download = await _modelDownloader.EnsureFileAsync(
                modelPath,
                artifact.HuggingFaceRepository,
                artifact.HuggingFaceRevision,
                artifact.HuggingFaceFile,
                artifact.ModelScopeRepository,
                artifact.ModelScopeRevision,
                artifact.ModelScopeFile,
                settings.HuggingFaceTimeoutSeconds,
                settings.ModelDownloadSource,
                expectedModelSha256,
                settings.RequireModelIntegrity,
                settings.HuggingFaceEndpoint,
                settings.ModelScopeEndpoint,
                cancellationToken).ConfigureAwait(false);
            if (download.UsedModelScope && !artifact.SupportsCurrentGgufRuntime)
            {
                TryDelete(modelPath);
                throw new InvalidOperationException(
                    $"{artifact.DisplayName} 的 ModelScope 回退文件是 {artifact.ModelScopeFile}（{artifact.Format}），当前 llama.cpp 运行时仅支持 GGUF；请安装 FunASR/PyTorch 运行时或切换到 Hugging Face 可用网络。");
            }
        }
        else
        {
            // 不自动下载：只校验用户自行放置的模型文件，不接受未经自证的文件。
            await _modelDownloader.VerifyExistingFileAsync(
                modelPath,
                expectedModelSha256,
                requireIntegrity: settings.RequireModelIntegrity,
                allowFirstTrust: false,
                cancellationToken).ConfigureAwait(false);
        }

        // VAD 只负责长音频切分；未安装时让短语音直接交给识别模型。
        var vadPath = ResolveAvailableVadPath(settings);

        var temporaryAudioPath = CreateTemporaryAudioPath();
        try
        {
            await WriteAudioAsync(audio, temporaryAudioPath, settings.FfmpegPath, cancellationToken).ConfigureAwait(false);
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
            // 只透出文件名：完整路径可能包含用户名等隐私信息。
            throw new InvalidOperationException(
                $"无法启动本地语音识别运行时：{Path.GetFileName(runtimePath)}（{ex.GetType().Name}）", ex);
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

    /// <summary>
    /// 把输入音频归一化成 16 kHz 单声道 16 bit PCM WAV 后写到 <paramref name="path"/>。
    /// 之前这里是把输入流原样拷成 .wav 文件，任何非 WAV 输入都会被当成 WAV 交给运行时。
    /// </summary>
    private static async Task WriteAudioAsync(
        Stream audio,
        string path,
        string ffmpegPath,
        CancellationToken cancellationToken)
    {
        if (audio.CanSeek)
        {
            audio.Position = 0;
        }

        var normalizer = new Audio.AudioNormalizer(
            string.IsNullOrWhiteSpace(ffmpegPath) ? null : ffmpegPath);
        var normalized = await normalizer.NormalizeToWavePcm16Mono16KAsync(
            audio,
            fileName: null,
            contentType: null,
            cancellationToken).ConfigureAwait(false);

        await File.WriteAllBytesAsync(path, normalized.WaveBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 在应用的便携数据目录下创建临时音频文件，并顺手清理过期残留。
    /// </summary>
    internal static string CreateTemporaryAudioPath()
    {
        var directory = AppPaths.TempAudioDirectory;
        Directory.CreateDirectory(directory);

        CleanupStaleFiles(directory);

        return Path.Combine(directory, $"clapback-{Guid.NewGuid():N}.wav");
    }

    private static void CleanupStaleFiles(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow - TempAudioRetention;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException)
                {
                    // 清理是尽力而为，不能影响本次识别。
                }
                catch (UnauthorizedAccessException)
                {
                    // 清理是尽力而为，不能影响本次识别。
                }
            }
        }
        catch (IOException)
        {
            // 目录不可枚举时跳过清理。
        }
        catch (UnauthorizedAccessException)
        {
            // 目录不可枚举时跳过清理。
        }
    }

    /// <summary>
    /// 把配置路径解析到应用目录内，拒绝绝对路径与 <c>..</c> 逃逸。
    /// 这是防止 settings.json 被篡改后指向任意 exe / 覆盖任意文件的关键闸门。
    /// </summary>
    internal static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("未配置本地语音识别路径");
        }

        if (Path.IsPathRooted(path))
        {
            throw new InvalidOperationException(
                $"本地路径必须是应用目录内的相对路径，不接受绝对路径：{Path.GetFileName(path)}");
        }

        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path, root);

        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("本地路径不允许跳出应用目录。");
        }

        return fullPath;
    }

    internal static string ResolveModelPath(ClapBackSettings settings)
    {
        var configuredPath = settings.LocalModelPath;
        if (string.IsNullOrWhiteSpace(configuredPath)
            || (settings.LocalModelVariant != LocalModelVariant.SenseVoiceSmall
                && string.Equals(configuredPath.Replace('\\', '/'),
                    "models/sensevoice-small-q8.gguf", StringComparison.OrdinalIgnoreCase)))
        {
            configuredPath = LocalModelCatalog.Get(settings.LocalModelVariant).DefaultLocalPath;
        }

        return ResolvePath(configuredPath);
    }

    internal static string? ResolveAvailableVadPath(ClapBackSettings settings)
    {
        if (!settings.InstallVadModel || string.IsNullOrWhiteSpace(settings.LocalVadModelPath))
        {
            return null;
        }

        try
        {
            var path = ResolvePath(settings.LocalVadModelPath);
            return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // 可选路径配置或文件不可用时保持与环境报告一致，不阻塞短语音。
            return null;
        }
    }

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

    /// <summary>
    /// 交给上层/日志前先去掉用户目录：识别运行时的 stderr 里可能回显完整模型与音频路径。
    /// </summary>
    private static string Sanitize(string value)
    {
        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        compact = compact.Replace(AppContext.BaseDirectory, "<app>", StringComparison.OrdinalIgnoreCase)
                         .Replace(Path.GetTempPath(), "<temp>", StringComparison.OrdinalIgnoreCase);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            compact = compact.Replace(profile, "<home>", StringComparison.OrdinalIgnoreCase);
        }

        return compact.Length <= 500 ? compact : compact[..500] + "…";
    }
}

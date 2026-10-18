using System.Formats.Tar;
using System.IO.Compression;
using ClapBack.Core.Models;

namespace ClapBack.Stt;

/// <summary>一次环境检查/安装的结果状态。</summary>
public enum ProvisionStatus
{
    /// <summary>本来就就绪，未做任何修改。</summary>
    Ok,
    /// <summary>本次自动安装成功。</summary>
    Installed,
    /// <summary>不就绪，且无法自动补齐。</summary>
    Failed,
    /// <summary>当前识别模式或配置不需要该项，未执行安装。</summary>
    Skipped,
    /// <summary>可选组件不可用，不影响短语音识别。</summary>
    OptionalUnavailable
}

/// <summary>环境检查的单个条目（运行时 / 识别模型 / VAD 模型）。</summary>
public sealed record ProvisionStep(string Name, ProvisionStatus Status, string Detail);

/// <summary>
/// 启动时的环境检测与自动安装报告。<see cref="IsReady"/> 为真表示当前模式没有缺失的必需本地组件。
/// </summary>
public sealed record ProvisionReport(IReadOnlyList<ProvisionStep> Steps)
{
    public bool IsReady => Steps.All(step => step.Status is
        ProvisionStatus.Ok or ProvisionStatus.Installed or ProvisionStatus.Skipped or ProvisionStatus.OptionalUnavailable);
}

/// <summary>
/// 启动时检测「运行时可执行文件 / 识别模型 / VAD 模型」是否就绪，缺失时自动下载安装。
///
/// 完整性边界（与 <see cref="HuggingFaceModelDownloader"/> 同一套）：
/// 每个安装产物都必须过 SHA-256，期望值来自<b>随源码评审发布</b>的内置锁定值
/// （<c>ClapBackSettings.RuntimeAssetSha256</c> / <c>VadModelSha256</c> /
/// <c>LocalModelCatalog.ExpectedSha256</c>）。没有锁定值就拒绝安装，而不是「下载下来先用着」。
/// 运行时 exe 会被直接执行，所以它的校验不能退化。
///
/// 压缩包只按文件名挑出那一个可执行文件写到固定目标路径，从不整包解压——因此不存在 zip-slip 路径穿越。
/// </summary>
public sealed class RuntimeProvisioner
{
    private const string RuntimeTempDirectoryName = "runtime-temp";
    private const int DownloadBufferSize = 81920;

    /// <summary>默认下载端点。留空的 <c>RuntimeDownloadEndpoint</c> 用它。</summary>
    public const string DefaultRuntimeDownloadEndpoint = "https://github.com/";

    // GitHub Release 下载会「连得上但中途卡死」：TCP 流没断，HttpClient 就一直等它
    // 那个 15 分钟超时。所以每次尝试必须有自己的超时，否则重试循环根本轮不起来——
    // 只会一次挂很久，然后整条链路超时。
    private const int DownloadRetryCount = 6;
    private const int DownloadRetryDelayMilliseconds = 1500;
    private static readonly TimeSpan DownloadAttemptTimeout = TimeSpan.FromSeconds(90);

    private readonly HttpClient _httpClient;
    private readonly HuggingFaceModelDownloader _modelDownloader;

    public RuntimeProvisioner(HttpClient? httpClient = null, HuggingFaceModelDownloader? modelDownloader = null)
    {
        _httpClient = httpClient ?? CreateDefaultHttpClient();
        _modelDownloader = modelDownloader ?? new HuggingFaceModelDownloader(_httpClient);
    }

    /// <summary>检查并在需要时补齐运行环境。每完成一步都会通过 <paramref name="progress"/> 报告。</summary>
    public Task<ProvisionReport> EnsureAsync(
        ClapBackSettings settings,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        EnsureForPlatformAsync(settings, OperatingSystem.IsWindows(), progress, cancellationToken);

    internal async Task<ProvisionReport> EnsureForPlatformAsync(
        ClapBackSettings settings,
        bool isWindows,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        if (settings.TranscriptionMode == TranscriptionMode.CloudOnly)
        {
            return new ProvisionReport(
            [
                new ProvisionStep("本地识别环境", ProvisionStatus.Skipped,
                    "CloudOnly 模式不需要本地运行时、识别模型或 VAD；云端服务配置在识别时检查。")
            ]);
        }

        if (!isWindows)
        {
            // Python FunASR 自行管理模型缓存，不使用 Windows GGUF 或 VAD 资产。
            return new ProvisionReport([CheckMacOsEnvironment(settings)]);
        }

        var steps = new List<ProvisionStep>
        {
            await EnsureRuntimeAsync(settings, progress, cancellationToken).ConfigureAwait(false),
            await EnsureModelAsync(settings, progress, cancellationToken).ConfigureAwait(false),
            await EnsureVadAsync(settings, progress, cancellationToken).ConfigureAwait(false)
        };

        return new ProvisionReport(steps);
    }

    // ---------------------------------------------------------------- 运行时

    private async Task<ProvisionStep> EnsureRuntimeAsync(
        ClapBackSettings settings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        const string name = "本地识别运行时";
        string runtimePath;
        try
        {
            runtimePath = LocalSenseVoiceTranscriptionProvider.ResolvePath(settings.LocalSttRuntimePath);
        }
        catch (Exception ex)
        {
            return new ProvisionStep(name, ProvisionStatus.Failed, $"运行时路径配置无效：{ex.Message}");
        }

        string? verifyError = null;
        if (File.Exists(runtimePath))
        {
            try
            {
                await _modelDownloader.VerifyExistingFileAsync(
                    runtimePath,
                    settings.LocalRuntimeSha256,
                    settings.RequireModelIntegrity,
                    allowFirstTrust: true,
                    cancellationToken).ConfigureAwait(false);
                return new ProvisionStep(name, ProvisionStatus.Ok, $"已就绪：{Path.GetFileName(runtimePath)}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                verifyError = ex.Message;
                // 过不了校验就清掉残留，接着走下面的自动安装。
                // 「把它修好」正是这一步存在的意义，不该丢给用户去手动删文件。
                TryDelete(runtimePath);
                TryDelete(ModelIntegrity.FingerprintPathFor(runtimePath));
            }
        }

        if (!settings.AutoProvisionOnStartup)
        {
            return verifyError is null
                ? new ProvisionStep(name, ProvisionStatus.Failed,
                    $"缺失：{Path.GetFileName(runtimePath)}，且已关闭自动安装（AutoProvisionOnStartup=false）。")
                : new ProvisionStep(name, ProvisionStatus.Failed, $"已有文件未通过完整性校验：{verifyError}");
        }

        try
        {
            progress?.Report($"正在下载本地识别运行时 {settings.RuntimeAssetName}…");
            await InstallRuntimeAsync(settings, runtimePath, progress, cancellationToken).ConfigureAwait(false);
            return new ProvisionStep(name, ProvisionStatus.Installed,
                $"已自动安装：{settings.RuntimeReleaseTag} / {settings.RuntimeAssetName}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProvisionStep(name, ProvisionStatus.Failed, $"自动安装失败：{ex.Message}");
        }
    }

    private async Task InstallRuntimeAsync(
        ClapBackSettings settings, string runtimePath, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var expected = ModelIntegrity.NormalizeHex(settings.RuntimeAssetSha256);
        if (expected.Length != 64)
        {
            // 运行时是会被直接执行的东西，没有锁定哈希就不装。
            throw new InvalidDataException(
                "运行时资产没有内置锁定 SHA-256，拒绝安装。请把 ClapBackSettings.RuntimeAssetSha256 填成所选资产的已核对摘要。");
        }

        var ownerAndRepo = settings.RuntimeReleaseRepository.Split('/', StringSplitOptions.TrimEntries);
        if (ownerAndRepo.Length != 2 || ownerAndRepo[0].Length == 0 || ownerAndRepo[1].Length == 0)
        {
            throw new ArgumentException($"RuntimeReleaseRepository 必须是 owner/name 形式，实际是：{settings.RuntimeReleaseRepository}");
        }

        if (string.IsNullOrWhiteSpace(settings.RuntimeReleaseTag) ||
            string.IsNullOrWhiteSpace(settings.RuntimeAssetName))
        {
            throw new ArgumentException("RuntimeReleaseTag 与 RuntimeAssetName 都不能为空。");
        }

        // GitHub 的浏览器下载地址是确定的，不必先调 API（也就没有速率限制问题）。
        var endpoint = NormalizeEndpoint(
            string.IsNullOrWhiteSpace(settings.RuntimeDownloadEndpoint)
                ? DefaultRuntimeDownloadEndpoint
                : settings.RuntimeDownloadEndpoint);
        var downloadUrl =
            $"{endpoint}{Uri.EscapeDataString(ownerAndRepo[0])}/{Uri.EscapeDataString(ownerAndRepo[1])}" +
            $"/releases/download/{Uri.EscapeDataString(settings.RuntimeReleaseTag)}/" +
            Uri.EscapeDataString(settings.RuntimeAssetName);

        var temporaryDirectory = CreateTemporaryDirectory(runtimePath);
        var archivePath = Path.Combine(temporaryDirectory, $"clapback-{Guid.NewGuid():N}.download");
        try
        {
            await DownloadToFileAsync(downloadUrl, archivePath, cancellationToken).ConfigureAwait(false);

            var actual = await ModelIntegrity.ComputeSha256HexAsync(archivePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"运行时资产 SHA-256 校验失败（{settings.RuntimeAssetName}）：应为 {expected}，实际为 {actual}。安装已放弃。");
            }

            progress?.Report("正在解压本地识别运行时…");
            var executableName = Path.GetFileName(settings.LocalSttRuntimePath);
            await ExtractSingleEntryAsync(archivePath, executableName, runtimePath, cancellationToken)
                .ConfigureAwait(false);

            // 指纹必须描述**解出来的 exe 自己**，不是压缩包。
            // 压缩包的摘要（RuntimeAssetSha256）已经在上面验证过下载内容了，
            // 但它是 zip 的哈希，和 exe 的哈希是两回事——写成同一个值会让下一次启动
            // 算 exe 自己的哈希对不上，把刚装好的运行时删掉。
            await _modelDownloader.VerifyExistingFileAsync(
                runtimePath,
                settings.LocalRuntimeSha256,
                requireIntegrity: settings.RequireModelIntegrity,
                allowFirstTrust: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(archivePath);
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    // ---------------------------------------------------------------- 识别模型

    private async Task<ProvisionStep> EnsureModelAsync(
        ClapBackSettings settings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        const string name = "识别模型";
        var artifact = LocalModelCatalog.Get(settings.LocalModelVariant);
        string? expected;
        string modelPath;
        try
        {
            expected = artifact.ResolveExpectedSha256(settings.LocalModelSha256);
            modelPath = LocalSenseVoiceTranscriptionProvider.ResolveModelPath(settings);
        }
        catch (Exception ex)
        {
            return new ProvisionStep(name, ProvisionStatus.Failed, ex.Message);
        }

        try
        {
            if (settings.LocalModelAutoDownload)
            {
                var existedBefore = File.Exists(modelPath);
                var stampBefore = existedBefore ? File.GetLastWriteTimeUtc(modelPath) : DateTime.MinValue;
                progress?.Report($"正在准备识别模型 {artifact.DisplayName}…");
                var result = await _modelDownloader.EnsureFileAsync(
                    modelPath,
                    artifact.HuggingFaceRepository,
                    artifact.HuggingFaceRevision,
                    artifact.HuggingFaceFile,
                    artifact.ModelScopeRepository,
                    artifact.ModelScopeRevision,
                    artifact.ModelScopeFile,
                    settings.HuggingFaceTimeoutSeconds,
                    settings.ModelDownloadSource,
                    expected,
                    settings.RequireModelIntegrity,
                    settings.HuggingFaceEndpoint,
                    settings.ModelScopeEndpoint,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                // 文件已存在且校验通过时下载器直接复用，这里不能报「下载完成」：那会让用户以为
                // 每次启动都在重下几百 MB，也会掩盖「离线可跑」这个真实状态。
                var downloaded = !existedBefore || File.GetLastWriteTimeUtc(modelPath) != stampBefore;
                return new ProvisionStep(name,
                    downloaded ? ProvisionStatus.Installed : ProvisionStatus.Ok,
                    $"{artifact.DisplayName}：{(downloaded ? $"{(result.UsedModelScope ? "ModelScope" : "Hugging Face")} 下载完成" : "已就绪")}");
            }

            await _modelDownloader.VerifyExistingFileAsync(
                modelPath, expected, settings.RequireModelIntegrity,
                allowFirstTrust: false, cancellationToken).ConfigureAwait(false);
            return new ProvisionStep(name, ProvisionStatus.Ok, $"{artifact.DisplayName}：已就绪");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProvisionStep(name, ProvisionStatus.Failed, $"{artifact.DisplayName}：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- VAD 模型

    private async Task<ProvisionStep> EnsureVadAsync(
        ClapBackSettings settings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        const string name = "VAD 模型";
        if (!settings.InstallVadModel || string.IsNullOrWhiteSpace(settings.LocalVadModelPath))
        {
            return new ProvisionStep(name, ProvisionStatus.Skipped, "可选：已跳过，短语音将不使用 VAD。");
        }

        try
        {
            var vadPath = LocalSenseVoiceTranscriptionProvider.ResolvePath(settings.LocalVadModelPath);
            var expected = ModelIntegrity.NormalizeHex(settings.VadModelSha256);
            if (settings.LocalModelAutoDownload)
            {
                var existedBefore = File.Exists(vadPath);
                var stampBefore = existedBefore ? File.GetLastWriteTimeUtc(vadPath) : DateTime.MinValue;
                progress?.Report("正在准备 VAD 模型…");
                await _modelDownloader.EnsureFileAsync(
                    vadPath,
                    settings.VadHuggingFaceRepository,
                    settings.VadHuggingFaceRevision,
                    settings.VadHuggingFaceFile,
                    settings.VadHuggingFaceRepository,
                    settings.VadHuggingFaceRevision,
                    settings.VadHuggingFaceFile,
                    settings.HuggingFaceTimeoutSeconds,
                    settings.ModelDownloadSource,
                    expected,
                    settings.RequireModelIntegrity,
                    settings.HuggingFaceEndpoint,
                    settings.ModelScopeEndpoint,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var downloaded = !existedBefore || File.GetLastWriteTimeUtc(vadPath) != stampBefore;
                return new ProvisionStep(name,
                    downloaded ? ProvisionStatus.Installed : ProvisionStatus.Ok, "已就绪");
            }

            await _modelDownloader.VerifyExistingFileAsync(
                vadPath, expected, settings.RequireModelIntegrity,
                allowFirstTrust: false, cancellationToken).ConfigureAwait(false);
            return new ProvisionStep(name, ProvisionStatus.Ok, "已就绪");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProvisionStep(name, ProvisionStatus.OptionalUnavailable,
                $"可选组件未就绪：{ex.Message}；短语音仍可不带 VAD 识别。");
        }
    }

    // ---------------------------------------------------------------- macOS

    private static ProvisionStep CheckMacOsEnvironment(ClapBackSettings settings)
    {
        const string name = "macOS FunASR 环境";
        var python = settings.MacOsPythonPath;
        if (string.IsNullOrWhiteSpace(python))
        {
            return new ProvisionStep(name, ProvisionStatus.Failed, "未配置 MacOsPythonPath。");
        }

        var resolved = MacOsFunAsrTranscriptionProvider.ResolvePath(python);
        return File.Exists(resolved)
            ? new ProvisionStep(name, ProvisionStatus.Ok, $"Python 解释器已就绪：{python}")
            : new ProvisionStep(name, ProvisionStatus.Failed,
                $"缺少 {python}。macOS 的 FunASR 环境需要先运行 scripts/macos-funasr-setup.sh，它会安装 Python 依赖，不适合由本程序静默代装。");
    }

    // ---------------------------------------------------------------- 压缩包

    /// <summary>
    /// 从压缩包里挑出<b>唯一</b>那一个目标文件名，写到固定目标路径。
    /// 不做整包解压，也就不会被包内的 ../ 路径穿越攻击写到别处。
    /// </summary>
    internal static async Task ExtractSingleEntryAsync(
        string archivePath, string entryFileName, string destinationPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entryFileName))
        {
            throw new ArgumentException("目标可执行文件名不能为空。");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var stagingPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".extracting";
        try
        {
            // 先把条目写到暂存文件，写完并释放句柄之后再原子替换目标。
            // 以前是直接在同一个方法里 File.Move，那时输出流还没 Dispose，Windows 上会报
            // 「文件被另一进程占用」——安装在最后一步失败。
            await ExtractToStagingFileAsync(archivePath, entryFileName, stagingPath, cancellationToken)
                .ConfigureAwait(false);
            File.Move(stagingPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(stagingPath);
        }
    }

    private static async Task ExtractToStagingFileAsync(
        string archivePath, string entryFileName, string stagingPath, CancellationToken cancellationToken)
    {
        await using (var source = new FileStream(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            DownloadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(
            stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            DownloadBufferSize, FileOptions.Asynchronous))
        {
            var found = await CopyArchiveEntryAsync(source, entryFileName, output, cancellationToken)
                .ConfigureAwait(false);
            if (!found)
            {
                throw new InvalidDataException($"压缩包里没有找到 {entryFileName}。");
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> CopyArchiveEntryAsync(
        Stream archive, string entryFileName, Stream output, CancellationToken cancellationToken)
    {
        // .tar.gz 在前，因为 gzip 流不是 zip。
        if (archive.CanSeek && archive.Length >= 2 && archive.ReadByte() == 0x1f && archive.ReadByte() == 0x8b)
        {
            archive.Position = 0;
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new TarReader(gzip, leaveOpen: true);
            while (await reader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } entry)
            {
                // DataStream 只在常规文件上非空，符号链接/目录/设备节点都会被这条挡掉。
                if (entry.DataStream is null ||
                    !string.Equals(Path.GetFileName(entry.Name), entryFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await entry.DataStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        archive.Position = 0;
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            if (!string.Equals(Path.GetFileName(entry.FullName), entryFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await using var stream = entry.Open();
            await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- 杂项

    private async Task DownloadToFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        // GitHub Release 的下载对部分网络是间歇性可达的（TCP/TLS 被随机重置，同样的地址
        // 多试几次能通）。模型下载那边早就有重试，运行时这条链路以前没有，结果就是
        // 「模型下得下来、运行时下不下来」。这里补上同样的重试退避。
        Exception? last = null;
        // 上次运行可能留下半截文件。DownloadOnceAsync 用 FileMode.CreateNew，
        // 有残留会直接 IOException，所以先清掉，别把重试次数浪费在自己的残留上。
        TryDelete(destinationPath);
        for (var attempt = 1; attempt <= DownloadRetryCount; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, destinationPath, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or TimeoutException)
            {
                last = ex;
                TryDelete(destinationPath);
                if (attempt < DownloadRetryCount)
                {
                    await Task.Delay(DownloadRetryDelayMilliseconds * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new HttpRequestException(
            $"下载本地识别运行时失败，已重试 {DownloadRetryCount} 次仍不成功：{last?.Message}", last);
    }

    private async Task DownloadOnceAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        // 每次尝试带自己的超时。它超时不代表用户取消，所以外层 catch 会把它当可重试失败处理。
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(DownloadAttemptTimeout);

        try
        {
            using var response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
            await using var file = new FileStream(
                destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                DownloadBufferSize, FileOptions.Asynchronous);
            await body.CopyToAsync(file, attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"下载本地识别运行时超时（单次尝试上限 {DownloadAttemptTimeout.TotalSeconds:F0} 秒）。");
        }
    }

    internal static string NormalizeEndpoint(string baseUrl)
    {
        // 同 HuggingFaceModelDownloader.BuildResolveUri：必须用 Scheme 收窄，
        // 因为 Uri.IsLoopback 对 file:// 恒为 true。
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps &&
             !(baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback)))
        {
            throw new ArgumentException("运行时下载端点必须是 https://（本机回环地址除外）。", nameof(baseUrl));
        }

        return baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
    }

    /// <summary>
    /// 暂存目录建在**落地产物的同一目录下**，不放 %LOCALAPPDATA%。
    /// 两条理由，都是踩出来的：
    /// 1. 暂存和目标必须同一个卷，最后的 File.Move 才是原子改名，而不是跨卷拷贝到一半失败；
    /// 2. 权限边界必须和目标一致。放在 AppData 会出现「下载成功却装不进去：
    ///    Access to the path '...\runtime-temp' is denied」——明明 exe 目录可写，
    ///    却因为一个无关的目录被锁而装不上。
    /// </summary>
    internal static string CreateTemporaryDirectory(string destinationPath)
    {
        var parent = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("运行时路径缺少所在目录。");
        Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, RuntimeTempDirectoryName);
        Directory.CreateDirectory(root);
        return root;
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
            // 尽力清理，失败不阻塞安装结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 尽力清理。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClapBack/1.0 (+https://github.com/)");
        // 运行时包比模型小得多，但给足余量。
        client.Timeout = TimeSpan.FromMinutes(15);
        return client;
    }
}

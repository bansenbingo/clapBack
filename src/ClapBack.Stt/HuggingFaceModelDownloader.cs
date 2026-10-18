using System.Net.Http.Headers;
using ClapBack.Core.Models;

namespace ClapBack.Stt;

/// <summary>
/// Downloads a model from Hugging Face first and falls back to ModelScope when
/// the foreign source times out or is otherwise unavailable. Model binaries
/// remain runtime artifacts and are never embedded in the GitHub source tree.
///
/// 完整性链路（见 <see cref="ModelIntegrity"/>）：
/// 下载前核对已存在文件的指纹，下载后对落盘字节算 SHA-256 并与「内置锁定值 / 源端声明值」比对，
/// 通过才原子替换目标文件并写入 <c>&lt;model&gt;.sha256</c> 指纹，供下次使用时复检 TOCTOU。
/// </summary>
public sealed class HuggingFaceModelDownloader
{
    private const int ModelScopeRetryCount = 3;
    private const int ModelScopeRetryDelayMilliseconds = 250;

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Expected, string Actual)> _verifiedCache = new();

    public HuggingFaceModelDownloader(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateDefaultHttpClient();
    }

    /// <summary>兼容重载：不做完整性校验，仅供测试或调用方自行校验的场景使用。</summary>
    public Task<ModelDownloadResult> EnsureFileAsync(
        string destinationPath,
        string repository,
        string revision,
        string file,
        CancellationToken cancellationToken = default) =>
        EnsureFileAsync(
            destinationPath,
            repository,
            revision,
            file,
            repository,
            revision,
            file,
            20,
            ModelDownloadSource.HuggingFaceFirst,
            expectedSha256: null,
            requireIntegrity: false,
            cancellationToken: cancellationToken);

    /// <summary>兼容重载：不做完整性校验。</summary>
    public Task<ModelDownloadResult> EnsureFileAsync(
        string destinationPath,
        string huggingFaceRepository,
        string huggingFaceRevision,
        string huggingFaceFile,
        string modelScopeRepository,
        string modelScopeRevision,
        string modelScopeFile,
        int huggingFaceTimeoutSeconds = 20,
        CancellationToken cancellationToken = default) =>
        EnsureFileAsync(
            destinationPath,
            huggingFaceRepository,
            huggingFaceRevision,
            huggingFaceFile,
            modelScopeRepository,
            modelScopeRevision,
            modelScopeFile,
            huggingFaceTimeoutSeconds,
            ModelDownloadSource.HuggingFaceFirst,
            expectedSha256: null,
            requireIntegrity: false,
            cancellationToken: cancellationToken);

    /// <param name="expectedSha256">期望 SHA-256（大写十六进制）。来自内置目录锁定值或用户配置。</param>
    /// <param name="requireIntegrity">
    /// 为 true 时，任何无法用「内置锁定值 / 源端声明值 / 已落盘指纹」自证的文件一律拒绝；
    /// 为 false 时退回旧的「存在且非空即有效」语义。
    /// </param>
    public async Task<ModelDownloadResult> EnsureFileAsync(
        string destinationPath,
        string huggingFaceRepository,
        string huggingFaceRevision,
        string huggingFaceFile,
        string modelScopeRepository,
        string modelScopeRevision,
        string modelScopeFile,
        int huggingFaceTimeoutSeconds,
        ModelDownloadSource source,
        string? expectedSha256,
        bool requireIntegrity,
        string? huggingFaceEndpoint = null,
        string? modelScopeEndpoint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(huggingFaceRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(huggingFaceFile);

        var fingerprintPath = ModelIntegrity.FingerprintPathFor(destinationPath);
        var expected = ModelIntegrity.NormalizeHex(expectedSha256);

        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var existing = await TryTrustExistingAsync(
                destinationPath,
                fingerprintPath,
                expected,
                requireIntegrity,
                allowFirstTrust: false,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                var attempt = await DownloadToAsync(
                    temporaryPath,
                    huggingFaceRepository,
                    huggingFaceRevision,
                    huggingFaceFile,
                    modelScopeRepository,
                    modelScopeRevision,
                    modelScopeFile,
                    huggingFaceTimeoutSeconds,
                    source,
                    huggingFaceEndpoint,
                    modelScopeEndpoint,
                    cancellationToken).ConfigureAwait(false);

                var actual = await ModelIntegrity.ComputeSha256HexAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
                AssertIntegrity(actual, attempt.DeclaredSha256, expected, requireIntegrity, attempt.UsedModelScope);

                await ModelIntegrity.WriteFingerprintAsync(fingerprintPath, actual, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, destinationPath, overwrite: true);

                return new ModelDownloadResult(
                    attempt.UsedModelScope,
                    destinationPath,
                    actual,
                    attempt.DeclaredSha256 ?? (expected.Length == 0 ? null : expected),
                    UsedFingerprint: false);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <summary>
    /// 只校验、不下载。供「关闭自动下载」或校验本地运行时 exe 使用。
    /// <paramref name="allowFirstTrust"/> 为 true 时，既没有期望哈希也没有指纹的文件
    /// 会被「首次信任」——计算并落盘指纹，之后每次使用都复检，从而挡住后续替换；
    /// 为 false 时则直接拒绝（适用于模型这类可被下载链路投毒的文件）。
    /// </summary>
    public async Task<ModelDownloadResult> VerifyExistingFileAsync(
        string destinationPath,
        string? expectedSha256,
        bool requireIntegrity,
        bool allowFirstTrust,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var fingerprintPath = ModelIntegrity.FingerprintPathFor(destinationPath);
        var expected = ModelIntegrity.NormalizeHex(expectedSha256);

        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(destinationPath))
            {
                throw new FileNotFoundException($"文件不存在：{Path.GetFileName(destinationPath)}", destinationPath);
            }

            var result = await TryTrustExistingAsync(
                destinationPath,
                fingerprintPath,
                expected,
                requireIntegrity,
                allowFirstTrust,
                cancellationToken).ConfigureAwait(false);

            return result ?? throw new InvalidDataException(
                $"文件 {Path.GetFileName(destinationPath)} 完整性校验失败，已拒绝使用。");
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <summary>
    /// 判断已存在的模型文件是否可信。可信则返回结果；能证明「被改坏」时删除并返回 null（重新下载）；
    /// 只是「无法自证」时抛出异常且保留原文件，避免删掉用户手工下载的模型。
    /// </summary>
    private async Task<ModelDownloadResult?> TryTrustExistingAsync(
        string destinationPath,
        string fingerprintPath,
        string expected,
        bool requireIntegrity,
        bool allowFirstTrust,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(destinationPath))
        {
            return null;
        }

        var fileInfo = new FileInfo(destinationPath);
        if (fileInfo.Length == 0)
        {
            DeleteBroken(destinationPath, "模型文件大小为 0");
            return null;
        }

        var hasFingerprint = File.Exists(fingerprintPath);
        if (expected.Length == 0 && !hasFingerprint && !requireIntegrity && !allowFirstTrust)
        {
            // 显式关闭完整性校验：保持旧语义（存在且非空即有效）。
            return new ModelDownloadResult(false, destinationPath);
        }

        // 缓存键含长度与 mtime：文件被替换后必然命中失败，会重新计算 SHA-256。
        // 这不是安全边界，只是避免每次识别都重算几百 MB 的哈希。
        var cacheKey = destinationPath + "|" + fileInfo.Length + "|" + fileInfo.LastWriteTimeUtc.Ticks;
        if (_verifiedCache.TryGetValue(cacheKey, out var cached) &&
            (expected.Length == 0 || string.Equals(cached.Expected, expected, StringComparison.Ordinal)))
        {
            return new ModelDownloadResult(false, destinationPath, cached.Actual, expected.Length == 0 ? null : expected, UsedFingerprint: false);
        }

        var actual = await ModelIntegrity.ComputeSha256HexAsync(destinationPath, cancellationToken).ConfigureAwait(false);

        if (expected.Length > 0)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                DeleteBroken(destinationPath, $"SHA-256 与期望值不符（期望 {expected}，实际 {actual}）");
                return null;
            }

            await ModelIntegrity.WriteFingerprintAsync(fingerprintPath, actual, cancellationToken).ConfigureAwait(false);
            _verifiedCache[cacheKey] = (expected, actual);
            return new ModelDownloadResult(false, destinationPath, actual, expected, UsedFingerprint: false);
        }

        if (hasFingerprint)
        {
            var fingerprint = await ModelIntegrity.ReadFingerprintAsync(fingerprintPath, cancellationToken).ConfigureAwait(false);
            if (fingerprint is null)
            {
                throw new InvalidDataException(
                    $"模型指纹文件 {Path.GetFileName(fingerprintPath)} 为空，无法验证文件未被替换。" +
                    "请删除该文件以触发重新下载，或在设置 LocalModelSha256 中填入已核对的 sha256。");
            }

            if (!string.Equals(fingerprint, actual, StringComparison.Ordinal))
            {
                DeleteBroken(
                    destinationPath,
                    $"SHA-256 与指纹文件 {Path.GetFileName(fingerprintPath)} 不符（指纹 {fingerprint}，实际 {actual}）");
                return null;
            }

            _verifiedCache[cacheKey] = (fingerprint, actual);
            return new ModelDownloadResult(false, destinationPath, actual, null, UsedFingerprint: true);
        }

        if (allowFirstTrust)
        {
            // 首次信任：没有可比对的依据时，把当前内容的摘要固化为指纹，
            // 之后任何替换都会被发现。文档中如实说明这一信任根是「首次运行时」建立的。
            await ModelIntegrity.WriteFingerprintAsync(fingerprintPath, actual, cancellationToken).ConfigureAwait(false);
            _verifiedCache[cacheKey] = (actual, actual);
            return new ModelDownloadResult(false, destinationPath, actual, null, UsedFingerprint: false);
        }

        if (requireIntegrity)
        {
            throw new InvalidDataException(
                $"文件 {Path.GetFileName(destinationPath)} 无法自证完整性：" +
                "既没有内置锁定哈希，也没有下载时落盘的指纹文件。" +
                $"请在设置 LocalModelSha256 中填入已核对的 sha256，或关闭 RequireModelIntegrity 以接受未校验模型。");
        }

        _verifiedCache[cacheKey] = (string.Empty, actual);
        return new ModelDownloadResult(false, destinationPath, actual, null, UsedFingerprint: false);
    }

    /// <summary>
    /// 校验落盘字节的摘要。<b>内置锁定哈希是信任根，源端声明值只是兜底</b>——
    /// 两者冲突时以锁定值为准，不能让一个下载响应头否决随源码评审发布的哈希。
    /// </summary>
    private static void AssertIntegrity(
        string actual,
        string? declared,
        string expected,
        bool requireIntegrity,
        bool usedModelScope)
    {
        if (expected.Length > 0)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"模型 SHA-256 校验失败（下载源 {(usedModelScope ? "ModelScope" : "Hugging Face")}）：应为 {expected}，实际为 {actual}。下载已丢弃。");
            }

            // 锁定值已命中：这就是我们要求的那份内容。源端声明值怎么写不影响结论
            // （它可能根本不是文件摘要，例如 Xet 后端的 ETag），因此不再用它做第二次否决。
            return;
        }

        if (!string.IsNullOrWhiteSpace(declared))
        {
            if (!string.Equals(declared, actual, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"模型 SHA-256 与下载源 {(usedModelScope ? "ModelScope" : "Hugging Face")} 声明的值不一致：" +
                    $"声明 {declared}，实际为 {actual}。下载已丢弃。");
            }

            return;
        }

        if (requireIntegrity)
        {
            throw new InvalidDataException(
                "无法验证模型完整性：下载源未声明 sha256，且没有内置锁定哈希。" +
                "请在设置 LocalModelSha256 中填入已核对的 sha256，或关闭 RequireModelIntegrity 以接受未校验模型。");
        }
    }

    private async Task<(bool UsedModelScope, string? DeclaredSha256)> DownloadToAsync(
        string temporaryPath,
        string huggingFaceRepository,
        string huggingFaceRevision,
        string huggingFaceFile,
        string modelScopeRepository,
        string modelScopeRevision,
        string modelScopeFile,
        int huggingFaceTimeoutSeconds,
        ModelDownloadSource source,
        string? huggingFaceEndpoint,
        string? modelScopeEndpoint,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(huggingFaceTimeoutSeconds, 1, 300));
        cts.CancelAfter(timeout);

        var attempts = BuildAttempts(
            source,
            huggingFaceRepository,
            huggingFaceRevision,
            huggingFaceFile,
            modelScopeRepository,
            modelScopeRevision,
            modelScopeFile,
            huggingFaceEndpoint,
            modelScopeEndpoint);

        Exception? huggingFaceError = null;
        Exception? modelScopeError = null;

        foreach (var attempt in attempts)
        {
            try
            {
                var declared = await DownloadOnceAsync(
                    temporaryPath,
                    attempt.Uri,
                    attempt.IsModelScope,
                    cts.Token).ConfigureAwait(false);
                return (attempt.IsModelScope, declared);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or OperationCanceledException)
            {
                if (attempt.IsModelScope)
                {
                    modelScopeError = ex;
                }
                else
                {
                    huggingFaceError = ex;
                }
            }
        }

        if (huggingFaceError is null && modelScopeError is null)
        {
            throw new InvalidOperationException("模型下载失败：没有配置任何可用的下载来源。");
        }

        throw new InvalidOperationException(
            $"模型下载失败：Hugging Face（{Describe(huggingFaceError)}）；ModelScope（{Describe(modelScopeError)}）");
    }

    private async Task<string?> DownloadOnceAsync(
        string temporaryPath,
        Uri uri,
        bool isModelScope,
        CancellationToken cancellationToken)
    {
        var attempts = isModelScope ? ModelScopeRetryCount : 1;
        for (var i = 0; i < attempts; i++)
        {
            if (i > 0)
            {
                await Task.Delay(ModelScopeRetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                // Hugging Face 对 LFS 文件在 X-Linked-Etag 里返回 "sha256:<hex>"；
                // 普通文件的 ETag 是 git blob SHA-1，ModelIntegrity 会正确地忽略掉。
                var declared = ModelIntegrity.TryParseDeclaredSha256(
                    TryGetHeader(response, "X-Linked-Etag"),
                    TryGetHeader(response, "ETag"));

                await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var temporaryDirectory = Path.GetDirectoryName(temporaryPath);
                if (!string.IsNullOrEmpty(temporaryDirectory))
                {
                    Directory.CreateDirectory(temporaryDirectory);
                }

                await using (var fileStream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 64,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await httpStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                    await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                return declared;
            }
            catch (Exception ex) when (i + 1 < attempts && ex is HttpRequestException or OperationCanceledException or IOException)
            {
                // ModelScope 偶发网络抖动，退避后重试下一次。
            }
        }

        throw new HttpRequestException($"模型下载源 {uri.Host} 连续尝试 {attempts} 次均未成功。");
    }

    private static IReadOnlyList<(Uri Uri, bool IsModelScope)> BuildAttempts(
        ModelDownloadSource source,
        string huggingFaceRepository,
        string huggingFaceRevision,
        string huggingFaceFile,
        string modelScopeRepository,
        string modelScopeRevision,
        string modelScopeFile,
        string? huggingFaceEndpoint,
        string? modelScopeEndpoint)
    {
        var attempts = new List<(Uri, bool)>();

        if (source is ModelDownloadSource.HuggingFaceFirst or ModelDownloadSource.HuggingFaceOnly)
        {
            attempts.Add((BuildResolveUri(
                string.IsNullOrWhiteSpace(huggingFaceEndpoint) ? DefaultHuggingFaceEndpoint : huggingFaceEndpoint,
                huggingFaceRepository,
                huggingFaceRevision,
                huggingFaceFile), false));
        }

        if (source is ModelDownloadSource.HuggingFaceFirst or ModelDownloadSource.ModelScopeOnly)
        {
            attempts.Add((BuildResolveUri(
                string.IsNullOrWhiteSpace(modelScopeEndpoint) ? DefaultModelScopeEndpoint : modelScopeEndpoint,
                modelScopeRepository,
                modelScopeRevision,
                modelScopeFile), true));
        }

        return attempts;
    }

    private static string Describe(Exception? error) =>
        error is null ? "未尝试" : $"{error.GetType().Name}: {error.Message}";

    private static string? TryGetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    private static void DeleteBroken(string path, string reason)
    {
        // 连指纹一起删。只删文件会留下一个指向「已经不存在的内容」的旧指纹，
        // 下次装回来的新文件永远对不上它——那是一个无法自愈的死状态。
        TryDelete(ModelIntegrity.FingerprintPathFor(path));

        if (TryDelete(path))
        {
            return;
        }

        throw new IOException($"模型文件校验失败（{reason}），但无法删除该文件，请手动删除：{Path.GetFileName(path)}");
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return !File.Exists(path);
        }
        catch (IOException)
        {
            // Nothing to do here because the temporary file should disappear when the process ends.
        }
        catch (UnauthorizedAccessException)
        {
            // Nothing to do here because the temporary file should disappear when the process ends.
        }

        return false;
    }

    /// <summary>
    /// Hugging Face 官方端点。默认<b>不用</b>它：不少网络会把 <c>huggingface.co</c> 的 DNS 劫持到无关 IP
    /// （实测解析到 Facebook 地址段 31.13.x.x 与 face:b00c::），直连必然超时。
    /// 确认自己网络能直连官方时，可在设置 <c>HuggingFaceEndpoint</c> 里显式改回这个值。
    /// </summary>
    public const string OfficialHuggingFaceEndpoint = "https://huggingface.co/";

    /// <summary>
    /// 默认使用的 Hugging Face 兼容端点——社区镜像 hf-mirror.com，仓库内容与官方逐字节一致。
    /// 换镜像<b>不会</b>削弱安全性：下载内容仍要过 <see cref="ModelIntegrity"/> 的 SHA-256 校验，
    /// 期望值来自随源码评审发布的锁定哈希，与由哪个端点提供下载无关。
    /// </summary>
    public const string DefaultHuggingFaceEndpoint = "https://hf-mirror.com/";

    /// <summary>ModelScope 官方端点，同样可通过 <c>ModelScopeEndpoint</c> 覆盖。</summary>
    public const string DefaultModelScopeEndpoint = "https://www.modelscope.cn/models/";

    internal static Uri BuildResolveUri(
        string baseUrl,
        string repository,
        string revision,
        string file)
    {
        // 用 Scheme 而不是 !IsLoopback 来收窄到 https：Uri.IsLoopback 对 file:// 恒为 true，
        // 写成「非 https 必须是回环」会把 file:/// 这种根本不是下载地址的方案放进来。
        // 真正想说的是「必须是 https，除非是本机 http 调试端点」。
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps &&
             !(baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback)))
        {
            throw new ArgumentException("模型下载端点必须是 https://（本机回环地址除外）。", nameof(baseUrl));
        }

        var normalizedBase = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";

        if (string.IsNullOrWhiteSpace(repository) ||
            !repository.Contains('/') ||
            repository.Split('/').Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("模型仓库必须使用 owner/name 格式。", nameof(repository));
        }

        if (string.IsNullOrWhiteSpace(revision))
        {
            throw new ArgumentException("模型版本不能为空。", nameof(revision));
        }

        if (string.IsNullOrWhiteSpace(file) || file.Split('/').Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("模型文件名不能为空。", nameof(file));
        }

        var escapedRepository = string.Join(
            "/",
            repository.Split('/').Select(Uri.EscapeDataString));
        var escapedRevision = Uri.EscapeDataString(revision);
        var escapedFile = string.Join("/", file.Split('/').Select(Uri.EscapeDataString));

        return new Uri(
            normalizedBase + escapedRepository + "/resolve/" + escapedRevision + "/" + escapedFile + "?download=true",
            UriKind.Absolute);
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClapBack/1.0 (+https://github.com/)");
        client.Timeout = TimeSpan.FromSeconds(10 * 60);
        return client;
    }
}

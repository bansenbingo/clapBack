using System.Net.Http.Headers;

namespace ClapBack.Stt;

/// <summary>
/// Downloads a model from Hugging Face first and falls back to ModelScope when
/// the foreign source times out or is otherwise unavailable. Model binaries
/// remain runtime artifacts and are never embedded in the GitHub source tree.
/// </summary>
public sealed class HuggingFaceModelDownloader
{
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public HuggingFaceModelDownloader(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateDefaultHttpClient();
    }

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
            cancellationToken);

    public async Task<ModelDownloadResult> EnsureFileAsync(
        string destinationPath,
        string huggingFaceRepository,
        string huggingFaceRevision,
        string huggingFaceFile,
        string modelScopeRepository,
        string modelScopeRevision,
        string modelScopeFile,
        int huggingFaceTimeoutSeconds = 20,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
        {
            return new ModelDownloadResult(false, destinationPath);
        }

        var directory = Path.GetDirectoryName(destinationPath)
                        ?? throw new InvalidOperationException("无法确定模型缓存目录");
        Directory.CreateDirectory(directory);
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.download";
        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
            {
                return new ModelDownloadResult(false, destinationPath);
            }

            Exception? huggingFaceError = null;
            var usedModelScope = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(huggingFaceTimeoutSeconds, 1, 300)));
                await DownloadAsync(
                    BuildResolveUri("https://huggingface.co", huggingFaceRepository, huggingFaceRevision, huggingFaceFile),
                    temporaryPath,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                huggingFaceError = new TimeoutException("Hugging Face 模型下载超时");
                TryDelete(temporaryPath);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
            {
                huggingFaceError = ex;
                TryDelete(temporaryPath);
            }

            if (huggingFaceError is not null)
            {
                usedModelScope = true;
                try
                {
                    await DownloadAsync(
                        BuildResolveUri("https://www.modelscope.cn/models", modelScopeRepository, modelScopeRevision, modelScopeFile),
                        temporaryPath,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception modelScopeError) when (modelScopeError is HttpRequestException or InvalidDataException)
                {
                    throw new InvalidOperationException(
                        $"模型下载失败：Hugging Face（{huggingFaceError.Message}）；ModelScope（{modelScopeError.Message}）",
                        modelScopeError);
                }
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return new ModelDownloadResult(usedModelScope, destinationPath);
        }
        finally
        {
            TryDelete(temporaryPath);
            _downloadGate.Release();
        }
    }

    private async Task DownloadAsync(Uri uri, string temporaryPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 64,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        if (new FileInfo(temporaryPath).Length == 0)
        {
            throw new InvalidDataException("模型源返回了空文件");
        }
    }

    private static Uri BuildResolveUri(string prefix, string repository, string revision, string file)
    {
        var repositoryParts = repository.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (repositoryParts.Length != 2
            || repositoryParts.Any(string.IsNullOrWhiteSpace)
            || string.IsNullOrWhiteSpace(revision)
            || string.IsNullOrWhiteSpace(file))
        {
            throw new ArgumentException("模型仓库必须是 owner/name，且必须配置 revision 和 file");
        }

        var encodedRepository = string.Join('/', repositoryParts.Select(Uri.EscapeDataString));
        var encodedRevision = Uri.EscapeDataString(revision.Trim('/'));
        var encodedFile = string.Join('/', file.Trim('/').Split('/').Select(Uri.EscapeDataString));
        return new Uri($"{prefix.TrimEnd('/')}/{encodedRepository}/resolve/{encodedRevision}/{encodedFile}?download=true");
    }

    private static HttpClient CreateDefaultHttpClient() => new() { Timeout = TimeSpan.FromMinutes(30) };

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

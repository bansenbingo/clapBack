using System.Security.Cryptography;

namespace ClapBack.Stt;

/// <summary>
/// 模型与本地运行时的完整性校验。
///
/// 信任根有两层：
/// 1. <c>LocalModelCatalog.ExpectedSha256</c> —— 随源码评审发布的硬编码期望值，最可靠；
/// 2. 下载时源端声明的摘要（Hugging Face 对 LFS 文件在 <c>X-Linked-Etag</c> 中返回
///    <c>"sha256:&lt;hex&gt;"</c>）+ 首次下载后落盘的指纹文件 <c>&lt;model&gt;.sha256</c>。
///
/// 第 2 层能挡住传输被截断/篡改、以及校验通过之后文件被悄悄替换（TOCTOU）。
/// 它挡不住「模型源本身被投毒」——那需要第 1 层，所以锁定哈希后应填入目录。
/// </summary>
public static class ModelIntegrity
{
    public const string FingerprintExtension = ".sha256";

    public static string FingerprintPathFor(string artifactPath) =>
        artifactPath.EndsWith(FingerprintExtension, StringComparison.OrdinalIgnoreCase)
            ? artifactPath
            : artifactPath + FingerprintExtension;

    /// <summary>把任意大小写/带空白的十六进制摘要规范化为大写无空白形式。</summary>
    public static string NormalizeHex(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    public static async Task<string> ComputeSha256HexAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public static async Task<string> ComputeSha256HexAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 64,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeSha256HexAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string?> ReadFingerprintAsync(string fingerprintPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(fingerprintPath))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(fingerprintPath, cancellationToken).ConfigureAwait(false);
        var normalized = NormalizeHex(text);
        return normalized.Length == 0 ? null : normalized;
    }

    public static async Task WriteFingerprintAsync(
        string fingerprintPath,
        string sha256Hex,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256Hex);
        var directory = Path.GetDirectoryName(fingerprintPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fingerprintPath, NormalizeHex(sha256Hex), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从 HTTP 响应头解析源端声明的 SHA-256。两种真实格式都要认：
    /// <list type="bullet">
    /// <item>Hugging Face 对 LFS 文件：<c>X-Linked-Etag: "sha256:&lt;64 hex&gt;"</c>（带引号、带前缀）；
    /// 普通文件的 <c>ETag</c> 是 git blob SHA-1（40 位），不能当 SHA-256 用，会被长度校验挡掉。</item>
    /// <item>ModelScope：<c>X-Linked-Etag: &lt;64 hex&gt;</c>（<b>裸 64 位十六进制，无前缀无引号</b>）。
    /// 只认 <c>sha256:</c> 前缀会让 ModelScope 的摘要被忽略，进而让
    /// <c>RequireModelIntegrity</c> 误判为「源端无法自证」而拒绝下载。</item>
    /// </list>
    /// 其它任何长度或字符集不合法的值一律返回 null，宁可放弃校验也不误信。
    /// </summary>
    public static string? TryParseDeclaredSha256(string? linkedEtag, string? etag)
    {
        // X-Linked-Etag 是下载源专门用来声明文件摘要的头：
        //   Hugging Face LFS：`"sha256:<hex>"`
        //   ModelScope：`<hex>`（裸 64 位，无前缀无引号，已与 hf-mirror 交叉核对确实是文件 SHA-256）
        // 这两种都认。
        return ParseSha256(linkedEtag, allowBareHex: true) ?? ParseSha256(etag, allowBareHex: false);
    }

    /// <param name="allowBareHex">
    /// 是否接受不带 <c>sha256:</c> 前缀的裸摘要。
    /// <b>普通 <c>ETag</b> 必须传 false</b>：Hugging Face 把 LFS 文件放在 Xet 存储后端，
    /// 跟随 302 之后拿到的最终响应里 <c>X-Linked-Etag</c> 已经消失，只剩一个 64 位十六进制的
    /// Xet 内容寻址 <c>ETag</c>——它不是文件的 SHA-256，当成摘要用会让每一次合法下载都被误判为篡改。
    /// </param>
    private static string? ParseSha256(string? candidate, bool allowBareHex)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var value = candidate.Trim().Trim('"').Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            value = value[2..].Trim().Trim('"').Trim();
        }

        var marker = value.IndexOf("sha256:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0 && !allowBareHex)
        {
            return null;
        }

        // 有 sha256: 前缀就取前缀之后；否则整段当作裸摘要（ModelScope 风格）。
        var hex = NormalizeHex(marker >= 0 ? value[(marker + "sha256:".Length)..] : value);
        return hex.Length == 64 && IsHex(hex) ? hex : null;
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

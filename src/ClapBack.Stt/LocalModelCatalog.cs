using ClapBack.Core.Models;

namespace ClapBack.Stt;

public sealed record LocalModelArtifact(
    LocalModelVariant Variant,
    string DisplayName,
    string DefaultLocalPath,
    string Format,
    string HuggingFaceRepository,
    string HuggingFaceRevision,
    string HuggingFaceFile,
    string ModelScopeRepository,
    string ModelScopeRevision,
    string ModelScopeFile,
    bool SupportsCurrentGgufRuntime,
    /// <summary>
    /// 期望 SHA-256（大写十六进制，64 字符）。这是模型身份的信任根，随源码评审发布，
    /// 发布前需人工核对 Hugging Face 页面上该文件的 sha256。
    /// 留空表示尚未锁定，此时由 <c>ClapBackSettings.RequireModelIntegrity</c> 决定
    /// 是拒绝运行，还是退化为「下载时校验源端声明摘要 + 首次信任指纹」。
    /// </summary>
    string? ExpectedSha256 = null)
{
    /// <summary>
    /// 生效的期望摘要：目录硬编码值优先。<c>settings.LocalModelSha256</c> 只能在此基础上
    /// 进一步收窄，不能用来放宽或替换信任根——两者不一致时直接报错，避免改配置绕过评审过的哈希。
    /// </summary>
    public string? ResolveExpectedSha256(string? configuredOverride)
    {
        var pinned = ModelIntegrity.NormalizeHex(ExpectedSha256);
        var configured = ModelIntegrity.NormalizeHex(configuredOverride);

        if (pinned.Length == 0)
        {
            return configured.Length == 0 ? null : configured;
        }

        if (configured.Length > 0 && !string.Equals(pinned, configured, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"配置的模型 SHA-256（{configured}）与内置锁定值（{pinned}）不一致。" +
                "内置锁定值来自源码评审，不能被配置覆盖；请删除 LocalModelSha256 配置，或改用与内置锁定值一致的模型文件。");
        }

        return pinned;
    }
}

public sealed record ModelDownloadResult(
    bool UsedModelScope,
    string FilePath,
    string Sha256Hex = "",
    string? DeclaredSha256 = null,
    bool UsedFingerprint = false);

public static class LocalModelCatalog
{
    // 信任根（ExpectedSha256）——随源码评审发布，值来自模型源公布的摘要，核对记录见下：
    //
    //   2026-10-15  sensevoice-small-q8.gguf
    //       4AE45C94422DE949B387E2E0FB10D7E14E4C42C69DB30C3444ECC7D4B844B7C5
    //       254208320 字节；由两个互相独立的源交叉核对一致：
    //         ModelScope  X-Linked-Etag（裸 64 位十六进制）
    //         hf-mirror   X-Linked-Etag "sha256:<hex>"
    //
    //   2026-10-15  paraformer-q8.gguf
    //       42BF76EA1575A336AACA4C1B7C01A82B79113E6D04D0D6B799561BFCF07EE011
    //       仅由 ModelScope 一个源核对（当日 huggingface.co 在该网络被 DNS 劫持，无法交叉验证）。
    //
    // 未锁定值时同样由 ClapBackSettings.RequireModelIntegrity 决定拒绝运行还是退化。
    // （Fun-ASR-Nano-2512 变体已按需求移除：它的两个下载源指向不同产物——Hugging Face 是 GGUF、
    //  ModelScope 是 model.pt——无法用一个锁定哈希同时代表两个文件。）
    public static LocalModelArtifact Get(LocalModelVariant variant) => variant switch
    {
        LocalModelVariant.SenseVoiceSmall => new(
            variant,
            "SenseVoiceSmall Q8",
            "models\\sensevoice-small-q8.gguf",
            "GGUF",
            "FunAudioLLM/SenseVoiceSmall-GGUF",
            "main",
            "sensevoice-small-q8.gguf",
            "FunAudioLLM/SenseVoiceSmall-GGUF",
            "master",
            "sensevoice-small-q8.gguf",
            true,
            ExpectedSha256: "4AE45C94422DE949B387E2E0FB10D7E14E4C42C69DB30C3444ECC7D4B844B7C5"),
        LocalModelVariant.ParaformerZh => new(
            variant,
            "Paraformer 中文 Q8",
            "models\\paraformer-q8.gguf",
            "GGUF",
            "FunAudioLLM/Paraformer-GGUF",
            "main",
            "paraformer-q8.gguf",
            "FunAudioLLM/Paraformer-GGUF",
            "master",
            "paraformer-q8.gguf",
            true,
            ExpectedSha256: "42BF76EA1575A336AACA4C1B7C01A82B79113E6D04D0D6B799561BFCF07EE011"),
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null)
    };
}

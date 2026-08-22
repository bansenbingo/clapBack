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
    bool SupportsCurrentGgufRuntime);

public sealed record ModelDownloadResult(bool UsedModelScope, string FilePath);

public static class LocalModelCatalog
{
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
            true),
        LocalModelVariant.FunAsrNano2512 => new(
            variant,
            "Fun-ASR-Nano-2512 Q8",
            "models\\fun-asr-nano-2512-q8_0.gguf",
            "GGUF / ModelScope PyTorch fallback",
            "FunAudioLLM/Fun-ASR-Nano-2512-GGUF",
            "main",
            "fun-asr-nano-2512-q8_0.gguf",
            "FunAudioLLM/Fun-ASR-Nano-2512",
            "master",
            "model.pt",
            false),
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
            true),
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null)
    };
}

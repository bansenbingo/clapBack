using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;
using ClapBack.Stt;
using Xunit;

namespace ClapBack.Tests;

/// <summary>
/// 安全与隐私加固的回归测试。
/// 这些用例覆盖两份审核报告（docs/audit/security-review.md、docs/audit/privacy-review.md）
/// 里高/中危问题的修复点，改坏任何一条都会在这里显形。
/// </summary>
public class SecurityHardeningTests
{
    private static readonly byte[] SomeAudio = { 1, 2, 3, 4 };

    // ---------------------------------------------------------------- 模型完整性

    [Fact]
    public void ModelIntegrity_ParsesHuggingFaceLfsLinkedEtag()
    {
        // Hugging Face 对 LFS 文件在 X-Linked-Etag 里返回 "sha256:<hex>"。
        var hex = new string('a', 64);

        var parsed = ModelIntegrity.TryParseDeclaredSha256($"\"sha256:{hex}\"", "\"3b26c2c0\"");

        Assert.Equal(new string('A', 64), parsed);
    }

    [Fact]
    public void ModelIntegrity_IgnoresGitBlobEtag()
    {
        // 普通文件的 ETag 是 git blob SHA-1（40 位），不是 sha256，必须被忽略。
        Assert.Null(ModelIntegrity.TryParseDeclaredSha256(null, "\"3b26c2c0d5d5a512345678901234567890123456\""));
    }

    [Theory]
    [InlineData("\"sha256:zzzz\"")]
    [InlineData("\"sha256:abc\"")]
    [InlineData("")]
    [InlineData(null)]
    public void ModelIntegrity_RejectsMalformedDeclaredDigest(string? linkedEtag)
    {
        Assert.Null(ModelIntegrity.TryParseDeclaredSha256(linkedEtag, null));
    }

    [Fact]
    public async Task ModelIntegrity_Fingerprint_DetectsTamperingAfterFirstTrust()
    {
        var directory = Path.Combine(Path.GetTempPath(), "clapback-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var artifactPath = Path.Combine(directory, "model.gguf");
            var fingerprintPath = ModelIntegrity.FingerprintPathFor(artifactPath);

            await File.WriteAllBytesAsync(artifactPath, new byte[] { 1, 2, 3 });
            var original = await ModelIntegrity.ComputeSha256HexAsync(artifactPath);
            await ModelIntegrity.WriteFingerprintAsync(fingerprintPath, original);

            // 指纹必须挡住「首次校验之后被替换」——这正是 TOCTOU 攻击的形状。
            await File.WriteAllBytesAsync(artifactPath, new byte[] { 9, 9, 9 });
            var tampered = await ModelIntegrity.ComputeSha256HexAsync(artifactPath);

            Assert.NotEqual(original, tampered);
            Assert.Equal(original, await ModelIntegrity.ReadFingerprintAsync(fingerprintPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ModelIntegrity_FingerprintPath_IsIdempotent()
    {
        var path = Path.Combine("models", "sensevoice-small-q8.gguf");
        var expected = path + ModelIntegrity.FingerprintExtension;

        Assert.Equal(expected, ModelIntegrity.FingerprintPathFor(path));
        Assert.Equal(expected, ModelIntegrity.FingerprintPathFor(expected));
    }

    [Fact]
    public void LocalModelCatalog_ConfiguredOverride_CannotContradictPinnedHash()
    {
        var pinned = new string('A', 64);
        var artifact = new LocalModelArtifact(
            LocalModelVariant.SenseVoiceSmall,
            "SenseVoiceSmall",
            "models/x.gguf",
            "GGUF",
            "owner/repo",
            "main",
            "x.gguf",
            "owner/repo",
            "master",
            "x.pt",
            true,
            pinned);

        // 配置只能收窄、不能替换信任根。
        Assert.Equal(pinned, artifact.ResolveExpectedSha256(pinned.ToLowerInvariant()));
        Assert.Throws<InvalidDataException>(() => artifact.ResolveExpectedSha256(new string('B', 64)));
    }

    [Fact]
    public void LocalModelArtifact_WithoutPin_UsesConfiguredHash()
    {
        // 没有内置锁定值的条目（合成一个）才允许由配置决定期望哈希。
        var artifact = new LocalModelArtifact(
            LocalModelVariant.SenseVoiceSmall,
            "Synthetic",
            "models\\x.gguf",
            "GGUF",
            "owner/repo",
            "main",
            "x.gguf",
            "owner/repo",
            "master",
            "x.gguf",
            true);
        var configured = new string('c', 64);

        Assert.Null(artifact.ExpectedSha256);
        // 归一化后统一为大写十六进制。
        Assert.Equal(new string('C', 64), artifact.ResolveExpectedSha256(configured));
        Assert.Null(artifact.ResolveExpectedSha256(null));
        Assert.Null(artifact.ResolveExpectedSha256("   "));
    }

    [Fact]
    public void LocalModelCatalog_EveryVariantIsPinned()
    {
        // 回归护栏：目录里每一个模型都必须带内置锁定哈希。
        // Fun-ASR-Nano-2512 就是因为两个源指向不同产物、无法统一锁定而被移除的。
        foreach (var variant in Enum.GetValues<LocalModelVariant>())
        {
            var artifact = LocalModelCatalog.Get(variant);
            Assert.False(
                string.IsNullOrWhiteSpace(artifact.ExpectedSha256),
                $"{variant} 缺少内置锁定 SHA-256，发布前必须核对并填写。");
            Assert.Equal(64, ModelIntegrity.NormalizeHex(artifact.ExpectedSha256).Length);
        }
    }

    // ---------------------------------------------------------------- 本地路径逃逸

    [Theory]
    [InlineData("../evil.exe")]
    [InlineData(@"..\..\evil.exe")]
    [InlineData(@"models\..\..\evil.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\server\share\evil.exe")]
    public void LocalModel_PathOutsideApplicationDirectory_IsRejected(string path)
    {
        Assert.Throws<InvalidOperationException>(() => LocalSenseVoiceTranscriptionProvider.ResolvePath(path));
    }

    [Fact]
    public void LocalModel_RelativePathInsideApplicationDirectory_IsAccepted()
    {
        var resolved = LocalSenseVoiceTranscriptionProvider.ResolvePath(@"models\sensevoice-small-q8.gguf");

        Assert.StartsWith(
            Path.GetFullPath(AppContext.BaseDirectory),
            resolved,
            StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- 云端端点强制 https

    [Theory]
    [InlineData("https://api.example.com/v1", "https://api.example.com/v1/audio/transcriptions")]
    [InlineData("https://api.example.com/v1/", "https://api.example.com/v1/audio/transcriptions")]
    public void CloudEndpoint_HttpsIsAccepted(string baseUrl, string expected)
    {
        Assert.Equal(expected, WhisperCompatibleTranscriptionProvider.BuildEndpoint(baseUrl).ToString());
    }

    [Theory]
    [InlineData("http://api.example.com/v1")]
    [InlineData("ftp://api.example.com/v1")]
    public void CloudEndpoint_PlaintextIsRejected(string baseUrl)
    {
        // 音频与 Bearer 密钥走明文会被中间人完整看到，必须显式拒绝。
        Assert.Throws<InvalidOperationException>(
            () => WhisperCompatibleTranscriptionProvider.BuildEndpoint(baseUrl));
    }

    [Fact]
    public void CloudEndpoint_LoopbackHttpIsAllowed()
    {
        Assert.Equal(
            "http://127.0.0.1:8080/audio/transcriptions",
            WhisperCompatibleTranscriptionProvider.BuildEndpoint("http://127.0.0.1:8080").ToString());
    }

    [Fact]
    public void CloudEndpoint_InsecureHttpRequiresExplicitOptIn()
    {
        var endpoint = WhisperCompatibleTranscriptionProvider.BuildEndpoint(
            "http://api.example.com/v1", allowInsecureHttp: true);

        Assert.Equal("http", endpoint.Scheme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/audio/transcriptions")]
    public void CloudEndpoint_InvalidUrlIsRejected(string baseUrl)
    {
        Assert.Throws<InvalidOperationException>(
            () => WhisperCompatibleTranscriptionProvider.BuildEndpoint(baseUrl));
    }

    // ---------------------------------------------------------------- 识别模式 fail-closed

    [Fact]
    public async Task UnknownTranscriptionMode_FailsClosedToLocalOnly()
    {
        var local = new FakeProvider(() => new TranscriptResult("本地结果"));
        var cloud = new FakeProvider(() => new TranscriptResult("云端结果"));
        var uploaded = false;
        var provider = new CompositeTranscriptionProvider(
            local,
            cloud,
            () => new ClapBackSettings { TranscriptionMode = (TranscriptionMode)999 },
            _ => uploaded = true);

        var result = await provider.TranscribeAsync(
            new MemoryStream(SomeAudio), new TranscriptionOptions("model", "zh"));

        // 配置损坏绝不能变成「自动上传」。
        Assert.Equal("本地结果", result.Text);
        Assert.Equal(1, local.Calls);
        Assert.Equal(0, cloud.Calls);
        Assert.False(uploaded);
    }

    [Fact]
    public async Task LocalOnly_NeverUploadsEvenWhenLocalFails()
    {
        var cloud = new FakeProvider(() => new TranscriptResult("云端结果"));
        var uploaded = false;
        var provider = new CompositeTranscriptionProvider(
            new FakeProvider(() => throw new InvalidOperationException("本地引擎崩了")),
            cloud,
            () => new ClapBackSettings { TranscriptionMode = TranscriptionMode.LocalOnly },
            _ => uploaded = true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranscribeAsync(
            new MemoryStream(SomeAudio), new TranscriptionOptions("model", "zh")));

        Assert.Equal(0, cloud.Calls);
        Assert.False(uploaded);
    }

    [Fact]
    public async Task LocalFirst_EmptyResult_DoesNotUploadByDefault()
    {
        var cloud = new FakeProvider(() => new TranscriptResult("云端结果"));
        var uploaded = false;
        var provider = new CompositeTranscriptionProvider(
            new FakeProvider(() => new TranscriptResult("   ")),
            cloud,
            () => new ClapBackSettings { TranscriptionMode = TranscriptionMode.LocalFirst },
            _ => uploaded = true);

        var result = await provider.TranscribeAsync(
            new MemoryStream(SomeAudio), new TranscriptionOptions("model", "zh"));

        // 空文本是「没听清」，不是「本地引擎坏了」。默认不因此把整段音频外发。
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(0, cloud.Calls);
        Assert.False(uploaded);
    }

    [Fact]
    public async Task LocalFirst_EmptyResult_UploadsOnlyWithExplicitOptIn()
    {
        var cloud = new FakeProvider(() => new TranscriptResult("云端结果"));
        var uploaded = false;
        var provider = new CompositeTranscriptionProvider(
            new FakeProvider(() => new TranscriptResult("   ")),
            cloud,
            () => new ClapBackSettings
            {
                TranscriptionMode = TranscriptionMode.LocalFirst,
                FallbackOnEmptyResult = true
            },
            _ => uploaded = true);

        var result = await provider.TranscribeAsync(
            new MemoryStream(SomeAudio), new TranscriptionOptions("model", "zh"));

        Assert.Equal("云端结果", result.Text);
        Assert.Equal(1, cloud.Calls);
        Assert.True(uploaded);
    }

    [Fact]
    public async Task LocalFirst_LocalFailure_FallsBackToCloudAndNotifies()
    {
        var cloud = new FakeProvider(() => new TranscriptResult("云端结果"));
        var notifications = new List<string>();
        var provider = new CompositeTranscriptionProvider(
            new FakeProvider(() => throw new InvalidOperationException("本地引擎崩了")),
            cloud,
            () => new ClapBackSettings { TranscriptionMode = TranscriptionMode.LocalFirst },
            message => notifications.Add(message));

        var result = await provider.TranscribeAsync(
            new MemoryStream(SomeAudio), new TranscriptionOptions("model", "zh"));

        // 回退必须「告知」用户音频已上传，不能静默。
        Assert.Equal("云端结果", result.Text);
        Assert.Single(notifications);
        Assert.Contains("回退云端", notifications[0]);
        Assert.Contains("音频将上传", notifications[0]);
    }

    [Fact]
    public async Task CloudOnly_NotifiesThatAudioIsUploaded()
    {
        var notifications = new List<string>();
        var provider = new CompositeTranscriptionProvider(
            new FakeProvider(() => new TranscriptResult("本地结果")),
            new FakeProvider(() => new TranscriptResult("云端结果")),
            () => new ClapBackSettings { TranscriptionMode = TranscriptionMode.CloudOnly },
            message => notifications.Add(message));

        var result = await provider.TranscribeAsync(
            new MemoryStream(SomeAudio), new TranscriptionOptions("model", "zh"));

        Assert.Equal("云端结果", result.Text);
        Assert.Single(notifications);
    }

    [Fact]
    public void ModelIntegrity_ParsesModelScopeBareHexEtag()
    {
        // ModelScope 返回的是裸 64 位十六进制，没有 sha256: 前缀也没有引号。
        // 只认前缀会让 ModelScope 回退路径在 RequireModelIntegrity 下被误判为「无法自证」。
        var hex = new string('4', 64);

        Assert.Equal(new string('4', 64), ModelIntegrity.TryParseDeclaredSha256(hex, null));
        Assert.Equal(new string('4', 64), ModelIntegrity.TryParseDeclaredSha256($"\"{hex}\"", null));
        Assert.Equal(new string('4', 64), ModelIntegrity.TryParseDeclaredSha256($"W/\"{hex}\"", null));
    }

    [Fact]
    public void LocalModelCatalog_SenseVoiceSmall_IsPinnedToCrossVerifiedHash()
    {
        // 2026-10-15 由 ModelScope 与 hf-mirror 两个独立源交叉核对一致的摘要。
        var artifact = LocalModelCatalog.Get(LocalModelVariant.SenseVoiceSmall);

        Assert.Equal(
            "4AE45C94422DE949B387E2E0FB10D7E14E4C42C69DB30C3444ECC7D4B844B7C5",
            artifact.ExpectedSha256);
        Assert.Equal(artifact.ExpectedSha256, artifact.ResolveExpectedSha256(null));
    }

    // ---------------------------------------------------------------- 模型下载端点

    [Fact]
    public void DownloadEndpoint_RejectsPlaintextBaseUrl()
    {
        Assert.Throws<ArgumentException>(() => HuggingFaceModelDownloader.BuildResolveUri(
            "http://evil.example/models/", "owner/name", "main", "model.gguf"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/models/")]
    public void DownloadEndpoint_RejectsInvalidBaseUrl(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() => HuggingFaceModelDownloader.BuildResolveUri(
            baseUrl, "owner/name", "main", "model.gguf"));
    }

    [Fact]
    public void DownloadEndpoint_AllowsConfiguredMirror()
    {
        // 被 DNS 劫持的网络需要能指向镜像；内容仍由 SHA-256 校验兜底。
        var uri = HuggingFaceModelDownloader.BuildResolveUri(
            "https://hf-mirror.com/", "FunAudioLLM/SenseVoiceSmall-GGUF", "main", "sensevoice-small-q8.gguf");

        Assert.Equal(
            "https://hf-mirror.com/FunAudioLLM/SenseVoiceSmall-GGUF/resolve/main/sensevoice-small-q8.gguf?download=true",
            uri.ToString());
    }

    [Fact]
    public void DownloadEndpoint_NormalizesMissingTrailingSlash()
    {
        var uri = HuggingFaceModelDownloader.BuildResolveUri("https://huggingface.co", "a/b", "main", "f.gguf");

        Assert.Equal("https://huggingface.co/a/b/resolve/main/f.gguf?download=true", uri.ToString());
    }

    // ---------------------------------------------------------------- 辅助类型

    private sealed class FakeProvider : ITranscriptionProvider
    {
        private readonly Func<TranscriptResult> _transcribe;

        public FakeProvider(Func<TranscriptResult> transcribe) => _transcribe = transcribe;

        public int Calls { get; private set; }

        public Task<TranscriptResult> TranscribeAsync(
            Stream audio,
            TranscriptionOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_transcribe());
        }
    }
}

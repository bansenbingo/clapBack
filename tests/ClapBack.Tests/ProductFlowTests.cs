using System.Net;
using ClapBack.Core;
using ClapBack.Core.Models;
using ClapBack.Stt;
using ClapBack.Stt.Audio;
using Xunit;

namespace ClapBack.Tests;

public sealed class ProductFlowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CloudOnly_SkipsAllLocalDependenciesAndDownloads(bool isWindows)
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var provisioner = new RuntimeProvisioner(client);
        var settings = new ClapBackSettings
        {
            TranscriptionMode = TranscriptionMode.CloudOnly,
            LocalSttRuntimePath = string.Empty,
            LocalModelPath = string.Empty,
            LocalModelVariant = (LocalModelVariant)999,
            LocalVadModelPath = string.Empty,
            MacOsPythonPath = string.Empty
        };

        var report = await provisioner.EnsureForPlatformAsync(settings, isWindows);

        Assert.True(report.IsReady);
        var step = Assert.Single(report.Steps);
        Assert.Equal(ProvisionStatus.Skipped, step.Status);
        Assert.Contains("CloudOnly", step.Detail);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MacOs_OnlyChecksPythonEnvironmentWithoutDownloadingGgufOrVad(bool pythonExists)
    {
        using var files = new AppFiles();
        var python = files.PathFor("python");
        if (pythonExists)
        {
            await File.WriteAllTextAsync(python, "python placeholder");
        }

        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var provisioner = new RuntimeProvisioner(client);
        var settings = new ClapBackSettings
        {
            MacOsPythonPath = python,
            LocalSttRuntimePath = string.Empty,
            LocalModelVariant = (LocalModelVariant)999,
            LocalModelPath = string.Empty,
            LocalVadModelPath = string.Empty
        };

        var report = await provisioner.EnsureForPlatformAsync(settings, isWindows: false);

        Assert.Equal(pythonExists, report.IsReady);
        var step = Assert.Single(report.Steps);
        Assert.Equal("macOS FunASR 环境", step.Name);
        Assert.Equal(pythonExists ? ProvisionStatus.Ok : ProvisionStatus.Failed, step.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task MissingVad_IsReportedAsOptionalAndDoesNotBlockRequiredReadySteps()
    {
        using var files = new AppFiles();
        var settings = files.OfflineSettings();
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var provisioner = new RuntimeProvisioner(client);

        var report = await provisioner.EnsureForPlatformAsync(settings, isWindows: true);

        var vad = report.Steps.Single(step => step.Name == "VAD 模型");
        Assert.Equal(ProvisionStatus.OptionalUnavailable, vad.Status);
        Assert.Contains("可选", vad.Detail);
        Assert.Contains("短语音", vad.Detail);
        Assert.Null(LocalSenseVoiceTranscriptionProvider.ResolveAvailableVadPath(settings));
        Assert.True(new ProvisionReport(
        [
            new ProvisionStep("运行时", ProvisionStatus.Ok, "已就绪"),
            new ProvisionStep("识别模型", ProvisionStatus.Ok, "已就绪"),
            vad
        ]).IsReady);
        // 可选 VAD 不掩盖必需运行时/识别模型缺失。
        Assert.False(report.IsReady);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FailedVadDownload_IsOptionalAndNotReportedAsInstalled()
    {
        using var files = new AppFiles();
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var provisioner = new RuntimeProvisioner(client);
        var settings = files.OfflineSettings() with
        {
            LocalModelAutoDownload = true,
            ModelDownloadSource = ModelDownloadSource.HuggingFaceOnly
        };

        var report = await provisioner.EnsureForPlatformAsync(settings, isWindows: true);

        var vad = report.Steps.Single(step => step.Name == "VAD 模型");
        Assert.Equal(ProvisionStatus.OptionalUnavailable, vad.Status);
        Assert.False(File.Exists(LocalSenseVoiceTranscriptionProvider.ResolvePath(settings.LocalVadModelPath)));
        Assert.True(handler.RequestCount > 0);
    }

    [Theory]
    [InlineData(false, "vad.gguf")]
    [InlineData(true, "")]
    public async Task DisabledOrUnconfiguredVad_IsSkipped(bool installVad, string vadName)
    {
        using var files = new AppFiles();
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var settings = files.OfflineSettings() with
        {
            InstallVadModel = installVad,
            LocalVadModelPath = vadName.Length == 0 ? string.Empty : files.RelativePathFor(vadName)
        };
        var provisioner = new RuntimeProvisioner(client);

        var report = await provisioner.EnsureForPlatformAsync(settings, isWindows: true);

        Assert.Equal(ProvisionStatus.Skipped, report.Steps.Single(step => step.Name == "VAD 模型").Status);
        Assert.Null(LocalSenseVoiceTranscriptionProvider.ResolveAvailableVadPath(settings));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Provider_UsesAvailableVadButIgnoresMissingEmptyOrDisabledVad()
    {
        using var files = new AppFiles();
        var settings = files.OfflineSettings();
        var path = LocalSenseVoiceTranscriptionProvider.ResolvePath(settings.LocalVadModelPath);

        Assert.Null(LocalSenseVoiceTranscriptionProvider.ResolveAvailableVadPath(settings));
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());
        Assert.Null(LocalSenseVoiceTranscriptionProvider.ResolveAvailableVadPath(settings));
        await File.WriteAllBytesAsync(path, new byte[] { 1 });
        Assert.Equal(path, LocalSenseVoiceTranscriptionProvider.ResolveAvailableVadPath(settings));
        Assert.Null(LocalSenseVoiceTranscriptionProvider.ResolveAvailableVadPath(settings with { InstallVadModel = false }));
        Assert.Null(LocalSenseVoiceTranscriptionProvider.ResolveAvailableVadPath(settings with { LocalVadModelPath = "../unavailable.gguf" }));
    }

    [Fact]
    public async Task Provisioner_UsesTheSameCustomModelPathAsProvider()
    {
        using var files = new AppFiles();
        var settings = files.OfflineSettings();
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var provisioner = new RuntimeProvisioner(client);

        var report = await provisioner.EnsureForPlatformAsync(settings, isWindows: true);

        var model = report.Steps.Single(step => step.Name == "识别模型");
        Assert.Equal(ProvisionStatus.Failed, model.Status);
        Assert.Contains("custom-model.gguf", model.Detail);
        Assert.Equal(files.PathFor("custom-model.gguf"), LocalSenseVoiceTranscriptionProvider.ResolveModelPath(settings));
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("models\\sensevoice-small-q8.gguf")]
    [InlineData("models/sensevoice-small-q8.gguf")]
    public void SwitchingVariantWithoutCustomPath_UsesSelectedVariantDefault(string configuredPath)
    {
        var settings = new ClapBackSettings
        {
            LocalModelVariant = LocalModelVariant.ParaformerZh,
            LocalModelPath = configuredPath
        };

        Assert.Equal(
            LocalSenseVoiceTranscriptionProvider.ResolvePath(LocalModelCatalog.Get(LocalModelVariant.ParaformerZh).DefaultLocalPath),
            LocalSenseVoiceTranscriptionProvider.ResolveModelPath(settings));
    }

    [Fact]
    public void ProviderAndNormalizer_UseTheSharedPortableTempAudioDirectory()
    {
        var audioPath = LocalSenseVoiceTranscriptionProvider.CreateTemporaryAudioPath();
        var converterDirectory = AudioNormalizer.CreateTemporaryDirectory();

        Assert.Equal(AppPaths.TempAudioDirectory, Path.GetDirectoryName(audioPath));
        Assert.Equal(AppPaths.TempAudioDirectory, converterDirectory);
        Assert.True(Directory.Exists(converterDirectory));
        Assert.False(File.Exists(audioPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelPreparation_PropagatesFromRuntimeOrModelDownload(bool runtimeExists)
    {
        using var files = new AppFiles();
        var settings = files.OfflineSettings() with
        {
            AutoProvisionOnStartup = true,
            LocalModelAutoDownload = true,
            RequireModelIntegrity = false,
            InstallVadModel = false,
            ModelDownloadSource = ModelDownloadSource.HuggingFaceOnly
        };
        if (runtimeExists)
        {
            await File.WriteAllTextAsync(files.PathFor("runtime.exe"), "runtime placeholder");
        }

        using var cancellation = new CancellationTokenSource();
        using var handler = new CancelOnRequestHandler(cancellation);
        using var client = new HttpClient(handler);
        var provisioner = new RuntimeProvisioner(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provisioner.EnsureForPlatformAsync(settings, isWindows: true, cancellationToken: cancellation.Token));

        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class CancelOnRequestHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation did not reach the download request.");
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class AppFiles : IDisposable
    {
        private readonly string _relativeRoot = "product-flow-" + Guid.NewGuid().ToString("N");

        public AppFiles() => Directory.CreateDirectory(PathFor(string.Empty));

        public string RelativePathFor(string name) => Path.Combine(_relativeRoot, name);
        public string PathFor(string name) => Path.Combine(AppContext.BaseDirectory, _relativeRoot, name);

        public ClapBackSettings OfflineSettings() => new()
        {
            AutoProvisionOnStartup = false,
            LocalModelAutoDownload = false,
            LocalSttRuntimePath = RelativePathFor("runtime.exe"),
            LocalModelPath = RelativePathFor("custom-model.gguf"),
            LocalVadModelPath = RelativePathFor("vad.gguf")
        };

        public void Dispose() => Directory.Delete(PathFor(string.Empty), recursive: true);
    }
}

using System.IO.Compression;
using ClapBack.Core;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;
using ClapBack.Stt;
using ClapBack.Stt.Audio;
using Xunit;

namespace ClapBack.Tests;

/// <summary>
/// 覆盖这次新增的三件事：运行环境自动安装（含压缩包解压的安全边界）、
/// 麦克风设备枚举的解析、以及设置里必须锁定的信任根。
/// </summary>
public class ProvisioningAndDeviceTests
{
    // ------------------------------------------------------------ 压缩包解压

    [Fact]
    public async Task ExtractSingleEntry_FindsTargetInsideNestedFolder()
    {
        var directory = CreateTempDirectory();
        try
        {
            var archivePath = Path.Combine(directory, "runtime.zip");
            WriteZip(archivePath,
                ("funasr-llamacpp-v0.2.6/llama-funasr-sensevoice.exe", new byte[] { 1, 2, 3 }),
                ("funasr-llamacpp-v0.2.6/README.md", new byte[] { 9 }));

            var destination = Path.Combine(directory, "runtime", "llama-funasr-sensevoice.exe");
            await RuntimeProvisioner.ExtractSingleEntryAsync(
                archivePath, "llama-funasr-sensevoice.exe", destination, CancellationToken.None);

            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ExtractSingleEntry_WithoutTargetEntry_Throws()
    {
        var directory = CreateTempDirectory();
        try
        {
            var archivePath = Path.Combine(directory, "runtime.zip");
            WriteZip(archivePath, ("only-readme.md", new byte[] { 9 }));

            var destination = Path.Combine(directory, "llama-funasr-sensevoice.exe");
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => RuntimeProvisioner.ExtractSingleEntryAsync(
                    archivePath, "llama-funasr-sensevoice.exe", destination, CancellationToken.None));

            Assert.Contains("llama-funasr-sensevoice.exe", exception.Message);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ExtractSingleEntry_PathTraversalEntries_NeverLandOutsideDestination()
    {
        var directory = CreateTempDirectory();
        try
        {
            var archivePath = Path.Combine(directory, "runtime.zip");
            // 包里带一个典型的 zip-slip 条目和一个正常条目。
            WriteZip(archivePath,
                ("../../evil.exe", new byte[] { 6, 6, 6 }),
                ("llama-funasr-sensevoice.exe", new byte[] { 1, 2, 3 }));

            var destination = Path.Combine(directory, "runtime", "llama-funasr-sensevoice.exe");
            await RuntimeProvisioner.ExtractSingleEntryAsync(
                archivePath, "llama-funasr-sensevoice.exe", destination, CancellationToken.None);

            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination));
            // 只按目标文件名挑条目写到固定路径，包内的 ../ 从来没被写盘。
            Assert.False(File.Exists(Path.Combine(directory, "evil.exe")));
            Assert.False(File.Exists(Path.GetFullPath(Path.Combine(directory, "..", "evil.exe"))));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ExtractSingleEntry_ReplacesExistingFileAtomically()
    {
        var directory = CreateTempDirectory();
        try
        {
            var archivePath = Path.Combine(directory, "runtime.zip");
            WriteZip(archivePath, ("llama-funasr-sensevoice.exe", new byte[] { 7, 7 }));

            var destination = Path.Combine(directory, "llama-funasr-sensevoice.exe");
            await File.WriteAllBytesAsync(destination, new byte[] { 0 });

            await RuntimeProvisioner.ExtractSingleEntryAsync(
                archivePath, "llama-funasr-sensevoice.exe", destination, CancellationToken.None);

            Assert.Equal(new byte[] { 7, 7 }, await File.ReadAllBytesAsync(destination));
            // 中途的临时文件必须清干净，不留 *.extracting。
            Assert.Empty(Directory.GetFiles(directory, "*.extracting"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // ------------------------------------------------------------ 设备枚举解析

    [Fact]
    public void AudioDeviceListParser_ParsesDevicesWithNamesAndIndices()
    {
        var devices = AudioDeviceListParser.Parse(
            """{"devices":[{"index":0,"name":"MacBook Pro 麦克风"},{"index":2,"name":"AirPods"}]}""");

        Assert.Equal(2, devices.Count);
        Assert.Equal("MacBook Pro 麦克风", devices[0].Id);
        Assert.Equal(0, devices[0].Index);
        Assert.Equal("AirPods", devices[1].Id);
        Assert.Equal(2, devices[1].Index);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"devices\": 3}")]
    [InlineData("{\"other\": []}")]
    [InlineData("[1,2,3]")]
    public void AudioDeviceListParser_MalformedInput_ReturnsEmpty(string json)
    {
        Assert.Empty(AudioDeviceListParser.Parse(json));
    }

    [Fact]
    public void AudioDeviceListParser_IgnoresNegativeIndexAndBlankName()
    {
        var devices = AudioDeviceListParser.Parse(
            """{"devices":[{"index":-1,"name":"坏的"},{"index":4,"name":"  "}]}""");

        Assert.Single(devices);
        Assert.Equal(4, devices[0].Index);
        Assert.Equal("输入设备 4", devices[0].Name);
    }

    // ------------------------------------------------------------ 就绪判定

    [Fact]
    public void ProvisionReport_IsReady_WhenEveryStepSucceeded()
    {
        var report = new ProvisionReport(
        [
            new ProvisionStep("运行时", ProvisionStatus.Installed, "已安装"),
            new ProvisionStep("识别模型", ProvisionStatus.Ok, "已就绪")
        ]);

        Assert.True(report.IsReady);
    }

    [Fact]
    public void ProvisionReport_IsNotReady_WhenAnyStepFailed()
    {
        var report = new ProvisionReport(
        [
            new ProvisionStep("运行时", ProvisionStatus.Ok, "已就绪"),
            new ProvisionStep("识别模型", ProvisionStatus.Failed, "下载失败")
        ]);

        Assert.False(report.IsReady);
    }

    // ------------------------------------------------------------ 信任根

    [Fact]
    public void RuntimeAssetDigest_IsPinned()
    {
        var settings = new ClapBackSettings();

        Assert.Equal("runtime-llamacpp-v0.2.6", settings.RuntimeReleaseTag);
        Assert.Equal("funasr-llamacpp-windows-x64.zip", settings.RuntimeAssetName);
        // 必须逐字符等于 GitHub Release 资产的 digest（大写）。
        // 少抄一个字符就会让所有用户在安装运行时这一步被校验拦下。
        Assert.Equal(
            "F6A73A548413BA9FBAF2145263EA66EC53CBDAD1FB11790DBEEEE493E339492E",
            ModelIntegrity.NormalizeHex(settings.RuntimeAssetSha256));
    }

    [Fact]
    public void VadModelDigest_IsPinned()
    {
        var settings = new ClapBackSettings();

        Assert.Equal(64, ModelIntegrity.NormalizeHex(settings.VadModelSha256).Length);
        Assert.Equal("models\\fsmn-vad.gguf", settings.LocalVadModelPath);
    }

    // ------------------------------------------------ 运行时下载端点与重试

    [Fact]
    public void RuntimeEndpoint_DefaultIsGitHub()
    {
        Assert.Equal("https://github.com/", RuntimeProvisioner.DefaultRuntimeDownloadEndpoint);
    }

    [Theory]
    [InlineData("https://github.com")]
    [InlineData("https://github.com/")]
    [InlineData("https://mirror.example.com")]
    [InlineData("https://mirror.example.com/releases")]
    public void RuntimeEndpoint_HttpsIsAcceptedAndGetsTrailingSlash(string input)
    {
        var normalized = RuntimeProvisioner.NormalizeEndpoint(input);

        Assert.EndsWith("/", normalized);
    }

    [Theory]
    [InlineData("http://mirror.example.com/")]
    [InlineData("ftp://mirror.example.com/")]
    [InlineData("file:///tmp/")]
    [InlineData("not a url")]
    public void RuntimeEndpoint_InsecureOrMalformedIsRejected(string input)
    {
        Assert.Throws<ArgumentException>(() => RuntimeProvisioner.NormalizeEndpoint(input));
    }

    [Fact]
    public void RuntimeEndpoint_LoopbackHttpIsAllowed()
    {
        // 开发时本地起个下载服务来测，不该被 https 要求挡住。
        Assert.Equal("http://127.0.0.1:8080/", RuntimeProvisioner.NormalizeEndpoint("http://127.0.0.1:8080"));
    }

    /// <summary>
    /// 回归护栏：GitHub Release 的下载对部分网络是间歇性可达的（TCP/TLS 被随机重置）。
    /// 以前这条链路只试一次，结果就是「模型下得下来、运行时下不下来」。
    /// </summary>
    [Fact]
    public async Task RuntimeDownload_RetriesAndSucceedsAfterTransientFailures()
    {
        var root = CreateTempDirectory();
        var executableName = "retry-test-" + Guid.NewGuid().ToString("N") + ".exe";
        try
        {
            // 造一个合法的 zip：里面唯一的目标文件就是要装出来的 exe。
            var executableContent = new byte[] { 77, 90, 1, 2, 3, 4 };
            using var archiveStream = new MemoryStream();
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("funasr-llamacpp-windows-x64/" + executableName);
                using var entryStream = entry.Open();
                entryStream.Write(executableContent, 0, executableContent.Length);
            }

            var archiveBytes = archiveStream.ToArray();
            var archiveDigest = await ModelIntegrity.ComputeSha256HexAsync(new MemoryStream(archiveBytes));

            var attempts = 0;
            var handler = new StubHandler(_ =>
            {
                attempts++;
                return attempts < 3
                    ? throw new HttpRequestException("connection reset")
                    : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(archiveBytes),
                    };
            });

            var settings = new ClapBackSettings
            {
                RuntimeReleaseRepository = "owner/repo",
                RuntimeReleaseTag = "tag",
                RuntimeAssetName = "funasr-llamacpp-windows-x64.zip",
                RuntimeAssetSha256 = archiveDigest,
                LocalSttRuntimePath = Path.Combine("runtime", executableName),
                LocalModelAutoDownload = false,
            };

            var provisioner = new RuntimeProvisioner(new HttpClient(handler), new HuggingFaceModelDownloader());
            var report = await provisioner.EnsureAsync(settings);

            var runtimeStep = report.Steps.Single(step => step.Name == "本地识别运行时");
            Assert.Equal(ProvisionStatus.Installed, runtimeStep.Status);
            Assert.True(attempts >= 3, $"重试没有生效，只试了 {attempts} 次");

            // 装出来的必须是解压后的 exe，不是压缩包本身。
            var installed = Path.Combine(AppPaths.BaseDirectory, "runtime", executableName);
            Assert.Equal(executableContent, await File.ReadAllBytesAsync(installed));
        }
        finally
        {
            DeleteDirectory(root);
            TryDelete(Path.Combine(AppPaths.BaseDirectory, "runtime", executableName));
            TryDelete(Path.Combine(AppPaths.BaseDirectory, "runtime", executableName + ".sha256"));
        }
    }

    /// <summary>
    /// 回归护栏：暂存目录必须和落地产物同目录。曾把它放在 %LOCALAPPDATA%\ClapBack\runtime-temp，
    /// 结果是「下载成功却装不进去：Access to the path '...\runtime-temp' is denied」——
    /// exe 目录明明可写，却因为一个无关目录的权限而装不上。
    /// </summary>
    [Fact]
    public void StagingDirectoryLandsNextToTheArtifactNotInAppData()
    {
        var root = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(root, "runtime", "llama-funasr-sensevoice.exe");
            var staging = RuntimeProvisioner.CreateTemporaryDirectory(destination);

            Assert.True(Directory.Exists(staging));
            // 关键不变量：暂存目录必须落在落地产物自己的目录里（同卷、同权限边界）。
            // 不去断言"不在 AppData 下"——落地产物本身完全可能合法地位于 AppData。
            Assert.StartsWith(
                Path.GetDirectoryName(destination)!, staging, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
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
            // 测试清理是尽力而为。
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    // ------------------------------------------------------------ 辅助

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "clapback-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
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
            // 测试清理是尽力而为。
        }
    }

    private static void WriteZip(string archivePath, params (string Name, byte[] Content)[] entries)
    {
        using var stream = File.Create(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var entryStream = entry.Open();
            entryStream.Write(content, 0, content.Length);
        }
    }
}

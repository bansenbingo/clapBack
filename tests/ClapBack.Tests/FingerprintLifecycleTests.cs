using ClapBack.Stt;
using Xunit;

namespace ClapBack.Tests;

/// <summary>
/// 回归：指纹必须描述<b>它旁边那个文件自己</b>。
///
/// 曾经把 GitHub Release 压缩包的 digest 写成了「解压出来的 exe」的指纹，
/// 于是第二次启动算 exe 自己的哈希对不上 → 把刚装好的运行时删掉。
/// 而 <c>DeleteBroken</c> 又只删文件不删指纹，留下一个永远对不上的旧指纹，
/// 让这个部件从此再也装不回去——一个无法自愈的死状态。
/// </summary>
public class FingerprintLifecycleTests
{
    [Fact]
    public async Task Fingerprint_DescribesTheFileItself_NotItsContainer()
    {
        var path = Path.Combine(Path.GetTempPath(), "clapback-fp-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4 });

            var downloader = new HuggingFaceModelDownloader();
            await downloader.VerifyExistingFileAsync(
                path, expectedSha256: null, requireIntegrity: true,
                allowFirstTrust: true, cancellationToken: CancellationToken.None);

            var written = await ModelIntegrity.ReadFingerprintAsync(ModelIntegrity.FingerprintPathFor(path));
            var actual = await ModelIntegrity.ComputeSha256HexAsync(path, CancellationToken.None);

            Assert.Equal(actual, written);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".sha256");
        }
    }

    [Fact]
    public async Task StaleFingerprint_IsRemovedTogetherWithTheBrokenFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "clapback-fp-" + Guid.NewGuid().ToString("N") + ".exe");
        var fingerprint = ModelIntegrity.FingerprintPathFor(path);
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4 });
            // 伪造一个指向「别的内容」的指纹——正是压缩包哈希被误写进去时留下的样子。
            await ModelIntegrity.WriteFingerprintAsync(fingerprint, new string('a', 64), CancellationToken.None);

            var downloader = new HuggingFaceModelDownloader();
            await Assert.ThrowsAsync<InvalidDataException>(
                () => downloader.VerifyExistingFileAsync(
                    path, expectedSha256: null, requireIntegrity: true,
                    allowFirstTrust: true, cancellationToken: CancellationToken.None));

            // 关键：连指纹一起清掉。只删文件会留下毒指纹，让后续安装永远失败。
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(fingerprint));
        }
        finally
        {
            File.Delete(path);
            File.Delete(fingerprint);
        }
    }

    [Fact]
    public async Task AfterAFailedCheck_TheArtifactCanBeInstalledAndTrustedAgain()
    {
        var path = Path.Combine(Path.GetTempPath(), "clapback-fp-" + Guid.NewGuid().ToString("N") + ".exe");
        var fingerprint = ModelIntegrity.FingerprintPathFor(path);
        try
        {
            // 第一轮：坏文件 + 毒指纹 → 拒绝并清场。
            await File.WriteAllBytesAsync(path, new byte[] { 9, 9 });
            await ModelIntegrity.WriteFingerprintAsync(fingerprint, new string('b', 64), CancellationToken.None);
            var downloader = new HuggingFaceModelDownloader();
            await Assert.ThrowsAsync<InvalidDataException>(
                () => downloader.VerifyExistingFileAsync(
                    path, expectedSha256: null, requireIntegrity: true,
                    allowFirstTrust: true, cancellationToken: CancellationToken.None));

            // 第二轮：重新安装一个好文件 → 必须能被信任，不能因为上一轮的残留而卡死。
            await File.WriteAllBytesAsync(path, new byte[] { 7, 7, 7 });
            var result = await downloader.VerifyExistingFileAsync(
                path, expectedSha256: null, requireIntegrity: true,
                allowFirstTrust: true, cancellationToken: CancellationToken.None);

            Assert.Equal(await ModelIntegrity.ComputeSha256HexAsync(path, CancellationToken.None), result.Sha256Hex);
            Assert.Equal(
                await ModelIntegrity.ComputeSha256HexAsync(path, CancellationToken.None),
                await ModelIntegrity.ReadFingerprintAsync(fingerprint));
        }
        finally
        {
            File.Delete(path);
            File.Delete(fingerprint);
        }
    }
}

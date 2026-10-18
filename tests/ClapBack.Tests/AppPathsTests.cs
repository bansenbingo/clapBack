using ClapBack.Core;
using ClapBack.Stt;
using Xunit;

namespace ClapBack.Tests;

/// <summary>
/// 「模型下载到 exe 同目录的 models 文件夹」这条约定的护栏。
/// </summary>
/// <remarks>
/// 要点是<b>位置</b>：必须紧挨着 exe，绝不能跑到 <c>%APPDATA%</c>、<c>%TEMP%</c>
/// 或当前工作目录下面。玩家拿到一个 exe 就该拿到整个应用的全部状态。
/// </remarks>
public sealed class AppPathsTests
{
    [Fact]
    public void EnsureDirectories_CreatesModelsAndRuntimeFolders()
    {
        AppPaths.EnsureDirectories();

        Assert.True(Directory.Exists(AppPaths.ModelsDirectory));
        Assert.True(Directory.Exists(AppPaths.RuntimeDirectory));
    }

    [Fact]
    public void EnsureDirectories_IsIdempotent()
    {
        AppPaths.EnsureDirectories();
        AppPaths.EnsureDirectories();

        Assert.True(Directory.Exists(AppPaths.ModelsDirectory));
        Assert.True(Directory.Exists(AppPaths.RuntimeDirectory));
    }

    [Fact]
    public void ModelsFolderSitsNextToTheExecutable()
    {
        // 解析基点就是 exe 所在目录，models 必须是它的直接子目录。
        var baseDirectory = AppPaths.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(AppPaths.ModelsDirectory);

        Assert.Equal(baseDirectory, parent?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Assert.Equal("models", Path.GetFileName(AppPaths.ModelsDirectory));
    }

    [Fact]
    public void ModelsFolderIsNotInAppDataOrTemp()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.False(StartsWith(AppPaths.ModelsDirectory, appData));
        Assert.False(StartsWith(AppPaths.ModelsDirectory, localAppData));
        Assert.False(StartsWith(AppPaths.ModelsDirectory, temp));
    }

    [Fact]
    public void ModelPathResolvesInsideTheModelsFolder()
    {
        var resolved = LocalSenseVoiceTranscriptionProvider.ResolvePath("models\\sensevoice-small-q8.gguf");

        var expected = Path.Combine(AppPaths.ModelsDirectory, "sensevoice-small-q8.gguf");
        Assert.Equal(Path.GetFullPath(expected), resolved);
    }

    [Fact]
    public void RuntimePathResolvesInsideTheRuntimeFolder()
    {
        var resolved = LocalSenseVoiceTranscriptionProvider.ResolvePath("runtime\\llama-funasr-sensevoice.exe");

        var expected = Path.Combine(AppPaths.RuntimeDirectory, "llama-funasr-sensevoice.exe");
        Assert.Equal(Path.GetFullPath(expected), resolved);
    }

    private static bool StartsWith(string path, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return false;
        }

        var normalizedPrefix = Path.GetFullPath(prefix).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(normalizedPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, normalizedPrefix, StringComparison.OrdinalIgnoreCase);
    }
}

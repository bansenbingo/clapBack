namespace ClapBack.Core;

/// <summary>
/// 模型、运行时和推理临时音频目录约定：放在 exe 所在目录下面。
/// </summary>
/// <remarks>
/// <para>
/// 玩家拿到的是一个 exe，把它放进哪个文件夹，模型和运行时就跟着放哪个文件夹——
/// 用户配置由各平台设置存储管理，Windows 使用 <c>%APPDATA%/ClapBack</c>；
/// 备份或卸载时应另外处理配置文件。此类只管理模型、运行时和推理临时音频。
/// </para>
/// <para>
/// <b>基点是 <see cref="AppContext.BaseDirectory"/>。</b>自包含 / 单文件发布时
/// 它就等于 exe 所在目录，正好是需求要的位置；开发期用 <c>dotnet ClapBack.Desktop.dll</c>
/// 跑，它就是那个 dll 的目录。用同一个基点还有个硬性理由：
/// <c>LocalSenseVoiceTranscriptionProvider.ResolvePath</c> 用它做路径逃逸白名单，
/// 下载目标和校验基点必须是同一个目录，否则文件会被自己造的白名单拒掉。
/// </para>
/// <para>
/// 因此<b>不要把 exe 装到 Program Files 这类需要管理员权限的目录</b>——
/// 运行时要往它旁边写几百 MB 的模型文件。
/// </para>
/// </remarks>
public static class AppPaths
{
    /// <summary>语音识别模型目录名。</summary>
    public const string ModelsFolderName = "models";

    /// <summary>本地识别运行时（llama-funasr 可执行文件）目录名。</summary>
    public const string RuntimeFolderName = "runtime";

    /// <summary>临时音频目录名（录音中转，用完即删）。</summary>
    public const string TempAudioFolderName = "audio-temp";

    /// <summary>exe 所在目录（解析基点）。</summary>
    public static string BaseDirectory { get; } = Path.GetFullPath(AppContext.BaseDirectory);

    /// <summary><c>&lt;exe目录&gt;/models</c> —— 语音识别大模型下载到这里。</summary>
    public static string ModelsDirectory { get; } = Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), ModelsFolderName);

    /// <summary><c>&lt;exe目录&gt;/runtime</c> —— 本地识别引擎可执行文件。</summary>
    public static string RuntimeDirectory { get; } = Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), RuntimeFolderName);

    /// <summary><c>&lt;exe目录&gt;/audio-temp</c> —— 录音中转目录。</summary>
    public static string TempAudioDirectory { get; } = Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), TempAudioFolderName);

    /// <summary>
    /// 把这几个目录建出来。<b>第一次运行 exe 时就调用</b>，让玩家当场看到
    /// <c>models</c> 文件夹已经在那里，而不是等到第一次下载才出现。
    /// </summary>
    /// <remarks>
    /// 幂等：已存在就什么都不做。某个目录建不出来（比如目录被占用、只读）
    /// 只会抛异常给调用方，不会静默吞掉——那样玩家会看到一个空的 models
    /// 文件夹却没有任何提示。
    /// </remarks>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
    }
}

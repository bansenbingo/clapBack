namespace ClapBack.Core.Models;

public enum TranscriptionMode
{
    LocalFirst,
    LocalOnly,
    CloudOnly
}

/// <summary>
/// 可选的本地模型变体。
/// 注意：这里曾经有 <c>FunAsrNano2512</c>，已按需求移除——它的两个下载源指向的是不同产物
/// （Hugging Face 是 GGUF，ModelScope 是 <c>model.pt</c>），无法用一个锁定哈希同时代表，可靠性不足。
/// 枚举值按<b>名称字符串</b>持久化，所以删除中间项不会让旧配置错位。
/// </summary>
public enum LocalModelVariant
{
    SenseVoiceSmall,
    ParaformerZh
}

/// <summary>
/// 模型下载来源策略。默认 HuggingFaceFirst：优先 Hugging Face，仅在该源超时或网络不可用时才回退 ModelScope。
/// 供应链敏感的部署应选 HuggingFaceOnly，彻底禁止回退到第二来源。
/// </summary>
public enum ModelDownloadSource
{
    HuggingFaceFirst = 0,
    HuggingFaceOnly = 1,
    ModelScopeOnly = 2
}

public sealed record ClapBackSettings
{
    public string Hotkey { get; init; } = "CapsLock";
    /// <summary>
    /// 麦克风输入设备，按<b>设备名</b>记（不是索引——索引重启后会变）。留空 = 系统默认输入设备。
    /// 匹配不到时回退默认设备，绝不静默换到别的硬件。
    /// </summary>
    public string MicrophoneDeviceId { get; init; } = string.Empty;
    public int InputDeviceNumber { get; init; }
    public int MaxRecordingSeconds { get; init; } = 30;
    public string Language { get; init; } = "zh";
    public TranscriptionMode TranscriptionMode { get; init; } = TranscriptionMode.LocalOnly;
    public LocalModelVariant LocalModelVariant { get; init; } = LocalModelVariant.SenseVoiceSmall;

    // Local SenseVoiceSmall GGUF defaults. Relative paths are resolved from the app directory.
    public string LocalSttRuntimePath { get; init; } = "runtime\\llama-funasr-sensevoice.exe";
    public string LocalModelPath { get; init; } = "models\\sensevoice-small-q8.gguf";
    public string LocalVadModelPath { get; init; } = "models\\fsmn-vad.gguf";
    // 模型期望 SHA-256（大写十六进制）。留空表示未锁定，此时由 RequireModelIntegrity
    // 决定是拒绝运行还是退化为「下载时校验源端声明的摘要 + 首次信任指纹」。
    public string LocalModelSha256 { get; init; } = string.Empty;
    // The GGUF model is downloaded on first local use and kept outside Git.
    public bool LocalModelAutoDownload { get; init; } = true;
    // 模型来源策略，默认 Hugging Face 优先（见 ModelDownloadSource）。
    public ModelDownloadSource ModelDownloadSource { get; init; } = ModelDownloadSource.HuggingFaceFirst;
    // 开启后模型必须通过 SHA-256 完整性校验才允许交给运行时；关闭则退回只检查文件非空（不推荐）。
    public bool RequireModelIntegrity { get; init; } = true;
    // 本地识别运行时可执行文件的期望 SHA-256。留空表示未锁定。
    public string LocalRuntimeSha256 { get; init; } = string.Empty;
    public string HuggingFaceRepository { get; init; } = "FunAudioLLM/SenseVoiceSmall-GGUF";
    public string HuggingFaceRevision { get; init; } = "main";
    public string HuggingFaceFile { get; init; } = "sensevoice-small-q8.gguf";
    public int HuggingFaceTimeoutSeconds { get; init; } = 20;
    // 模型下载端点。部分网络会对 huggingface.co 做 DNS 劫持（解析到无关 IP）导致直连失败，
    // 此时可换成镜像，例如 https://hf-mirror.com/ 。换镜像不削弱安全性：内容仍要过 SHA-256 校验。
    // 留空使用官方端点。只接受 https://（本机回环地址除外）。
    /// <summary>
    /// Hugging Face 兼容端点。留空用内置默认 <c>https://hf-mirror.com/</c>（官方 <c>huggingface.co</c> 在不少网络
    /// 被 DNS 劫持，直连会超时；确认可直连时可填 <c>https://huggingface.co/</c>）。
    /// 只接受 https://（本机回环除外）。换镜像不削弱安全性：内容仍要过 SHA-256 校验。
    /// </summary>
    public string HuggingFaceEndpoint { get; init; } = string.Empty;

    /// <summary>ModelScope 端点，留空用 <c>https://www.modelscope.cn/models/</c>。同样只接受 https://。</summary>
    public string ModelScopeEndpoint { get; init; } = string.Empty;
    public string LocalSttBackend { get; init; } = "cpu";
    public int LocalSttThreads { get; init; } = 2;
    public int LocalSttTimeoutSeconds { get; init; } = 20;

    // macOS experiment settings. The Python adapter is intentionally separate
    // from the Windows WPF runtime and uses FunASR's ModelScope hub directly.
    public string MacOsPythonPath { get; init; } = ".venv-funasr/bin/python";
    public string MacOsFunAsrScriptPath { get; init; } = "tools/macos_funasr_transcribe.py";
    // 麦克风录音脚本。它同时提供 --list（枚举输入设备）给麦克风下拉框用。
    public string MacOsFunAsrRecordScriptPath { get; init; } = "tools/macos_funasr_record.py";
    public string MacOsFunAsrModel { get; init; } = "iic/SenseVoiceSmall";
    // CPU is the stable default on macOS; MPS can be enabled explicitly after
    // validating the local PyTorch build and model output.
    public string MacOsFunAsrDevice { get; init; } = "cpu";
    public int MacOsSttTimeoutSeconds { get; init; } = 300;

    // Cloud settings are retained for an optional provider or explicit fallback.
    public string Model { get; init; } = "glm-asr-2512";
    public string SttBaseUrl { get; init; } = "https://api.openai.com/v1";
    public string CloudProtocol { get; init; } = "WhisperCompatible";
    // 云端端点必须是 https://。仅在明确勾选后才允许 http://，避免音频与 Bearer 密钥明文外发。
    public bool AllowInsecureHttpEndpoint { get; init; }
    // LocalFirst 语义：仅当本地识别「抛异常」时才回退云端。
    // 本地返回空文本代表「确实没听清/没人说话」，默认不再把整段音频上传云端重试。
    public bool FallbackOnEmptyResult { get; init; }
    public string ChatOpenKey { get; init; } = "Enter";

    /// <summary>
    /// 识别完成后是否自动把文字粘贴进游戏聊天框并按回车发送。
    /// <para>
    /// 默认<b>false</b>：主流流程是「识别完 → 文字留在剪贴板 → 玩家在游戏里自己按 Ctrl+V」，
    /// 由玩家掌握发送时机和内容。设为 true 才让程序替你粘贴并回车（注入按键有风险，
    /// 参见 <c>TargetWindowProcessNames</c> 白名单）。
    /// </para>
    /// </summary>
    public bool AutoSend { get; init; }

    /// <summary>
    /// 自动发送完成后是否把剪贴板还原成发送前的内容。
    /// <para>
    /// 默认<b>false</b>：识别出来的文字要留在剪贴板里供玩家 Ctrl+V，还原会把它擦掉。
    /// 只有开了 <see cref="AutoSend"/> 且确实不想让文字留在剪贴板时才设为 true。
    /// </para>
    /// </summary>
    public bool RestoreClipboard { get; init; }

    /// <summary>
    /// 是否启用敏感词同音替换。默认<b>true</b>。
    /// <para>
    /// 识别结果会先把敏感词换成同音词（例如「傻逼」→「沙币」），再写进剪贴板。
    /// 目的是既能表达原意又不直接骂人。字库见 <see cref="HomophoneEntries"/>，
    /// 在 GUI 的「字库」页里增删改。
    /// </para>
    /// </summary>
    public bool EnableHomophoneSubstitution { get; init; } = true;

    /// <summary>
    /// 敏感词同音替换字库。留空表示不做替换（等价于关掉 <see cref="EnableHomophoneSubstitution"/>）。
    /// <para>
    /// 整个列表随设置一起持久化；在 GUI 的「字库」页里增删改。想恢复出厂值用那一页的
    /// 「恢复默认字库」，它会重新装入 <see cref="HomophoneDefaults.All"/>。
    /// </para>
    /// </summary>
    public IReadOnlyList<HomophoneEntry> HomophoneEntries { get; init; } = HomophoneDefaults.All;

    /// <summary>
    /// 是否把识别出的文字原文显示在通知气泡里。
    /// 默认关闭：气泡会出现在屏幕上，可能被截图、录屏或旁人看到，
    /// 与「日志不记录识别文本」的隐私承诺冲突。需要预览时再显式打开。
    /// </summary>
    public bool ShowTranscriptInNotification { get; init; }
    // 发送前必须确认前台窗口属于这些进程（逗号分隔，不含 .exe）；不匹配则拒绝注入文本。
    public string TargetWindowProcessNames { get; init; } = "Overwatch,Overwatch2";
    public bool RequireTargetWindowMatch { get; init; } = true;
    // 非 WAV 输入交给 ffmpeg 归一化；留空表示在 PATH 中查找 ffmpeg/ffmpeg.exe。
    public string FfmpegPath { get; init; } = string.Empty;
    public bool PlaySounds { get; init; } = true;
    public bool StartWithWindows { get; init; }

    // ---- 启动时的运行环境检测与自动安装 ----
    // 程序启动时检查「运行时可执行文件 / 识别模型 / VAD 模型」是否就绪，缺失就自动下载安装。
    // 关闭后只做检测、不下载（离线或供应链受限的部署用）。
    public bool AutoProvisionOnStartup { get; init; } = true;

    // FunASR llama.cpp 预编译运行时的 GitHub Release。
    public string RuntimeReleaseRepository { get; init; } = "modelscope/FunASR";
    public string RuntimeReleaseTag { get; init; } = "runtime-llamacpp-v0.2.6";

    /// <summary>
    /// 运行时下载端点，默认 默认 GitHub 官方。部分网络会对 <c>github.com</c> 的 Release 下载做链路阻断
    /// （DNS 正常、TCP/TLS 被重置），表现为「模型下得下来，运行时下不下来」。可以换成镜像前缀。
    /// 换镜像不削弱安全性：压缩包仍要过 <see cref="RuntimeAssetSha256"/> 的 SHA-256 校验，
    /// 期望值来自随源码评审发布的锁定哈希，与由哪个端点提供下载无关。留空用官方端点。
    /// 只接受 https://（本机回环地址除外）。
    /// </summary>
    public string RuntimeDownloadEndpoint { get; init; } = string.Empty;
    // 要安装的资产名。默认是通用 CPU 包（兼容性最好，但不含 AVX2/加速度）。
    // 有 AVX2/FMA/F16C/BMI2 的 CPU 可改成 funasr-llamacpp-windows-x64-avx2.zip；
    // 有 Vulkan 驱动可改成 funasr-llamacpp-windows-x64-vulkan.zip 并把 LocalSttBackend 设为 vulkan。
    public string RuntimeAssetName { get; init; } = "funasr-llamacpp-windows-x64.zip";
    // 上述资产的 SHA-256（大写十六进制）。来自 GitHub Release 资产的 digest 字段，随源码评审发布。
    // 换 RuntimeAssetName 时这里也必须换成对应资产的摘要，否则安装会拒绝执行。
    public string RuntimeAssetSha256 { get; init; } = "F6A73A548413BA9FBAF2145263EA66EC53CBDAD1FB11790DBEEEE493E339492E";

    // VAD 模型（FSMN-VAD）。长音频切分用；缺失不影响短语音识别。
    public bool InstallVadModel { get; init; } = true;
    public string VadHuggingFaceRepository { get; init; } = "FunAudioLLM/fsmn-vad-GGUF";
    public string VadHuggingFaceRevision { get; init; } = "main";
    public string VadHuggingFaceFile { get; init; } = "fsmn-vad.gguf";
    // 期望 SHA-256。已用 hf-mirror 与 ModelScope 两个独立源交叉核对一致（2026-10-15）。
    public string VadModelSha256 { get; init; } = "1270F2559C495F4E7B6E739541151027D360761A3FDA43FC147034F5719F5479";
}

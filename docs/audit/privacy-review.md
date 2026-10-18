# ClapBack 隐私与数据流专项审核报告

- 审核范围：.NET 8 solution "ClapBack"（Windows 托盘程序、Avalonia 跨平台 GUI、macOS FunASR sidecar、STT/凭据/设置/模型下载基础设施、README.md 与 PLAN.md 隐私承诺）
- 审核方式：严格只读源码审查（未修改任何源码）
- 审核日期：2026-10-15
- 严重性定义：**高** = 无同意即外发敏感数据或明文泄密；**中** = 特定配置/异常路径下违反隐私承诺或存在外发/泄露面扩大风险；**低** = 残留、可被截屏/继承等次要泄露面。

---

## 【结论摘要】

1. **默认配置（`TranscriptionMode.LocalOnly`）下音频不会离开本机**，Windows 主路径录音全程在内存（`WavAudioRecorder.cs:43-44`），临时 WAV 在成功/取消/失败/异常路径均有 `finally` 删除——核心承诺基本兑现，**无"默认即外发"的高危问题**。
2. 但存在 **3 条"未明确同意即上传"的静默外发路径**（中危）：非法/未知枚举值 fail-open 到云端回退、Desktop 保存设置解析失败回退 `LocalFirst`、`LocalFirst` 下本地识别结果为空文本也静默上传完整音频（与"失败时回退"承诺语义不符且全程无提示）。
3. **云端通道不强制 HTTPS**（中危）：`SttBaseUrl` 可配置为 `http://`，音频与 Bearer API Key 明文外发。
4. 临时 WAV 在**崩溃/进程被杀/删除失败**路径下会残留（含完整语音），且 `TryDelete` 静默吞掉删除失败（低危，但违反 PLAN.md:115"立即删除"的字面承诺）。
5. 识别文本通过 `NotificationRaised` 进入 Windows 通知气泡（`VoiceInputController.cs:112`），错误通知/异常消息携带用户路径与 stderr 片段（低危，日志本体无录音内容/识别文本/密钥——全库无持久日志）。
6. 凭据存储合规：Windows 用 Credential Manager（`WindowsCredentialStore.cs:38-71`），Desktop 仅读 `CLAPBACK_STT_API_KEY` 环境变量，**配置文件均不含明文密钥**（README:41 承诺兑现）；但环境变量被子进程继承（低危）。
7. 模型下载仅两处硬编码 HTTPS 端点（Hugging Face 主源 / ModelScope 回退），**无任何遥测**；`.gitignore` 覆盖 `models/`、`*.gguf`（README:85 承诺兑现），缺口为 `*.download` 残缺下载文件（低危）。

**高危：0 项；中危：4 项；低危：6 项。**

---

## 【逐项发现】

### 1. 音频数据生命周期

#### 1.1 Windows WPF 录音全程在内存，无临时文件 — 状态：符合
- **严重性**：无（合规）
- **文件:行号**：`src/ClapBack.Windows/Infrastructure/WavAudioRecorder.cs:43-44, 122-126`；`src/ClapBack.Core/Models/TranscriptionModels.cs:7-21`；`src/ClapBack.Core/VoiceInputController.cs:95`
- **证据**：`_buffer = new MemoryStream(); _writer = new WaveFileWriter(...)`（:43-44）；`StopAsync` 的 `finally { _writer?.Dispose(); _buffer?.Dispose(); }` 后 `result = new RecordedAudio(new MemoryStream(bytes, ...), "clapback.wav")`（:122-126）；`RecordedAudio : IDisposable` → `Content.Dispose()`（TranscriptionModels.cs:20）；调用方 `using var audio = await _recorder.StopAsync(...)`（VoiceInputController.cs:95）。
- **风险描述**：主录音路径不落盘，音频字节流生命周期随方法结束释放，隐私面最小。
- **修复建议**：无需修复；建议保持此实现，避免后续重构改为落盘缓冲。

#### 1.2 本地推理临时 WAV：正常路径删除，崩溃/锁定路径残留
- **严重性**：低
- **文件:行号**：`src/ClapBack.Stt/LocalSenseVoiceTranscriptionProvider.cs:83-101, 332-349`；`src/ClapBack.Desktop/MacOsMicrophoneRecorder.cs:60, 124-135, 163-166, 179-202, 226-234`；`src/ClapBack.Desktop/MacOsFunAsrTranscriptionProvider.cs:59, 63-66, 102-105, 261-269`；`tools/macos_funasr_record.py:106-111`
- **证据**：
  - Windows 本地推理：临时 WAV `Path.Combine(Path.GetTempPath(), $"clapback-{Guid.NewGuid():N}.wav")`（LocalSenseVoiceTranscriptionProvider.cs:83-85），`try/finally` 内 `TryDelete(tempWavPath)`（:86-101）；`TryDelete` 对 `IOException`/`UnauthorizedAccessException` **静默吞掉**（:332-349），无告警、无重试。
  - macOS 录音：`clapback-macos-record-{guid}.wav`（MacOsMicrophoneRecorder.cs:60）；`StopAsync`/异常路径 `finally CleanupAsync`（:124-135, 179-202）、`CancelAsync`（:163-166）均 `TryDelete`（:226-234 同样吞异常）。
  - FunASR 推理临时 WAV `clapback-macos-{guid}.wav`（MacOsFunAsrTranscriptionProvider.cs:59），`finally TryDelete`（:102-105, 261-269）。
  - 崩溃/被杀路径：`tools/macos_funasr_record.py:106-111` 的 Python 子进程会把完整录音写入 temp 后退出，宿主崩溃时无人清理；**全库无启动清扫逻辑**（grep 确认）。
- **风险描述**：成功/取消/失败/异常四条路径都会删除；但（a）进程崩溃或被强杀时含完整语音的 WAV 残留于 %TEMP%/`/var/folders/...`；（b）子进程短暂占用文件导致 `File.Delete` 抛 `IOException` 被吞，删除失败无声残留；（c）无启动清扫，残留长期存在。虽有 OS 用户目录 ACL 保护，但仍属语音数据残留，与 PLAN.md:115"立即删除临时文件"字面承诺不完全相符。
- **修复建议**：启动时扫描并删除 `clapback-*` 前缀的遗留临时文件；`TryDelete` 失败时降级重试或至少上报 `NotificationRaised`；考虑用 `FileOptions.DeleteOnClose` 或 `FILE_FLAG_DELETE_ON_CLOSE`。

#### 1.3 取消/停止边界：StartAsync 失败后 `_audioPath` 残留
- **严重性**：低
- **文件:行号**：`src/ClapBack.Desktop/MacOsMicrophoneRecorder.cs:43-85, 148-151`
- **证据**：`StartAsync` 中 `_audioPath = Path.Combine(...)`（:60）在进程启动（:67）之前赋值；启动失败抛异常后 `CancelAsync` 在 `_process is null` 时 `return`（:148-151），不执行 `CleanupAsync`。该路径下此时尚未产生文件，故当前无实际残留，但 `StopAsync` 里 `File.ReadAllBytesAsync(audioPath)`（:128-129）在未先 `CleanupAsync` 读取失败时，`finally` 仍会清理（:132-135），逻辑闭合。
- **风险描述**：当前无实际数据残留；属于防御性缺口，未来重构（如延后写文件）可能引入。
- **修复建议**：`CancelAsync` 在 `_process is null` 时也执行 `CleanupAsync`，保证无条件清理。

---

### 2. 数据外发

#### 2.1 默认模式为 LocalOnly，该路径无外发 — 状态：符合
- **严重性**：无（合规）
- **文件:行号**：`src/ClapBack.Core/Models/ClapBackSettings.cs:5-7, 23`；`src/ClapBack.Stt/CompositeTranscriptionProvider.cs:32-33`
- **证据**：`enum TranscriptionMode { LocalFirst, LocalOnly, CloudOnly }`（LocalFirst=0 是 `default` 值）；`TranscriptionMode = TranscriptionMode.LocalOnly`（:23）；`if (mode == TranscriptionMode.LocalOnly) return local;`（CompositeTranscriptionProvider.cs:32-33）。
- **风险描述**：与 README.md:72"默认识别模式为『仅本地』"一致，默认配置下不发起音频上传。
- **修复建议**：无需修复。

#### 2.2 【中危】未知/非法枚举值 fail-open 到云端回退
- **严重性**：中
- **文件:行号**：`src/ClapBack.Stt/CompositeTranscriptionProvider.cs:32-41, 43-75`；`src/ClapBack.Core/Models/ClapBackSettings.cs:5-7`；`src/ClapBack.Desktop/DesktopSettingsStore.cs:8-11, 38`；`src/ClapBack.Desktop/MainWindow.axaml.cs:165-166`
- **证据**：`CompositeTranscriptionProvider` 只显式处理 `LocalOnly`/`CloudOnly`，**其余一切值落到 `LocalFirst` 分支**（:32-41 的 if 链 + :43-75 的 else 逻辑），该分支会缓冲全部音频字节（:43-50）并在本地异常/空文本时上传云端（:72-76）。Desktop 配置 `DesktopSettingsStore`（:8-11 无 `JsonStringEnumConverter`）把 `TranscriptionMode` **序列化为数字**，UI 侧 `SelectedIndex = (int)settings.TranscriptionMode` / `(TranscriptionMode)ModeComboBox.SelectedIndex`（MainWindow.axaml.cs:165-166）依赖枚举顺序；枚举重排、手改 JSON 数字、未来新增枚举值（如 `Hybrid`）都会静默进入"上传云端"路径。
- **风险描述**：这是"未明确同意即上传"的 fail-open 路径：用户期望"仅本地"但配置解析异常时音频被发送到第三方云端，且全程无提示。违反 PLAN.md:24"避免未明确同意时上传录音"。
- **修复建议**：把 else 分支改为 fail-closed（未知值视为 `LocalOnly` 或直接拒绝并报错）；配置持久化改用字符串枚举（加 `JsonStringEnumConverter`），读取时对未知字符串显式报错而非回退。

#### 2.3 【中危】Desktop 保存设置解析失败回退 LocalFirst
- **严重性**：中
- **文件:行号**：`src/ClapBack.Desktop/MainWindow.axaml.cs:144-148`
- **证据**：`var mode = ... ?? "LocalFirst"`，`Enum.TryParse(...)` 失败时 `transcriptionMode = TranscriptionMode.LocalFirst`（:144-148）。
- **风险描述**：UI 文案与枚举名映射一旦失配（本地化、UI 改名、异常状态），用户点击"保存"即被改写为带云端回退的 LocalFirst，后续录音可能上传；无任何告警。
- **修复建议**：解析失败时保持当前已加载的设置值或报错，禁止静默回退到含云端行为的模式。

#### 2.4 【中危】LocalFirst 下"识别结果为空"即静默上传完整音频
- **严重性**：中
- **文件:行号**：`src/ClapBack.Stt/CompositeTranscriptionProvider.cs:54-75`；对照 `PLAN.md:80, 97`、`VoiceInputController.cs:97-112`
- **证据**：`if (localError is not null || string.IsNullOrWhiteSpace(localResult.Text)) localError ??= "本地识别未返回文本。";`（:56-61）随后 `cloud.TranscribeAsync(...)` 上传**全部音频字节**（:72-76），调用方 `VoiceInputController` 只根据最终文本发通知（:108-112），**没有任何"已使用云端"提示**。单元测试 `VoiceInputControllerTests.cs:65-78` 明确断言回退时上传完整音频（by design）。
- **风险描述**：用户没说话/录音为空/识别为空文本时也会把音频上传云端，与"本地失败后云端回退"的失败语义不符，且静默执行，属于"未明确同意即上传"路径（PLAN.md:24, 80）。空文本本身可能包含用户的环境声音等。
- **修复建议**：空文本不算"失败"，不触发回退；若保留回退，须在上传前弹出确认或至少用 `NotificationRaised` 明示"已将音频发送至云端"。

#### 2.5 【中危】云端通道不强制 HTTPS，音频与 API Key 可明文外发
- **严重性**：中
- **文件:行号**：`src/ClapBack.Stt/WhisperCompatibleTranscriptionProvider.cs:48-66, 123-131, 54`；`src/ClapBack.Core/Models/ClapBackSettings.cs:53`
- **证据**：`BuildEndpoint`（:123-131）只校验绝对 URI 并拼 `/audio/transcriptions`，**不检查 scheme 是否 https**；`SttBaseUrl` 默认 `"https://api.openai.com/v1"`（ClapBackSettings.cs:53）但可被设置为任意 `http://` 地址；上传内容为完整 WAV（:48-66）+ `Authorization: Bearer {apiKey}`（:54）；瞬态错误还会**重试重传**（:74-77）。
- **风险描述**：自建/内网 Whisper 兼容服务用 http 时，语音数据与 API Key 在网络上明文传输，可被同网段嗅探。错误响应体（截 300 字符）还可能进入异常消息（:80, 133-137）。
- **修复建议**：`BuildEndpoint` 强制 `Uri.Scheme == "https"`（或要求显式 opt-in 才允许 http）；重试前提示用户。

#### 2.6 网络外发点全集：无遥测 — 状态：符合
- **严重性**：无（合规）
- **文件:行号**：`src/ClapBack.Stt/HuggingFaceModelDownloader.cs:72, 93`；`src/ClapBack.Stt/WhisperCompatibleTranscriptionProvider.cs:123-131`；`tools/macos_funasr_server.py:59`
- **证据**：全部网络出口仅三处：用户配置的 `SttBaseUrl`（音频上传）、HuggingFace 模型下载、ModelScope 模型下载/回退（Python `AutoModel(hub="ms", disable_update=True)`，:59）。无统计上报、无崩溃上报、无使用遥测代码。
- **风险描述**：无。
- **修复建议**：保持；若未来加入遥测须默认关闭并写入隐私文档。

---

### 3. 日志脱敏

#### 3.1 无持久日志，但 NotificationRaised 携带完整识别文本
- **严重性**：低
- **文件:行号**：`src/ClapBack.Core/VoiceInputController.cs:108-112, 156-160`；`src/ClapBack.Windows/App.xaml.cs:86-88`；`tools/MacExperiment/Program.cs:16`
- **证据**：全库无日志框架/日志文件（grep `Debug.WriteLine`/Serilog/ILogger/`File.Append` 无命中）；`LastTranscript = text`（:108）为内存字段；`NotificationRaised($"识别完成：{text}", NotificationKind.Info)`（:112）→ Windows `ShowBalloonTip(1800, "ClapBack", message, ToolTipIcon.Info)`（App.xaml.cs:86-88）；MacExperiment `Console.WriteLine(result.Text)`（:16）。
- **风险描述**：PLAN.md:116 承诺"日志不记录识别文本"，日志层面确实没有记录；但识别文本以**通知气泡**形式展示，可能被直播/截屏/屏幕共享/通知历史捕获。CLI 实验工具把识别文本输出到 stdout（若被重定向即落盘）。
- **修复建议**：通知只显示"识别完成（N 字）"，完整文本仅留在 UI 面板；CLI 输出加 `--no-text` 或提示勿重定向。

#### 3.2 异常消息与通知携带用户路径、stderr 片段
- **严重性**：低
- **文件:行号**：`src/ClapBack.Stt/LocalSenseVoiceTranscriptionProvider.cs:157, 189, 202, 258, 351-355`；`src/ClapBack.Desktop/MacOsMicrophoneRecorder.cs:52-57, 116-122`；`src/ClapBack.Desktop/MacOsFunAsrTranscriptionProvider.cs:85, 166, 172, 220-221`；`src/ClapBack.Core/VoiceInputController.cs:158-160`；`src/ClapBack.Desktop/MainWindow.axaml.cs:64, 94, 109`
- **证据**：`FileNotFoundException($"找不到文件：{path}", path)`（LocalSenseVoiceTranscriptionProvider.cs:202, 258）；异常消息内嵌 `Sanitize(process.StandardError.ReadToEnd())`（:189；MacOsFunAsrTranscriptionProvider.cs:85, 166, 172），`Sanitize` 仅截 500/1000 字符去换行，**不做路径/PII 脱敏**（:351-355；MacOsMicrophoneRecorder.cs:236-237）；这些异常消息流入通知（VoiceInputController.cs:158-160）与 Desktop `TranscriptText.Text = ex.Message`（MainWindow.axaml.cs:64, 94, 109）。
- **风险描述**：通知/界面可能展示含用户名的绝对路径（如 `C:\Users\<name>\...`）与子进程 stderr，属于元数据 PII 泄露面；不含录音内容与识别文本。
- **修复建议**：`Sanitize` 增加路径脱敏（替换用户目录为 `~`）与长度限制；对外展示的异常只保留错误码+类别。

---

### 4. 凭据存储

#### 4.1 API Key 不落配置文件，Windows 走 Credential Manager — 状态：符合
- **严重性**：无（合规）
- **文件:行号**：`src/ClapBack.Windows/Infrastructure/WindowsCredentialStore.cs:15-78, 10, 44`；`src/ClapBack.Windows/App.xaml.cs:36-39`；`src/ClapBack.Windows/SettingsWindow.xaml.cs:44, 103-110`；`src/ClapBack.Windows/Infrastructure/JsonSettingsStore.cs:18-24, 49-61`；`src/ClapBack.Desktop/DesktopSettingsStore.cs:13-17`；`src/ClapBack.Core/Models/ClapBackSettings.cs:19-59`
- **证据**：Windows `CredWrite`/`CredRead`/`CredDelete`（WindowsCredentialStore.cs:15-78），目标名 `"ClapBack.Stt.ApiKey"`（App.xaml.cs:39；SettingsWindow.xaml.cs:103-110），`CredentialManagerPersistence.PersistLocalMachine = 2`（:10），凭据仅在 Credential Manager（DPAPI）内；`JsonSettingsStore`/`DesktopSettingsStore` 序列化的 `ClapBackSettings` **无任何密钥字段**（:19-59），明文 JSON 中不含 API Key。
- **风险描述**：无明文密钥落盘。
- **修复建议**：无需修复。

#### 4.2 Desktop 仅用环境变量 `CLAPBACK_STT_API_KEY`，但会被子进程继承
- **严重性**：低
- **文件:行号**：`src/ClapBack.Desktop/App.axaml.cs:27-33`；`src/ClapBack.Desktop/MacOsFunAsrTranscriptionProvider.cs:120-131`；`src/ClapBack.Stt/LocalSenseVoiceTranscriptionProvider.cs:112-124`
- **证据**：`_ => Task.FromResult(Environment.GetEnvironmentVariable("CLAPBACK_STT_API_KEY"))`（App.axaml.cs:27-33，注释明示"跨平台 shell 不持久化密钥"）；Python sidecar（MacOsFunAsrTranscriptionProvider.cs:120-131）与 llama.cpp 运行时（LocalSenseVoiceTranscriptionProvider.cs:112-124）均以 `ProcessStartInfo` 启动且**未清除/裁剪环境变量**。
- **风险描述**：API Key 明文存在于进程环境，同用户的子进程、崩溃转储、`/proc/<pid>/environ`（Linux）等可读取。跨平台配置文件本身不含密钥，README:41 承诺兑现；此为环境变量方案的固有泄露面。
- **修复建议**：启动子进程时构造最小环境（`psi.Environment.Clear()` 后仅注入必需变量）；macOS/桌面端可改用系统 keychain。

---

### 5. 配置与元数据

#### 5.1 持久化字段清单与敏感性
- **严重性**：低（元数据暴露）
- **文件:行号**：`src/ClapBack.Core/Models/ClapBackSettings.cs:19-59`；`src/ClapBack.Windows/Infrastructure/JsonSettingsStore.cs:21-24`；`src/ClapBack.Desktop/DesktopSettingsStore.cs:13-17, 8-11`
- **证据**：持久化字段：Hotkey、InputDeviceNumber、MaxRecordingSeconds、Language、TranscriptionMode、LocalModelVariant、LocalSttRuntimePath、LocalModelPath、LocalVadModelPath、LocalModelSha256、LocalModelAutoDownload、HuggingFaceTimeoutSeconds、LocalSttBackend、LocalSttThreads、LocalSttTimeoutSeconds、MacOsPythonPath、MacOsFunAsrModel、MacOsFunAsrDevice、MacOsSttTimeoutSeconds、Model、SttBaseUrl、CloudProtocol、ChatOpenKey、AutoSend、RestoreClipboard、PlaySounds、StartWithWindows。写入 `%APPDATA%\ClapBack\settings.json`（Windows）与 `%APPDATA%\ClapBack\desktop-settings.json`（Desktop），明文 JSON（`WriteIndented`）。敏感项：`SttBaseUrl`（暴露所用云服务）、`MacOsPythonPath`/`LocalSttRuntimePath`/`LocalModelPath` 可能含用户名的绝对路径。**无音频、无识别文本、无 API Key**。
- **风险描述**：低。配置文件可泄露用户名与第三方 STT 服务信息，但受用户目录 ACL 保护；Desktop 配置将 `TranscriptionMode` 存为数字（无 `JsonStringEnumConverter`，DesktopSettingsStore.cs:8-11），与 2.2 的 fail-open 叠加会静默改变外发行为。
- **修复建议**：枚举改字符串持久化；路径字段可做用户目录相对化（`~`）。

---

### 6. 模型下载隐私

#### 6.1 下载源 HTTPS 硬编码、无遥测、临时文件 finally 删除 — 状态：符合
- **严重性**：无（合规）
- **文件:行号**：`src/ClapBack.Stt/HuggingFaceModelDownloader.cs:72, 87-95, 56, 108-112, 157`；`src/ClapBack.Stt/LocalModelCatalog.cs:22-59, 69-74`；`src/ClapBack.Stt/LocalSenseVoiceTranscriptionProvider.cs:55-77, 221-231`；`tools/macos_funasr_server.py:59`；`tools/macos_funasr_transcribe.py:78-83`
- **证据**：主源 `https://huggingface.co`（:72），回退 `https://www.modelscope.cn/models`（:87-95），均硬编码 HTTPS；下载经 `?download=true`（:157）；临时文件 `{dest}.{guid}.download` 于 `finally` 内 `TryDelete`（:56, 108-112）；下载完成后 SHA-256 校验（LocalSenseVoiceTranscriptionProvider.cs:221-231），错误模型会被 `TryDelete` 且报 `InvalidDataException`（LocalModelCatalog.cs:69-74）。Python 侧 `AutoModel(..., hub="ms", disable_update=True)`（:59）禁用更新检查。**无任何遥测/统计上报**；下载请求不携带用户数据（仅标准 HTTPS GET）。
- **风险描述**：无隐私泄露；模型下载是出网行为但不上传任何本地数据。`LocalModelAutoDownload=true` 时首次本地识别前自动下载（网络行为但非数据外发）。
- **修复建议**：可在 UI 明示"将连接 Hugging Face/ModelScope"（PLAN.md:113 的许可证/SHA 展示部分已实现）。

#### 6.2 `.gitignore` 覆盖：`models/`、`*.gguf` 完整，`*.download` 有缺口
- **严重性**：低
- **文件:行号**：`.gitignore:1-13`（含 `models/`、`*.gguf`）；`src/ClapBack.Stt/HuggingFaceModelDownloader.cs:56`
- **证据**：`.gitignore` 含 `models/` 与 `*.gguf`（README:85 承诺兑现）；但残缺下载文件命名为 `{dest}.{guid}.download`（:56），若模型路径位于仓库根（默认 `models\...` 外的自定义路径），`xxx.gguf.<guid>.download` 既不匹配 `*.gguf` 也不匹配 `models/`，可被 `git add` 带入仓库；`runtime/`（外部 exe 放置目录）也未忽略。
- **风险描述**：模型权重/残缺文件可能被误提交（主要是体积与许可证问题，隐私影响低）。
- **修复建议**：`.gitignore` 增加 `*.download`、`runtime/`。

---

### 7. 承诺与实现一致性对照表

| # | 承诺（出处） | 实际行为 | 是否一致 | 证据（文件:行号） |
|---|---|---|---|---|
| 1 | 「本地识别不上传音频」「本地模式下音频只在本机处理，不上传云端」（README.md:94；PLAN.md:10） | `LocalOnly` 分支纯本地，不发起任何外网请求；但（a）未知/非法枚举值 fail-open 到云端回退；（b）Desktop 设置解析失败回退 `LocalFirst`；（c）`LocalFirst` 下空文本也上传且无提示 | **部分一致**（默认路径一致，存在 3 条静默外发路径） | `CompositeTranscriptionProvider.cs:32-75`；`ClapBackSettings.cs:5-7,23`；`MainWindow.axaml.cs:144-148`；`VoiceInputControllerTests.cs:65-78` |
| 2 | 「默认不保存录音」「默认不保留原始音频；本地推理完成、取消或失败后立即删除临时文件」（PLAN.md:10,115） | Windows 录音纯内存；临时 WAV 在成功/取消/失败/异常路径均 `finally` 删除；崩溃/被杀/删除失败（`TryDelete` 吞异常）路径残留，且无启动清扫 | **基本一致**（崩溃与删除失败路径残留） | `WavAudioRecorder.cs:43-44,122-126`；`LocalSenseVoiceTranscriptionProvider.cs:83-101,332-349`；`MacOsMicrophoneRecorder.cs:60,124-135,226-234`；`MacOsFunAsrTranscriptionProvider.cs:59,102-105`；`tools/macos_funasr_record.py:106-111` |
| 3 | 「日志只记录状态、耗时和错误码，不记录录音内容或识别文本」（PLAN.md:116,188） | 全库无持久日志，日志层面确实不含录音内容/识别文本/密钥；但识别文本经 `NotificationRaised` 进入通知气泡（`$"识别完成：{text}"`），错误通知/异常含用户路径与 stderr；承诺中的"耗时"从未实现（`TranscriptResult.Duration` 无赋值） | **部分一致**（日志无泄露，但通知通道泄露识别文本；"耗时"未实现） | `VoiceInputController.cs:108-112,156-160`；`App.xaml.cs:86-88`；`TranscriptionModels.cs:5`；`LocalSenseVoiceTranscriptionProvider.cs:189,202,258` |
| 4 | 「云端 API Key 不写入跨平台配置文件」「Windows 用 Credential Manager」（README.md:41,94；PLAN.md:112） | Windows：Credential Manager（`CredWrite`/`CredRead`，`"ClapBack.Stt.ApiKey"`）；Desktop：仅读 `CLAPBACK_STT_API_KEY` 环境变量；`settings.json`/`desktop-settings.json` 均无密钥字段 | **一致**（附注：环境变量被子进程继承） | `WindowsCredentialStore.cs:15-78`；`SettingsWindow.xaml.cs:103-110`；`App.axaml.cs:27-33`；`ClapBackSettings.cs:19-59`；`JsonSettingsStore.cs:49-61`；`DesktopSettingsStore.cs:33-39` |
| 5 | 「默认识别模式为『仅本地』」（README.md:72） | `TranscriptionMode = TranscriptionMode.LocalOnly` | **一致** | `ClapBackSettings.cs:23` |
| 6 | 「models/、*.gguf 已加入 .gitignore」（README.md:85） | 两者均在 `.gitignore`；`*.download` 残缺文件与 `runtime/` 未覆盖 | **一致**（有小缺口） | `.gitignore:1-13`；`HuggingFaceModelDownloader.cs:56` |
| 7 | 「云端 API Key 仅在启用云端模式时使用 DPAPI/Windows Credential Manager 加密保存」（PLAN.md:112） | Credential Manager（DPAPI 保护）；Desktop 用环境变量（非加密保存，但也不持久化） | **一致** | `WindowsCredentialStore.cs:38-71`；`App.axaml.cs:27-33` |
| 8 | 「本地推理子进程使用最小权限启动，不联网」（PLAN.md:114） | 子进程（llama.cpp 运行时、Python FunASR sidecar）未显式限制网络，也未裁剪环境变量（API Key 被继承）；"不联网"依赖子进程自身行为 | **部分一致**（网络未被技术性隔离） | `LocalSenseVoiceTranscriptionProvider.cs:112-124`；`MacOsFunAsrTranscriptionProvider.cs:120-131` |
| 9 | 「模型文件来源、许可证和 SHA-256 校验值在设置页展示」（PLAN.md:113） | SHA-256 字段持久化并在设置页展示；下载后强制校验 | **一致** | `ClapBackSettings.cs:41`；`LocalSenseVoiceTranscriptionProvider.cs:221-231` |
| 10 | PLAN.md:97 承诺 4 种 STT 模式（含"本地优先"） | 枚举仅 3 值：`LocalFirst, LocalOnly, CloudOnly`（`LocalFirst` 即本地优先/回退） | **一致**（4 种描述对应 3 枚举+组合语义） | `ClapBackSettings.cs:5-7` |
| 11 | 「默认配置为仅本地，避免未明确同意时上传录音」（PLAN.md:24） | 默认值确实为 `LocalOnly`；但配置损坏/解析失败时 fail-open 上传，等效于绕过"明确同意" | **部分一致** | `ClapBackSettings.cs:23`；`CompositeTranscriptionProvider.cs:32-75`；`MainWindow.axaml.cs:144-148` |

---

## 【修复优先级建议】

1. **中危**：`CompositeTranscriptionProvider` 改 fail-closed（未知模式 = 仅本地或报错）；空文本不触发云端回退；上传前提示用户（CompositeTranscriptionProvider.cs:32-75、MainWindow.axaml.cs:144-148）。
2. **中危**：`WhisperCompatibleTranscriptionProvider.BuildEndpoint` 强制 HTTPS（WhisperCompatibleTranscriptionProvider.cs:123-131）。
3. **低危**：临时 WAV 启动清扫 + `TryDelete` 失败重试/告警；通知不带识别文本；`Sanitize` 做路径脱敏；子进程环境裁剪；`.gitignore` 补 `*.download`、`runtime/`。

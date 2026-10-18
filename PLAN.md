# ClapBack Windows 语音转文字发送工具规划

## Summary

ClapBack 首版定位为 Windows 10/11 的守望先锋语音工具，包含主窗口和托盘：

1. 按一次全局热键开始录音。
2. 再按一次停止录音并交给本机部署的中文语音模型快速识别。
3. 自动打开聊天框、粘贴文字并按 Enter 发送。
4. 默认不保存录音；本地模式下音频只在本机处理，不上传云端。

项目按全新 .NET 项目实施；当前仓库中的 Whisper 兼容 HTTP 客户端保留为云端适配器，本地默认实现需按本计划接入。

## 当前实现边界

- 已实现本地 SenseVoiceSmall GGUF 的 Windows 子进程适配：模型/运行时/VAD 路径、SHA-256 校验、CPU/Vulkan/CUDA 后端、临时音频清理、超时、取消和低优先级启动。
- 已实现模型与运行时的完整性信任根：`LocalModelCatalog.cs` 硬编码各模型文件的 SHA-256（随源码评审发布），用户配置的 `LocalModelSha256` 只能收窄不能替换；下载后比对源端声明摘要（Hugging Face `X-Linked-Etag: "sha256:<hex>"`、ModelScope 裸 64 位十六进制）；没有内置锁定值的文件首次信任后落盘 `<artifact>.sha256` 指纹，之后每次使用复检，挡住校验后被替换的 TOCTOU。`RequireModelIntegrity=true`（默认）时无法自证的文件一律拒绝。
- 已实现本地模型首次使用时从 Hugging Face `FunAudioLLM/SenseVoiceSmall-GGUF` 下载 `sensevoice-small-q8.gguf`；模型缓存位于被 Git 忽略的 `models/` 目录，支持取消、临时文件和原子替换。
- 已实现 Hugging Face 下载超时/网络失败后自动回退 ModelScope；模型目录提供 `SenseVoiceSmall` 和中文 `Paraformer` 两个选项，两者都由 llama.cpp GGUF 运行时直接加载。**`Fun-ASR-Nano-2512` 已移除**：它的两个下载源指向不同产物（Hugging Face 是 `fun-asr-nano-2512-q8_0.gguf`，ModelScope 是 `model.pt`），无法用一个锁定哈希同时代表，可靠性不足。
- 已增加 macOS 实验适配：使用项目专用 `.venv-funasr` 安装 FunASR/ModelScope，通过 `iic/SenseVoiceSmall` 完成 WAV 单次识别；新增 `ClapBack.MacExperiment` 的 .NET 8 入口，Windows WPF 主链路不变。
- 已增加 `ClapBack.Desktop` Avalonia UI 跨平台 GUI：Windows/macOS 共用一套 .NET 8 XAML 界面，支持麦克风录音、本地识别、结果复制和设置持久化；macOS 选择 FunASR Python 适配器，Windows 选择现有 GGUF 适配器。**已移除「选择音频文件并识别」入口**——目标场景是实时同声传译，输入必须来自麦克风，文件导入只会掩盖麦克风链路的问题。非 WAV 归一化代码保留（见下）。
- macOS GUI 已接入麦克风录音：通过 `sounddevice`/PortAudio 采集 16 kHz 单声道 WAV，再交给 `MacOsFunAsrTranscriptionProvider` 调用 ModelScope `iic/SenseVoiceSmall`；录音权限和依赖安装步骤已写入 README。
- 已增加 macOS `.app` 打包：`scripts/build-macos-app.sh` 发布自包含 .NET 8/Avalonia bundle，生成 `dist/ClapBack.app`，支持 Finder 双击启动；路径解析兼容 Finder 启动时的非项目工作目录。
- 已修复 macOS 识别质量与速度问题：录音 sidecar 自动避开 Steam/BlackHole 等虚拟输入并优先 MacBook 内置麦克风；FunASR 改为常驻 JSON sidecar，模型只在首次识别时加载，默认 CPU 以保证输出稳定。
- 已实现本地、云端和本地失败后云端回退的统一选择层；默认配置为仅本地，避免未明确同意时上传录音。
- 云端层目前继续使用现有 Whisper 兼容 multipart `/audio/transcriptions` 形状；`glm-asr-2512` 已可作为模型 ID 配置，但其供应商专用 API 路径、鉴权和请求格式尚未在线验证，不能宣称 GLM 云端直连已完成。
- 当前 `IAudioRecorder` 仍在停止录音后提交完整 WAV，识别运行时按单次命令执行；常驻模型进程和录音期间的增量/部分结果尚未完成，不能宣称已经实现 800ms 首字延迟。
- 下一步应增加本地常驻 sidecar 或原生流式运行时协议，再将录音分块推送给 `Paraformer-zh-streaming` 或支持流式的 SenseVoice 管线。
- 已支持非 WAV 输入：`ClapBack.Stt/Audio` 把任意受支持输入归一化成 16 kHz 单声道 16 bit PCM WAV。WAV 走纯托管解码（PCM/IEEE float/EXTENSIBLE，8/16/24/32 bit，下混 + 线性插值重采样），其余格式走 ffmpeg（`ArgumentList` 逐项传参、60 秒超时、`Kill(entireProcessTree:true)`）；缺 ffmpeg 时明确报错而不是发错格式的音频。云端上传按真实容器声明 MIME 与文件名。
- 已允许把 Hugging Face / ModelScope 端点换成镜像（`HuggingFaceEndpoint`/`ModelScopeEndpoint`，只接受 `https://`，本机回环除外），用于直连被 DNS 劫持的网络；换镜像不削弱安全性，内容仍要过 SHA-256。**默认端点就是镜像 `https://hf-mirror.com/`**——`huggingface.co` 在不少网络被 DNS 劫持到无关 IP（实测解析到 Facebook 地址段），直连必然超时；确认可直连时把 `HuggingFaceEndpoint` 填回 `https://huggingface.co/`。
- 已按隐私与安全审核结论收紧默认行为：未知/非法 `TranscriptionMode` 一律 fail-closed 到「仅本地」（设置以名称字符串持久化，读到不认识的值整份设置作废而不是猜）；`LocalFirst` 下空文本默认不再静默上传（`FallbackOnEmptyResult` 显式开启才回退）；云端端点强制 HTTPS；发送前校验目标窗口进程白名单，不匹配则一个键都不注入；剪贴板按格式整体快照与恢复，发送间隙用户复制了新内容就不覆盖。
- 已实现**启动时检测并安装运行环境**（`RuntimeProvisioner`）：依次检查本地识别运行时、识别模型、VAD 模型，缺哪样装哪样，并把每一步的结果摊开显示在 GUI 的「运行环境」面板里；补齐之前不放行录音。运行时来自 `modelscope/FunASR` 的 GitHub Release `runtime-llamacpp-v0.2.6`（默认 CPU 包 `funasr-llamacpp-windows-x64.zip`，可切 AVX2 / Vulkan / CUDA），下载后按 GitHub 资产 `digest` 校验再**只解出 `llama-funasr-sensevoice.exe` 这一个文件**写到 `runtime/`——不做整包解压，压缩包里的 `../` 路径穿越条目根本不会被写盘。
- 已实现**麦克风选择**：GUI「设置 → 麦克风」下拉框列出本机输入设备（Windows 走 NAudio/WaveInEvent，macOS 走 `tools/macos_funasr_record.py --list`）。配置里存**设备名**而不是下标——下标会随插拔和驱动更新漂移；找不到就退回系统默认设备，绝不「找不到就随便挑一个硬件」。`WavAudioRecorder` 与设备枚举移到 `ClapBack.Stt/Audio/`，让 WPF 托盘和 Avalonia GUI 共用同一份实现。
- **重要教训（Hugging Face Xet 迁移）**：Hugging Face 现在把 LFS 文件放在 Xet 存储后端，`HttpClient` 跟随 302 后拿到的**最终响应**里 `X-Linked-Etag` 已消失，只剩一个 64 位十六进制的 Xet `ETag`，它**不是文件的 SHA-256**。旧实现把它当摘要用，导致每一次合法下载都被判成「声明值不一致」而丢弃。现改为：裸 64 位十六进制只从 `X-Linked-Etag` 认（ModelScope 的约定），普通 `ETag` 必须显式带 `sha256:` 前缀。同时明确主从关系：**内置锁定哈希是信任根，命中即放行，源端声明值不能反过来否决它**。

## 技术与模块

- 技术栈：.NET 8 + WPF + MVVM，x64 Windows 客户端。
- `ClapBack.Core`
  - 录音状态机：`Idle → Recording → Transcribing → Sending → Success/Error`
  - 配置模型、错误码、日志接口、取消与超时策略。
- `ClapBack.Windows`
  - 托盘图标和设置窗口。
  - 使用 `RegisterHotKey` 注册全局热键。
  - 使用 NAudio/WASAPI 采集 16 kHz、单声道 PCM 音频。
  - 使用 Windows Clipboard API 保存、粘贴并尽量恢复用户原剪贴板。
  - 使用标准 Windows 输入模拟打开聊天框、粘贴和回车，不读取游戏内存、不注入 DLL。
- `ClapBack.Desktop`
  - 基于 Avalonia 11 FluentTheme 的 Windows/macOS 共用桌面窗口。
  - 通过 `StorageProvider` 选择 PCM WAV 音频并显示识别结果；其他格式转换列为后续工作。
  - macOS 通过 Python 录音 sidecar 完成开始/停止录音，录音结果在内存中交给 FunASR，不保留临时音频。
  - 通过系统剪贴板复制结果，配置写入用户应用数据目录，不与 Windows WPF 设置文件混用。
  - 当前是跨平台识别实验入口；录音、全局热键和游戏聊天发送继续由 Windows WPF 链路负责，后续再抽象为跨平台服务。
- `ClapBack.Stt`
  - 定义供应商抽象：
    ```csharp
    interface ITranscriptionProvider
    {
        Task<TranscriptResult> TranscribeAsync(
            Stream audio,
            TranscriptionOptions options,
            CancellationToken cancellationToken);
    }
    ```
  - `ITranscriptionProvider` 继续作为唯一识别抽象；实现 `LocalSenseVoiceProvider`、`CloudWhisperCompatibleProvider`，并由 `CompositeTranscriptionProvider` 按设置选择本地、云端或失败回退。
  - 本地首选 `SenseVoiceSmall-GGUF` Q8（约 234M 参数、模型文件约 254MB），通过 FunASR/llama.cpp 的 Windows x64 运行包调用；不要求 Python 常驻，适合与游戏同时运行。
  - 支持配置模型文件路径、运行时可执行文件路径、CPU/Vulkan 后端、线程数、语言、VAD、超时和取消；默认低于正常优先级运行并限制线程，避免抢占游戏资源。
  - 模型文件首次使用前检查存在性、文件大小和 SHA-256 校验值；模型下载、升级和删除由设置页显式触发。校验分三层：内置锁定哈希（信任根）→ 下载时源端声明摘要 → 落盘指纹 `<artifact>.sha256` 每次复检；`RequireModelIntegrity=true` 时无法自证即拒绝执行，不允许「空哈希跳过校验」。
  - 本地运行时尽量常驻并预加载模型，录音期间按 VAD/分块进行增量推理；停止热键时只提交最终片段，避免每次重新加载模型。
  - 运行时进程使用临时 WAV 或本地流式协议，读取结构化文本输出；超时或崩溃时终止子进程并返回可重试错误。
  - 默认语言为中文，支持中英文混合；保留热词和标点后处理扩展点。
  - 云端适配器保留 Base URL、模型 ID、协议、API Key、超时和重试配置，支持 `glm-asr-2512` 等模型；不得把 GLM 的具体鉴权或路径硬编码进核心层。
- `ClapBack.Tests`
  - 状态机、配置、识别服务、剪贴板和发送流程测试。

## 核心交互

### 录音与发送流程

**主流程是「识别 → 文字进剪贴板 → 玩家自己在游戏里按 Ctrl+V」。** 程序的职责到「把文字放进剪贴板」为止，发送时机和内容由玩家掌握。

- 首次按热键：
  - 检查麦克风权限和识别配置。
  - 开始录音。
  - 托盘图标变为“录音中”，可播放短提示音。
- 再次按热键：
  - 停止录音并进入识别状态。
  - 默认最长录音 30 秒，超过后自动停止。
  - 本地模式在常驻模型进程中推理并获取文字，不发起外网请求；云端模式或回退策略明确启用时才发送音频。
  - **先做敏感词同音替换**（把「傻逼」这类词换成「沙币」「啥波一」等同音词），字库可配置。替换必须发生在写剪贴板之前，保证玩家 `Ctrl+V` 粘出去的就是替换后的文字。
  - **把识别出来的文字写进系统剪贴板并留在那里。**
  - 提示「已复制到剪贴板，在游戏中按 Ctrl+V 粘贴」。
- 仅当 `AutoSend = true`（**默认 false**）时才额外执行：
  - 打开守望先锋聊天框。
  - 粘贴文字。
  - 自动按 Enter 发送。
  - `RestoreClipboard = true`（**默认 false**）时才把剪贴板还原成发送前的内容；默认不还原，好让文字继续留在剪贴板里供玩家再粘一次。
- 识别期间再次按热键：
  - 忽略并提示“正在识别”。
  - 设置中提供取消当前任务的操作。
- 空识别结果、本地模型未安装、模型加载失败、推理超时、云端请求失败、聊天框发送失败时：
  - 不执行回车。
  - 显示错误通知。
  - 保留识别文本到临时结果区域，允许复制重试。

### 可配置项

- 全局热键。
- 麦克风设备。
- STT 模式：本地优先、本地仅用、云端仅用或本地失败后云端回退。
- 本地 STT 运行时路径。
- 本地模型文件路径、模型名称和 SHA-256 校验值。
- 推理后端（CPU 或可用 GPU）、线程数、语言和超时。
- 云端 STT Base URL、协议、模型 ID（例如 `glm-asr-2512`）和 API Key。
- 最大录音时长。
- 聊天框打开按键和目标频道。
- 自动发送开关。
- 是否恢复原剪贴板。
- 敏感词同音替换开关与字库（GUI 的「字库」页里增删改，随设置持久化，可一键恢复默认）。
- 模型下载端点（`HuggingFaceEndpoint` / `ModelScopeEndpoint`，默认 hf-mirror + ModelScope）与运行时下载端点（`RuntimeDownloadEndpoint`，默认 GitHub）。
- 运行时/模型/VAD 的下载源版本与资产名（`RuntimeReleaseTag` / `RuntimeAssetName`），以及各自的锁定 SHA-256。
- 开机启动。
- 提示音和通知开关。

## 安全、隐私与兼容性

- 本地模式不需要 STT API Key；本地模型路径和推理参数写入应用配置。
- 云端 API Key 仅在启用云端模式时使用 DPAPI/Windows Credential Manager 加密保存。
- 模型文件来源、许可证和 SHA-256 校验值在设置页展示，禁止静默执行不明来源的模型或运行时。
- 本地推理子进程使用最小权限启动，不联网；必要时通过 Windows 防火墙规则验证无外连。
- 默认不保留原始音频；本地推理完成、取消或失败后立即删除临时文件。
- 日志只记录状态、耗时和错误码，不记录录音内容或识别文本。
- 仅使用公开 Windows 输入和剪贴板能力，不附加守望先锋进程、不修改游戏文件、不读取内存。
- 明确提示：自动输入行为仍需遵守暴雪账号和游戏服务条款。
- 首版支持窗口化、无边框和全屏窗口模式；对独占全屏做兼容性测试。
- 需要处理权限不一致问题：若守望先锋以管理员权限运行，程序也需提供管理员运行提示。

## 里程碑

1. **项目骨架**
   - 创建 solution、WPF 托盘程序、MVVM 基础结构。
   - 完成全局热键注册和状态通知。

2. **录音模块**
   - 接入 NAudio。
   - 完成麦克风选择、权限检查、最长时长和临时文件清理。

3. **本地中文语音识别**
   - 集成并固定受支持的 FunASR/llama.cpp Windows x64 运行时及 `SenseVoiceSmall-GGUF` Q8 模型。
   - 保持模型常驻，完成 VAD/分块增量识别、最终片段确认、超时、取消、崩溃恢复、错误映射和耗时统计。
   - 在同一目标硬件上比较 CPU 限线程与 Vulkan 两种模式，默认选择对游戏帧率影响更小的模式。
   - 以 `Paraformer-zh-streaming` 作为第二候选：若实测需要更低的首字延迟，再评估其 Python/ONNX 或本地服务集成成本。
   - 用中文、夹杂英文和游戏术语的短 WAV 样本完成离线 smoke test，确认模型加载、VAD 分段和识别结果。

4. **云端识别兼容层**
   - 保留现有 Whisper 兼容 HTTP 客户端接口，并抽象协议/鉴权/模型 ID。
   - 增加 `glm-asr-2512` 适配验证；云端仅在用户选择、模型不可用或本地回退策略触发时调用。
   - 云端与本地均返回统一的 `TranscriptResult`，核心状态机不感知供应商差异。

5. **守望先锋发送器**
   - 实现聊天框打开、剪贴板粘贴、Enter 发送。
   - 加入剪贴板备份/恢复、输入失败保护和自动发送开关。

6. **设置与安全**
   - 完成设置页面、Credential Manager 存储、日志脱敏和开机启动。

7. **测试与发布**
   - 完成 Windows 安装包、版本信息、崩溃日志和基础更新机制。
   - 目标为 Windows 10 22H2+ / Windows 11，x64。

## 测试计划

- 单元测试：
  - 热键状态转换。
  - 录音超时和取消。
  - 本地进程启动、模型缺失/校验失败、超时、取消、空结果和错误码。
  - 云端协议选择、`glm-asr-2512` 模型 ID、API Key 缺失/错误和本地失败回退。
  - 剪贴板保存/恢复。
  - 识别失败时禁止误发送。
- 集成测试：
  - 使用 Mock STT 服务验证完整链路。
  - 验证不同麦克风、无麦克风和设备切换。
  - 验证中文、英文、中英混合及标点。
- Windows 手工测试：
  - 守望先锋窗口化、无边框、全屏。
  - 不同聊天频道和键盘布局。
  - 游戏前台、切出游戏、权限不一致。
  - 模型加载失败、GPU 不可用时回退 CPU、游戏运行时 CPU/GPU 占用、长音频、长文本和剪贴板被其他程序占用。
- 验收目标：
  - 在目标硬件上，15 秒以内中文语音从停止录音到发送完成通常不超过 2 秒；首次加载模型的耗时单独记录。
  - 增量识别首个稳定片段延迟目标不超过 800ms；最终文本以停止热键后的确认结果为准。
  - 游戏前台运行时，识别期间帧率和 1% low 相对未启用 STT 的基线下降不超过 5%。
- 断网状态下仍可完成本地模式中已安装模型的识别和发送；云端模式断网时必须明确失败且不误发送。
  - 识别失败时不误按回车。
  - 连续运行 2 小时无崩溃、无持续资源泄漏。

## 默认假设

- 首版只支持守望先锋 Windows 客户端，后续通过 `ITextSender` 接口扩展其他游戏。
- 首版本地默认采用 `SenseVoiceSmall-GGUF` Q8：中文优先、约 254MB 模型文件、官方提供 Windows 边缘运行路径，且官方资料报告 CPU 约 17× 实时；这是游戏并行场景的首选。
- `Paraformer-zh-streaming`（约 220M、约 600ms 分块）作为低延迟备选，但只有在目标硬件实测优于 SenseVoice 且可接受 Windows 部署复杂度时切换。
- `Fun-ASR-Nano`（约 800M）和本地 `GLM-ASR-Nano-2512`（1.5B、约 4.5GB 权重）不作为默认本地模型，以免占用过多内存/显存；GLM-ASR-2512 保留为云端 API 选项。
- 首版默认识别后复制到剪贴板，由用户在聊天框按 Ctrl+V；自动打开聊天框并发送需要在设置中显式开启。
- 支持本地优先与云端回退，不做云端录音历史、账号系统和移动端应用；模型下载统计和日志不记录录音内容或识别文本。

## 模型对比与选型依据

| 候选 | 中文/方言 | 规模 | 实时与部署 | 本项目结论 |
|---|---|---:|---|---|
| [SenseVoiceSmall](https://github.com/QwenAudio/SenseVoice) | 中文、粤语、英/日/韩；适合短语音 | 234M；Q8 GGUF 约 254MB | FunASR/llama.cpp 提供 Windows x64 CPU/Vulkan 路径；官方资料报告 CPU 约 17× 实时 | **本地默认**：体积、中文效果、速度和 C# 子进程集成最均衡 |
| [Paraformer-zh-streaming](https://github.com/modelscope/FunASR) | 中文、英语 | 约 220M | 官方示例为约 600ms 分块流式；Windows 原生常驻集成需要额外 Python/ONNX 或服务封装 | **低延迟备选**：用于实测首字延迟不达标时替换 |
| [Fun-ASR-Nano](https://github.com/QwenAudio/Fun-ASR) | 中文、英语、日语及中文方言 | 约 800M；GGUF 组合约 1GB | GPU/更高资源需求；不适合与游戏共享资源的默认配置 | 暂不作为本地默认 |
| [GLM-ASR-Nano-2512](https://github.com/zai-org/GLM-ASR) | 中文、粤语及多语言 | 1.5B；本地权重约 4.5GB | 本地部署资源较重；云端 API 可避免占用游戏机器 | **保留云端**：通过统一云端适配器支持 `glm-asr-2512` |

选型以目标机器上的实测为最终依据：同一组中文/中英混合/游戏术语样本，分别记录首字延迟、最终延迟、字符错误率、峰值 RAM/VRAM、游戏 FPS/1% low 和连续运行稳定性；不得只依据仓库宣传 benchmark 宣称达标。

# ClapBack

Windows 语音转文字工具：按一次热键开始录音，再按一次停止识别，结果自动复制到剪贴板。在游戏聊天框按 `Ctrl+V` 即可粘贴。

## Windows 快速开始

当前 Windows 主入口是 **`ClapBack.exe`**，包含主窗口、全局热键、托盘、设置和字库编辑。本次本地发布目录为 `dist/windows-mvp/`。

1. 双击 `dist/windows-mvp/ClapBack.exe`。请保留同目录的原生 DLL，不要单独搬走 exe。
2. 首次启动会检查并准备本地识别运行时和模型，进度显示在主窗口底部。下载期间可以取消准备，或直接打开“设置与字库”调整网络端点、模型路径或识别模式。
3. 在“设置与字库 → 录音与游戏”中选择麦克风、热键和最长录音时长，点击“保存并应用”。默认热键为 `CapsLock`，默认最长 30 秒。
4. 按热键或“开始录音”，说完再按一次。到达时限也会自动停止并识别。
5. 识别结果会显示在窗口中，并自动复制到剪贴板。复制失败时文字仍保留，可点击“复制文字”重试。
6. 在游戏中打开聊天框，按 `Ctrl+V`，自行按 Enter 发送。

关闭主窗口会收起到托盘；右键托盘可重新打开窗口、进入设置或退出。识别中可以取消本次语音，退出时会结束当前任务。

### 默认行为

- 默认“仅本地”，录音不会上传云端。首次安装所需组件需要网络；组件就绪后可离线识别。
- 默认只复制文字，`AutoSend=false`。如需自动打开聊天框、粘贴并发送，在设置中显式开启，并在游戏前台使用全局热键录音。
- 自动发送的默认目标进程为 `Overwatch,Overwatch2`，聊天键可选 Enter 或 F2。
- 识别前应用同音替换；预览、剪贴板和发送使用同一份文本。“字库”页支持新增、修改、删除和恢复默认，点击“保存并应用”后生效。
- 热键支持 CapsLock、F1–F24 和 Ctrl/Alt/Shift/Win 组合，例如 `Ctrl+Shift+Space`。新热键注册失败时保留原热键。
- VAD 是可选组件，未安装不会阻止短语音识别。
- 识别结果默认去掉句末的句号（模型常补一个「。」，粘进游戏聊天框反而碍事）；问号、感叹号保留。可在“识别与下载”里关闭。
- 最小化会收起到右下角托盘（可在“录音与游戏”里关闭这一行为）。
- 默认以管理员身份启动：启动时若当前不是管理员，会用 UAC 提权重启自己；在 UAC 上点“否”则以普通权限继续运行，识别功能不受影响。可在“录音与游戏”里关掉。
- 目标进程与识别语言都提供下拉选择（目标进程会先列出 `Overwatch/Overwatch2`，再补上本机当前有窗口的进程），仍可手动编辑，避免手打进程名出错。

## 游戏内字符气泡

识别时在画面上显示一个置顶、半透明、不抢键盘焦点的小窗，用来在游戏里看到识别结果：

- 录制时显示“正在听你说…”，识别完成后显示文字，到点自动隐藏。
- 字号（12–72）、不透明度（0.2–1.0）、显示时长（1–60 秒）都在“游戏气泡”页调整，可先点“预览气泡”看效果。
- 直接用鼠标把气泡拖到想要的位置即可：位置会记住，下次仍从那里弹出；“气泡位置复位”会回到屏幕底部居中。

气泡是普通置顶窗口，独占全屏的游戏可能盖住它；请使用“无边框窗口 / 窗口化全屏”模式。

## 本地识别环境

Windows 使用 FunASR / llama.cpp GGUF 运行时，默认模型为 SenseVoiceSmall Q8；设置中也可选择 Paraformer 中文 Q8。

```text
windows-mvp/
    ClapBack.exe
    *.dll                           原生运行库，随程序一起保留
    models/
        sensevoice-small-q8.gguf     默认识别模型，约 254 MB
        fsmn-vad.gguf                可选 VAD
    runtime/
        llama-funasr-sensevoice.exe
    audio-temp/                     推理期间的临时 WAV，结束后清理
```

运行时默认来自 `modelscope/FunASR` 的 GitHub Release `runtime-llamacpp-v0.2.6`，资产为 `funasr-llamacpp-windows-x64.zip`。模型默认从 `https://hf-mirror.com/` 下载，失败后按设置回退到 ModelScope。模型和本地引擎按需下载，不嵌入应用 exe。

若无法下载，在“识别与下载”中展开下载端点设置后重试。也可以自行放置匹配版本的组件，再关闭自动准备或自动下载。相对路径以 **exe 所在目录** 为基点，与启动时的工作目录无关。请把程序放在当前用户可写的文件夹中。

已有的完整性校验保持原状，本轮重点是功能和交互，没有扩展哈希、安全审计或签名方案。组件安装的详细实现见 `RuntimeProvisioner`、`LocalModelCatalog` 和 `HuggingFaceModelDownloader`。

## 设置和数据位置

- Windows 设置与字库：`%APPDATA%/ClapBack/settings.json`。
- Windows 云端 API Key：Windows Credential Manager，凭据名 `ClapBack.Stt.ApiKey`。
- Avalonia 桌面实验入口：独立使用 `%APPDATA%/ClapBack/desktop-settings.json`，与 Windows 主程序不共用设置文件。
- Windows 模型、运行时和临时推理音频位于程序目录下。备份设置时需另外备份上述用户配置文件；整个应用并非完全便携。

## 云端识别

Windows 设置中可选“仅云端”或“本地优先（失败时上传云端）”。填写服务 URL、模型名和 API Key 后保存。仅云端模式直接跳过本地组件准备。

接口使用 Whisper 兼容的 `multipart /audio/transcriptions` 请求。服务 URL 通常以 `/v1` 结尾；模型名由服务供应商决定。默认值 `glm-asr-2512` 只是可配置模型名，不代表已验证某家 GLM 专用接口。没有有效服务地址和密钥时不能完成云端转写。

本地优先模式会在本地识别失败时通知并上传音频到配置的服务；空识别结果默认不触发上传。

## 构建与发布

需要 Windows 和 .NET 8 或更新的 SDK。源码项目目标为 .NET 8，普通 `dotnet test` 需要匹配的 .NET 8 运行时。

```powershell
dotnet restore ClapBack.sln
dotnet build ClapBack.sln -c Release
dotnet test tests/ClapBack.Tests/ClapBack.Tests.csproj -c Release
dotnet publish src/ClapBack.Windows/ClapBack.Windows.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false `
    -o dist/windows-mvp
```

自包含发布自带 .NET 运行时，目标机器不需要另装 .NET。WPF 原生 DLL 必须随 exe 分发，运行时不依赖将它们解压到 `%TEMP%/.net`。解决方案配置为 `Any CPU`，无需向整个 solution 传 `-p:Platform=x64`。

## Avalonia 桌面与 macOS 实验入口

`ClapBack.Desktop` 使用 Avalonia 11，在 Windows/macOS 上提供麦克风录音、时限自动停止、结果预览、自动复制和设置/字库编辑。Windows 游戏场景建议使用上面的 WPF 主入口；Avalonia 入口尚未实现全局热键和游戏自动发送，不显示这些无效选项。

```bash
dotnet run --project src/ClapBack.Desktop -c Release
```

macOS 先准备 Python 环境：

```bash
./scripts/macos-funasr-setup.sh
./scripts/build-macos-app.sh
```

生成位置为 `dist/ClapBack.app`。Apple Silicon 默认 `osx-arm64`；Intel 可设置 `MACOS_RID=osx-x64`。应用包含 .NET/Avalonia，FunASR Python 环境仍需单独准备，并在设置中填写解释器路径。

macOS 使用 `sounddevice` / PortAudio 采集音频，首次运行需允许麦克风权限；FunASR 通过常驻 Python sidecar 复用模型，默认 CPU。macOS 环境检查不再下载 Windows GGUF 引擎或 VAD。桌面入口的云端密钥从 `CLAPBACK_STT_API_KEY` 环境变量读取。

## 实现范围

- `ClapBack.Core`：录音状态机、取消/时限、配置、热键解析、同音替换。
- `ClapBack.Stt`：GGUF / macOS FunASR / Whisper 兼容接口、音频归一化、组件准备。
- `ClapBack.Windows`：WPF 主窗口、托盘、全局热键、设备选择、剪贴板、可选游戏发送。
- `ClapBack.Desktop`：Avalonia 跨平台实验界面。
- `ClapBack.Tests`：状态机、音频、配置、下载与功能回归测试。

Windows 当前按“停止录音后单次识别”工作，尚未实现录音期间增量转写或常驻 GGUF 引擎，不承诺流式首字延迟。实际麦克风、识别速度、GPU 后端、守望先锋聊天输入和 macOS 行为需要在对应设备上验证。

详细规划见 [PLAN.md](PLAN.md)。

# clapBack

Windows 托盘工具：按一次全局热键开始录音，再按一次将语音转成文字并发送到守望先锋聊天框。

## 当前状态

工程已按 [`PLAN.md`](PLAN.md) 建立为 .NET 8 solution：

- `ClapBack.Core`：状态机、配置模型和跨平台接口。
- `ClapBack.Stt`：本地 SenseVoiceSmall GGUF 运行时适配器、Whisper 兼容云端客户端和本地/云端选择层。
- `ClapBack.Windows`：WPF 托盘程序、CapsLock 全局热键、NAudio 录音、剪贴板发送和 Windows Credential Manager。
- `ClapBack.Desktop`：基于 Avalonia UI 的 Windows/macOS 跨平台桌面界面；提供本地音频文件选择、识别结果预览、复制和设置持久化。
- `ClapBack.Tests`：核心状态机测试。

## Windows/macOS 跨平台 GUI

GUI 框架选用 [Avalonia UI](https://avaloniaui.net/) 11：它在 Windows 和 macOS 上共享同一套 XAML/.NET 8 界面代码，避免维护两套原生窗口，同时可以复用 `ClapBack.Core` 与 `ClapBack.Stt`。启动桌面 GUI：

```bash
DOTNET_ROOT=/usr/local/share/dotnet /usr/local/share/dotnet/dotnet run \
  --project src/ClapBack.Desktop -c Release
```

当前跨平台 GUI 已支持：

- 选择 PCM WAV 文件并调用本地识别提供程序（其他格式转换列为后续工作）。
- 在 macOS 上通过“开始录音/停止并识别”直接采集 16 kHz 单声道麦克风音频，随后调用本地 FunASR SenseVoiceSmall。
- 展示识别状态和结果，一键复制到系统剪贴板。
- 保存识别模式、模型、macOS FunASR Python 路径、热键和自动发送选项。

### macOS App 启动

仓库提供 macOS `.app` 打包脚本。执行一次构建后，可直接在 Finder 中双击应用，之后不需要命令行启动：

```bash
./scripts/build-macos-app.sh
```

生成位置：`dist/ClapBack.app`。当前 Apple Silicon 默认使用 `osx-arm64`；Intel Mac 可使用 `MACOS_RID=osx-x64 ./scripts/build-macos-app.sh`。`.app` 会包含 .NET 运行时和 Avalonia 原生库，但 FunASR Python 环境仍安装在仓库的 `.venv-funasr` 中。

macOS 默认使用 `MacOsFunAsrTranscriptionProvider` 调用 `.venv-funasr` 中的 FunASR；Windows 默认使用现有 SenseVoiceSmall GGUF 运行时。桌面 GUI 的云端模式读取 `CLAPBACK_STT_API_KEY` 环境变量，不会把密钥写入跨平台配置文件；Windows 生产托盘程序仍使用 Credential Manager。录音、全局热键和守望先锋聊天发送仍沿用 Windows WPF 托盘链路，跨平台统一录音/热键抽象列为下一阶段工作。

首次在 macOS 使用 GUI 前，请先安装 FunASR 及麦克风采集依赖：

```bash
./scripts/macos-funasr-setup.sh
DOTNET_ROOT=/usr/local/share/dotnet /usr/local/share/dotnet/dotnet run \
  --project src/ClapBack.Desktop -c Release
```

首次点击“开始录音”时，macOS 会请求麦克风权限；请在“系统设置 → 隐私与安全性 → 麦克风”中允许终端或 ClapBack。录音进程达到设置中的最长时长后会自动停止，默认 30 秒。

当前本地识别适配器会在停止录音后启动 FunASR llama.cpp 子进程完成一次 SenseVoiceSmall 推理，并支持模型校验、超时、取消和本地/云端模式选择。录音期间的增量转写和常驻模型进程仍是后续工作；当前不能把它描述为完整流式识别。

## Windows 构建

在 Windows 10 22H2 或 Windows 11、安装 .NET 8 SDK 后执行：

```powershell
dotnet restore ClapBack.sln
dotnet build ClapBack.sln -c Release -p:Platform=x64
dotnet test tests/ClapBack.Tests/ClapBack.Tests.csproj -c Release
dotnet publish src/ClapBack.Windows/ClapBack.Windows.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishReadyToRun=true
```

首次启动后右键托盘图标进入设置。默认识别模式为“仅本地”，需要准备 FunASR llama.cpp Windows 运行时、`SenseVoiceSmall-GGUF` Q8 模型和可选的 FSMN-VAD 模型：

- 运行时：`runtime/llama-funasr-sensevoice.exe`
- 模型：`models/sensevoice-small-q8.gguf`
- VAD：`models/fsmn-vad.gguf`

模型不会提交到 GitHub。首次选择本地识别并开始识别时，程序会自动从 Hugging Face 下载默认模型：

- 仓库：`FunAudioLLM/SenseVoiceSmall-GGUF`
- 分支：`main`
- 文件：`sensevoice-small-q8.gguf`
- 本地缓存：`models/sensevoice-small-q8.gguf`

下载使用临时文件完成后再原子替换，下载失败或取消不会留下可被误用的半成品。可在设置 JSON 中将 `LocalModelAutoDownload` 设为 `false`，改为只使用已存在的本地模型；`models/`、`*.gguf` 已加入 `.gitignore`，不会随源码上传。

设置中的本地模型下拉框还提供：

- `Fun-ASR-Nano-2512 Q8`：Hugging Face 使用 `FunAudioLLM/Fun-ASR-Nano-2512-GGUF` 的 Q8 GGUF；ModelScope 回退源为官方 `FunAudioLLM/Fun-ASR-Nano-2512/model.pt`。后者是 FunASR/PyTorch 格式，需要对应的外部运行时，不能直接交给当前 llama.cpp GGUF 运行时。
- `Paraformer 中文 Q8`：使用 `FunAudioLLM/Paraformer-GGUF` 的 Q8 GGUF，Hugging Face 和 ModelScope 均可下载，适合中文低延迟备选。

每个模型都先尝试 Hugging Face；超过配置的下载超时时间或发生网络错误后自动切换 ModelScope。ModelScope 下载同样写入被 Git 忽略的本地模型缓存。

本地识别不上传音频。设置中也可以选择“本地优先（失败时云端回退）”或“仅云端”，配置 Whisper 兼容服务 URL、模型（例如 `glm-asr-2512`）和 API Key。云端 API Key 使用 Windows Credential Manager 保存。默认全局热键为 CapsLock。

当前云端实现使用 Whisper 兼容的 `multipart /audio/transcriptions` 请求；`glm-asr-2512` 已作为可配置模型 ID 保留，但 GLM 供应商专用 API 的请求格式仍需单独验证。

## macOS 语音转文字实验

macOS 不运行 Windows 的 `llama-funasr-sensevoice.exe`。仓库提供了一个不影响 Windows WPF 主程序的 Python 实验适配器，直接调用 FunASR 的 ModelScope `iic/SenseVoiceSmall` 模型：

```bash
./scripts/macos-funasr-setup.sh
.venv-funasr/bin/python tools/macos_funasr_transcribe.py /path/to/recording.wav
# 或使用 .NET 8 macOS 试验入口
dotnet run --project src/ClapBack.MacExperiment -- /path/to/recording.wav
```

首次执行识别时，FunASR 会把模型缓存到本机 ModelScope 缓存目录；默认自动选择 Apple Silicon MPS，遇到 MPS 不兼容时可加 `--device cpu`。录音按钮已经接入 macOS GUI；全局热键和守望先锋聊天发送 UI 仍是后续工作。

## 注意事项

程序使用标准 Windows 全局热键、剪贴板和输入模拟，不读取或修改守望先锋进程内容。自动输入行为仍需遵守游戏服务条款；建议先在自定义房间验证聊天频道和发送按键。

# clapBack

Windows 托盘工具：按一次全局热键开始录音，再按一次将语音转成文字并发送到守望先锋聊天框。

## 当前状态

工程已按 [`PLAN.md`](PLAN.md) 建立为 .NET 8 solution：

- `ClapBack.Core`：状态机、配置模型和跨平台接口。
- `ClapBack.Stt`：本地 SenseVoiceSmall GGUF 运行时适配器、Whisper 兼容云端客户端和本地/云端选择层。
- `ClapBack.Windows`：WPF 托盘程序、CapsLock 全局热键、NAudio 录音、剪贴板发送和 Windows Credential Manager。
- `ClapBack.Tests`：核心状态机测试。

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

## 注意事项

程序使用标准 Windows 全局热键、剪贴板和输入模拟，不读取或修改守望先锋进程内容。自动输入行为仍需遵守游戏服务条款；建议先在自定义房间验证聊天频道和发送按键。

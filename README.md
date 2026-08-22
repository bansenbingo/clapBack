# clapBack

Windows 托盘工具：按一次全局热键开始录音，再按一次将语音转成文字并发送到守望先锋聊天框。

## 当前状态

工程已按 [`PLAN.md`](PLAN.md) 建立为 .NET 8 solution：

- `ClapBack.Core`：状态机、配置模型和跨平台接口。
- `ClapBack.Stt`：Whisper 兼容 API 客户端。
- `ClapBack.Windows`：WPF 托盘程序、F13 全局热键、NAudio 录音、剪贴板发送和 Windows Credential Manager。
- `ClapBack.Tests`：核心状态机测试。

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

首次启动后右键托盘图标进入设置，填写 Whisper 兼容服务 URL 和 API Key。默认服务地址为 `https://api.openai.com/v1`，默认全局热键为 F13。

## 注意事项

程序使用标准 Windows 全局热键、剪贴板和输入模拟，不读取或修改守望先锋进程内容。自动输入行为仍需遵守游戏服务条款；建议先在自定义房间验证聊天频道和发送按键。

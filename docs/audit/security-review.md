# ClapBack 应用安全专项审核报告

- **审核对象**：ClapBack .NET 8 solution（Windows 托盘语音输入工具 + macOS 实验版）
- **审核范围**：子进程安全、模型供应链完整性、运行时与模型加载、输入模拟与剪贴板、密钥与配置、依赖与构建、路径与文件处理、Python sidecar
- **审核方式**：严格只读源码审计（未修改任何源码；本报告为唯一产出文件）
- **审核日期**：2026-10-15
- **威胁模型**：同用户权限下的恶意软件/本地低权限攻击者、网络中间人与被入侵的模型托管源、误触发导致的非预期输入注入

---

## 【结论摘要】

ClapBack 在**进程启动方式**上做得规范：全部外部进程使用 `UseShellExecute=false` + `ArgumentList` 传参，无 shell 拼接、无命令注入；下载链路强制 HTTPS 且对仓库路径做转义；临时文件统一使用 GUID 命名，不可预测。这些是值得保持的基线。

但存在 **3 项高危** 问题，核心在于"完整性校验形同虚设"与"输入注入无目标校验"：

1. **模型与运行时均无有效完整性校验**：`LocalModelCatalog.cs` 中没有任何硬编码哈希；SHA-256 期望值来自用户可写的 `settings.json`，且默认为空字符串 —— 空值分支会**完全跳过校验**（`LocalSenseVoiceTranscriptionProvider.cs:221`），下载器也只检查"文件非空"（`HuggingFaceModelDownloader.cs:137-140`）。恶意 GGUF/被替换的 `llama-funasr-sensevoice.exe` 会被直接加载执行。
2. **外部运行时 exe 仅检查存在性即执行**（`LocalSenseVoiceTranscriptionProvider.cs:254-260`），无签名/哈希校验，构成用户权限下的任意代码执行与持久化通道。
3. **SendInput 输入注入不校验目标窗口**（`OverwatchTextSender.cs:23,33-36`），叠加 `AutoSend` 默认开启（`ClapBackSettings.cs:56`）与录音超时自动发送（`VoiceInputController.cs:141-154`），误触发时识别文本会经"剪贴板+Ctrl+V+Enter"注入到**任意前台窗口**（终端、聊天、浏览器等）。

此外有 8 项中危：ModelScope 回退源/浮动 revision 的供应链信任边界、`settings.json` 控制的路径逃逸、剪贴板非文本内容永久丢失、剪贴板 TOCTOU、云端 BaseURL 未强制 HTTPS（Bearer 密钥可被诱导明文外发）、发布产物无签名、macOS 脚本解析的祖先目录劫持、Python sidecar 任意 ModelScope 模型 ID。

依赖包（NAudio 2.2.1、Avalonia 11.3.9 等）在可查漏洞库中未发现已知 CVE，但审核环境无法联网执行 `dotnet list package --vulnerable` 复核，需在发布流程中常态化。

---

## 【逐项发现】

### 专项 1：子进程安全

#### 1-1　严重性：**高**　外部运行时仅凭"文件存在"即执行（跨专项 3-1）

- **文件:行号**：`src/ClapBack.Stt/LocalSenseVoiceTranscriptionProvider.cs:254-260`、`:112-123`
- **证据**：
  ```csharp
  private static void ValidateRuntime(string runtimePath)
  {
      if (!File.Exists(runtimePath))
      {
          throw new FileNotFoundException("本地语音识别运行时不存在", runtimePath);
      }
  }
  ...
  StartInfo = new ProcessStartInfo
  {
      FileName = runtimePath,
      UseShellExecute = false,
      ...
  ```
- **攻击场景**：`runtime\llama-funasr-sensevoice.exe` 无 Authenticode 签名校验、无哈希校验。应用若安装在用户可写目录（便携版常见），同用户恶意软件用任意程序替换该 exe；下一次按热键识别时以当前用户权限执行攻击者代码（且继承全部环境变量，见 1-3）。
- **修复建议**：对运行时 exe 做 SHA-256 校验（哈希硬编码在代码或随发布清单提供），或至少校验 Authenticode 签名主体；把 runtime/ 目录设为仅受信任安装器可写。

#### 1-2　严重性：**中**　macOS 脚本/解释器解析按"祖先目录遍历 + CWD 优先"，可被脚本劫持

- **文件:行号**：`src/ClapBack.Stt/MacOsFunAsrTranscriptionProvider.cs:202-218`、`src/ClapBack.Desktop/MacOsMicrophoneRecorder.cs:204-224`
- **证据**：
  ```csharp
  private static string ResolvePath(string path)
  {
      if (Path.IsPathRooted(path)) return path;               // 绝对路径直接信任
      var workingPath = Path.GetFullPath(path, Directory.GetCurrentDirectory());
      if (File.Exists(workingPath)) return workingPath;        // CWD 优先于应用目录
      var appPath = Path.GetFullPath(path, AppContext.BaseDirectory);
      if (File.Exists(appPath)) return appPath;
      var directory = new DirectoryInfo(AppContext.BaseDirectory);
      for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
      {
          var candidate = Path.Combine(directory.FullName, path);
          if (File.Exists(candidate)) return candidate;        // 向上 8 层父目录
      }
      return appPath;
  }
  ```
- **攻击场景**：应用位于 `~/Downloads/clapBack/dist/...` 之类深层目录时，攻击者在任一可写的祖先目录放置 `tools/macos_funasr_server.py` 或 `.venv-funasr/bin/python`，即可让应用执行其指定的解释器/脚本（用户权限任意代码执行）；`MacOsPythonPath` 绝对路径分支（`:204`）同样直接受 settings 控制。
- **修复建议**：固定解析基点为 `AppContext.BaseDirectory`，移除父目录遍历与 CWD 优先；对解析结果做目录前缀白名单校验；打包时把脚本放入 .app bundle 内部并校验其哈希。

#### 1-3　严重性：**低**　子进程继承完整环境变量（含 `CLAPBACK_STT_API_KEY`）

- **文件:行号**：`src/ClapBack.Stt/LocalSenseVoiceTranscriptionProvider.cs:142-146`
- **证据**：
  ```csharp
  process.StartInfo.Environment["OMP_NUM_THREADS"] = threads.ToString();
  process.StartInfo.Environment["GGML_NUM_THREADS"] = threads.ToString();
  ```
  仅新增两个变量，未清理其余环境；`CLAPBACK_STT_API_KEY` 会随 `StartInfo.Environment` 继承给 llama.cpp 子进程（Python sidecar 同理）。
- **攻击场景**：被替换的运行时（1-1）或被劫持的 Python sidecar（1-2）可直接读取云端 API Key。
- **修复建议**：启动子进程前剔除 `CLAPBACK_STT_API_KEY` 等敏感变量（`process.StartInfo.Environment.Remove(...)`），或改用仅注入白名单变量的启动方式。

#### 1-4　严重性：**低**　超时 Kill 覆盖面良好，但失败被静默吞掉、macOS 侧异常处理不完整

- **文件:行号**：`src/ClapBack.Stt/LocalSenseVoiceTranscriptionProvider.cs:163-182`、`:309-330`；`src/ClapBack.Stt/MacOsFunAsrTranscriptionProvider.cs:193-198`；`src/ClapBack.Desktop/MacOsMicrophoneRecorder.cs:110-112,183-191`
- **证据**：
  ```csharp
  // LocalSenseVoice：超时抛 TimeoutException 且 finally 兜底 Kill，entireProcessTree:true
  catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
  {
      TryKill(process);
      throw new TimeoutException($"本地语音识别超过 {timeoutSeconds} 秒");
  }
  ...
  catch (System.ComponentModel.Win32Exception) { /* Best-effort cleanup only. */ }
  ```
  macOS 侧 `StopServer()` / `CleanupAsync()` 只 `catch (InvalidOperationException)`，`process.Kill` 抛出的 `Win32Exception` 会冒泡。
- **攻击场景**：Windows 上 `TryKill` 吞掉 `Win32Exception` 后不重试，被挂起/保护的残留子进程可能持续占用 CPU；macOS 上 Kill 失败会掩盖原始识别错误并中断清理（临时 wav 仍会删除）。
- **修复建议**：Kill 失败时记录日志并二次尝试（或 `WaitForExit` 超时后告警）；`StopServer`/`CleanupAsync` 同步补 catch `Win32Exception`；Windows 无僵尸进程问题（`Process` 释放即回收句柄），但应保留失败可观测性。

#### 1-5（正面项）　无命令注入；参数传递规范

- **文件:行号**：`src/ClapBack.Stt/LocalSenseVoiceTranscriptionProvider.cs:125-140`、`src/ClapBack.Stt/MacOsFunAsrTranscriptionProvider.cs:131-141`、`src/ClapBack.Desktop/MacOsMicrophoneRecorder.cs:72-76`
- **证据**：三处均为 `UseShellExecute = false` + `ArgumentList.Add(...)` 逐参传递（如 `-m modelPath`、`--backend cpu|vulkan|cuda`），后端值经 `NormalizeBackend` 白名单（`LocalSenseVoiceTranscriptionProvider.cs:275-282`）；`tools/*.py` 全文无 `subprocess`/`popen`/`os.system`/`shell=True`。**不存在命令注入/参数注入**。

#### 1-6（正面项）　临时文件命名不可预测

- **文件:行号**：`LocalSenseVoiceTranscriptionProvider.cs:83-85`、`MacOsFunAsrTranscriptionProvider.cs:59`、`MacOsMicrophoneRecorder.cs:60`、`HuggingFaceModelDownloader.cs:56`
- **证据**：`$"clapback-{Guid.NewGuid():N}.wav"` / `destinationPath + $".{Guid.NewGuid():N}.download"`，GUID 命名无预创建符号链接/抢占风险；删除走 `TryDelete` 不抛异常。仅注意 `JsonSettingsStore.cs:54` 的 `settings.json.tmp` 为固定名（位于用户私有目录，风险低，见 7-2）。

---

### 专项 2：模型供应链完整性（重点）

#### 2-1　严重性：**高**　下载与缓存模型均无哈希校验，"非空文件"即视为有效

- **文件:行号**：`src/ClapBack.Stt/HuggingFaceModelDownloader.cs:48-51`、`:60-63`、`:137-140`
- **证据**：
  ```csharp
  // 缓存命中判断：存在且非空即返回，不校验内容
  if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
  {
      return new ModelDownloadResult(false, destinationPath);
  }
  ...
  // 下载完成后的唯一"完整性"检查
  if (new FileInfo(temporaryPath).Length == 0)
  {
      throw new InvalidDataException("模型源返回了空文件");
  }
  ```
  下载路径全程无 SHA-256/长度比对；`LocalModelCatalog.cs` 亦无任何校验值字段（见 2-2）。
- **攻击场景**：① 托管源（HF/ModelScope）账号或 CDN 被投毒、revision `main/master` 被推入恶意权重；② 本地低权限攻击者直接向 `models\sensevoice-small-q8.gguf` 写入恶意 GGUF——利用 llama.cpp/GGML 解析器漏洞可达成用户权限代码执行，或至少控制转写输出（再经 AutoSend 自动发送，见 4-1）。缓存判断只看非空，使投毒后的文件被永久视为"已下载完成"。
- **修复建议**：在 `LocalModelCatalog.cs` 为每个条目硬编码 SHA-256；下载后、`File.Move` 前强制比对，失败即删除临时文件并报错；缓存命中路径同样复核哈希。平台方 HF 的 `?download=true` 响应可另取 `X-Linked-Etag`/`sha256:` 作为交叉验证。

#### 2-2　严重性：**高**　SHA-256 期望值来自用户设置且默认为空，校验可被整体跳过（含缓存短路）

- **文件:行号**：`src/ClapBack.Core/Models/ClapBackSettings.cs:30`、`src/ClapBack.Stt/LocalSenseVoiceTranscriptionProvider.cs:211-231`、`:212-218`、`:233-237`；`src/ClapBack.Stt/LocalModelCatalog.cs:5-16`
- **证据**：
  ```csharp
  // ClapBackSettings.cs:30 —— 默认空字符串
  public string LocalModelSha256 { get; init; } = string.Empty;
  ```
  ```csharp
  // LocalSenseVoiceTranscriptionProvider.cs:221-231
  if (normalizedExpected.Length > 0)          // 空字符串 => 完全跳过 SHA-256
  {
      await using var stream = File.OpenRead(modelPath);
      var actual = Convert.ToHexString(
          await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
      if (!string.Equals(actual, normalizedExpected, StringComparison.Ordinal))
      {
          throw new InvalidDataException(
              $"本地语音模型 SHA-256 校验失败，应为 {normalizedExpected}，实际为 {actual}");
      }
  }
  ```
  ```csharp
  // :212-218 一次校验后按 (路径, 期望哈希) 缓存，永不复检
  if (string.Equals(_validatedModelPath, modelPath, StringComparison.OrdinalIgnoreCase)
      && string.Equals(_validatedModelSha256, normalizedExpected, StringComparison.Ordinal))
  {
      return;
  }
  ```
  `LocalModelArtifact`（`LocalModelCatalog.cs:5-16`）字段含 Variant/DisplayName/路径/仓库/revision/file/格式，**没有 Sha256 字段**——校验值不受版本控制、不受代码审查。
- **攻击场景**：三重绕过：① 保持默认空值即无校验；② 攻击者篡改 `settings.json` 把 `LocalModelSha256` 改成自己模型的哈希（自证循环）；③ 校验通过后替换文件，缓存（`:233-237`）使后续调用直接返回，不再复检（TOCTOU，校验在 `:77`，执行在 `:89`，中间与加载时均可掉包）。
- **修复建议**：把期望哈希移入 `LocalModelCatalog.cs` 硬编码（随代码评审/签名发布）；`LocalModelSha256` 仅可作为"覆盖并告警"的高级选项；空值时**拒绝执行**而非跳过；对模型文件句柄做独占打开（`FileShare.None`）跨过校验到子进程加载窗口，或校验后立即把内容交给运行时（内存加载）。

#### 2-3　严重性：**中**　ModelScope 回退源 + 浮动 revision（main/master）信任边界过宽，双源无一致性校验

- **文件:行号**：`src/ClapBack.Stt/HuggingFaceModelDownloader.cs:87-103`、`src/ClapBack.Stt/LocalModelCatalog.cs:30-34,41-47,53-58`
- **证据**：
  ```csharp
  usedModelScope = true;
  await DownloadAsync(
      BuildResolveUri("https://www.modelscope.cn/models", modelScopeRepository, modelScopeRevision, modelScopeFile),
      temporaryPath, cancellationToken).ConfigureAwait(false);
  ```
  两个源的文件**内容可以不同**，均无哈希比对；`HuggingFaceRevision = "main"`、`ModelScopeRevision = "master"` 为浮动分支。
- **攻击场景**：HF 不可达（网络故障/封锁）时自动回退 ModelScope——任一源的仓库被入侵、账号被接管或分支被强推恶意提交，即在用户无感知（`HuggingFaceModelDownloader` 无任何用户确认/来源提示）下落地恶意模型；浮动 revision 使"同一路径不同时间下载到不同内容"成为常态。
- **修复建议**：revision 固定为 commit SHA/不可变 tag；回退源下载后同样比对硬编码哈希（两源同哈希才接受）；在 UI/日志中明确标注 `UsedModelScope` 来源（`ModelDownloadResult` 已携带该信息，应透传到可见状态）。

#### 2-4　严重性：**低**　"临时文件 + 原子替换"整体安全；异常中断会残留孤儿 `.download` 文件

- **文件:行号**：`src/ClapBack.Stt/HuggingFaceModelDownloader.cs:56`、`:105`、`:108-112`
- **证据**：
  ```csharp
  var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.download";
  ...
  File.Move(temporaryPath, destinationPath, overwrite: true);   // 同卷 rename，原子替换
  ...
  finally { TryDelete(temporaryPath); _downloadGate.Release(); }
  ```
- **分析**：临时文件与目标同目录（同卷），`File.Move(overwrite:true)` 为原子替换，失败/取消/磁盘满不会破坏旧模型，`finally` 清理本次临时文件——**该机制本身安全**。唯一残留风险：进程被强杀/断电时孤儿 `*.download` 留在 `models/`，下次运行不清理（仅清理本次 GUID），长期积累占用磁盘并留下模型片段。
- **修复建议**：启动下载器时清理目录下过期的 `*.download` 文件；对临时文件写入后 `Flush(true)`/`FileStream.FlushToDisk` 再 rename，缩小断电窗口。

#### 2-5　严重性：**中**　下载目标路径由 settings 提供，绝对路径/`..` 可逃出应用目录

- **文件:行号**：`src/ClapBack.Stt/LocalSenseVoiceTranscriptionProvider.cs:262-270`、`src/ClapBack.Stt/HuggingFaceModelDownloader.cs:53-55,105`
- **证据**：
  ```csharp
  private static string ResolvePath(string path)
  {
      ...
      return Path.GetFullPath(path, AppContext.BaseDirectory);   // 绝对路径与 ".." 均不加限制
  }
  ```
  `EnsureFileAsync` 会对 `destinationPath` 的父目录 `Directory.CreateDirectory(directory)` 并以 `File.Move(..., overwrite: true)` 覆盖目标。
- **攻击场景**：篡改 `%AppData%\ClapBack\settings.json` 的 `LocalModelPath` 为 `..\..\..\重要文件` 或绝对路径 → 下载器覆盖写任意同卷文件（以应用权限）；`LocalSttRuntimePath`/`LocalVadModelPath` 同理可指向任意可执行/可读文件（配合 1-1 = 任意 exe 执行）。
- **修复建议**：对 `ResolvePath` 结果做 `AppContext.BaseDirectory`（或约定的 models 目录）前缀校验，拒绝逃逸路径；仅允许配置相对路径；下载目标强制收敛到 `models/` 目录下由 `LocalModelCatalog.DefaultLocalPath` 决定的文件名。

#### 2-6（正面项）　强制 HTTPS，仓库/文件名校验与转义到位，无 URL 注入

- **文件:行号**：`src/ClapBack.Stt/HuggingFaceModelDownloader.cs:72`、`:93`、`:143-158`
- **证据**：
  ```csharp
  BuildResolveUri("https://huggingface.co", ...)   // :72
  BuildResolveUri("https://www.modelscope.cn/models", ...)  // :93
  ...
  if (repositoryParts.Length != 2 || ... ) throw new ArgumentException("模型仓库必须是 owner/name，且必须配置 revision 和 file");
  var encodedRepository = string.Join('/', repositoryParts.Select(Uri.EscapeDataString));
  ```
  URL 前缀硬编码 https；repository 强制 `owner/name` 两段；各段 `Uri.EscapeDataString` 转义；`HttpClient` 默认证书校验。仓库参数来自内置目录（非网络输入），URL/路径注入不可行。

#### 2-7　严重性：**低**　文档与实现差距：PLAN.md 承诺的校验机制未实现

- **文件:行号**：`PLAN.md:61`、`PLAN.md:113`（对照 `LocalSenseVoiceTranscriptionProvider.cs:221`）
- **证据**：PLAN.md 称"模型文件首次使用前检查存在性、文件大小和 SHA-256 校验值……禁止静默执行不明来源的模型或运行时"；实际默认配置下 SHA-256 被跳过、模型与运行时静默自动下载/执行。
- **修复建议**：在实现补齐前修订文档表述；把 2-1/2-2/1-1 修复纳入同一里程碑。

---

### 专项 3：运行时与模型加载

#### 3-1　严重性：**高**　执行未校验的外部可执行文件

- 同 **1-1**（`LocalSenseVoiceTranscriptionProvider.cs:254-260` 仅 `File.Exists`；`:116` `FileName = runtimePath`）。补充：`runtime\llama-funasr-sensevoice.exe` 为用户态 .NET 应用直接 `Process.Start` 的目标，Windows Defender/SmartScreen 无法区分被替换的二进制；`ResolvePath`（2-5）进一步允许 settings 指定任意绝对路径的 exe。**修复见 1-1 + 2-5**。

#### 3-2　严重性：**高**　SHA-256 校验存在完整跳过分支 + 校验缓存 + 校验/加载 TOCTOU

- 同 **2-2**。补充证据——校验与使用之间的掉包窗口：
  ```csharp
  // LocalSenseVoiceTranscriptionProvider.cs:77  → 校验
  await ValidateModelAsync(modelPath, settings.LocalModelSha256, cancellationToken);
  ...
  // :89 → 交给外部进程按路径加载（此时文件内容可已被替换）
  var output = await RunRuntimeAsync(runtimePath, modelPath, vadPath, temporaryAudioPath, settings, cancellationToken);
  ```
  模型以**文件路径**形式传给 `-m`（`:125-126`），校验对象与加载对象是同一路径但不是同一内容快照。

#### 3-3　严重性：**低**　非托管代码加载面

- **文件:行号**：`src/ClapBack.Windows/MainWindow.xaml.cs:65-69`、`src/ClapBack.Windows/Infrastructure/OverwatchTextSender.cs:121-125`、`src/ClapBack.Windows/Infrastructure/WindowsCredentialStore.cs:97-107`
- **分析**：托管侧仅有静态 P/Invoke（user32 / advapi32），全仓库无 `Assembly.Load`/`LoadLibrary` 动态加载调用；非托管库主要来自 NAudio（winmm 等）、llama-funasr 子进程（其自带 DLL、Vulkan/CUDA 后端）。风险点回到 1-1/3-1：被替换的 exe 可加载任意非托管库。**建议**：对发布包内全部 DLL/exe 提供哈希清单并在安装时校验。

---

### 专项 4：输入模拟与剪贴板

#### 4-1　严重性：**高**　SendInput 不校验目标窗口，误触发即向任意窗口注入"粘贴 + Enter"

- **文件:行号**：`src/ClapBack.Windows/Infrastructure/OverwatchTextSender.cs:23`、`:33-36`、`:43-47`；`src/ClapBack.Core/Controller/VoiceInputController.cs:67-70,141-154`；`src/ClapBack.Core/Models/ClapBackSettings.cs:56`
- **证据**：
  ```csharp
  public void CaptureCurrentTarget() => _targetWindow = GetForegroundWindow();   // 不校验是不是守望先锋
  ...
  if (_targetWindow != IntPtr.Zero) { SetForegroundWindow(_targetWindow); }      // 返回值不检查
  ...
  KeyboardInput.Press(_chatOpenKey());          // 默认 Enter（打开聊天框）
  await Task.Delay(80, cancellationToken);
  KeyboardInput.Press(0x11, 0x56);              // Ctrl+V
  KeyboardInput.Press(0x0D);                    // Enter 发送
  ```
  ```csharp
  // VoiceInputController.cs:141-154 录音超时自动走完整"停止→识别→发送"链路
  await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
  await HandleHotkeyAsync(CancellationToken.None);
  ```
  `AutoSend` 默认 `true`（`ClapBackSettings.cs:56`）。
- **攻击场景**：① 热键为无修饰键 CapsLock（`MainWindow.xaml.cs:31` `RegisterHotKey(_handle, HotkeyId, 0, _hotkey)`），游戏中/打字时极易误触；若当时前台不是游戏（终端、聊天窗口、浏览器地址栏、密码框），识别文本被 Ctrl+V 粘贴并**按 Enter 提交**——终端里等于执行命令；② 录音开始时抓的窗口句柄在数十秒识别后可能已不是用户所想的窗口，`SetForegroundWindow` 还会强行抢焦点；③ `SetForegroundWindow` 失败（被系统拒绝）时按键直接进入**当时**的前台窗口。
- **修复建议**：发送前校验目标窗口身份（进程名/窗口类名白名单，如 `Overwatch.exe`、`UnityWndClass`），不匹配则取消发送并提示；`SetForegroundWindow` 返回 false 时中止；AutoSend 增加"目标窗口已变化则取消"保护；考虑给热键强制带修饰键（Ctrl/Alt）。

#### 4-2　严重性：**中**　剪贴板快照仅保存文本，非文本内容被永久破坏

- **文件:行号**：`src/ClapBack.Windows/Infrastructure/WindowsClipboardService.cs:7`、`:14`、`:20`、`:23-34`
- **证据**：
  ```csharp
  public sealed record ClipboardSnapshot(string? Text);
  ...
  return RunOnUiThread(() => new ClipboardSnapshot(Clipboard.ContainsText() ? Clipboard.GetText() : null));
  ...
  if (snapshot.Text is null) { return RunOnUiThread(() => Clipboard.Clear()); }   // 原剪贴板是图片/文件 => 直接清空
  else { return RunOnUiThread(() => Clipboard.SetText(snapshot.Text)); }
  ```
- **攻击场景**：用户复制了文件、图片、Excel 区域等非文本内容 → `Clipboard.SetText(text)`（`:20`）整板覆盖 → 恢复阶段要么 `Clear()`（永久丢失）要么写回旧文本。这是**用户数据丢失**，且 `ClipboardSnapshot` 把用户剪贴板明文留在内存（托管堆）中，退出前可被转储读取。
- **修复建议**：用 `Clipboard.GetDataObject`/`SetDataObject` 保存并恢复**完整格式集合**；恢复前比对当前剪贴板序列号（`GetClipboardSequenceNumber`），仅当未被第三方修改时才恢复；敏感文本快照用后置零。

#### 4-3　严重性：**中**　剪贴板恢复存在 TOCTOU 竞态，用户新复制内容会被旧快照覆盖

- **文件:行号**：`src/ClapBack.Windows/Infrastructure/OverwatchTextSender.cs:38-56`
- **证据**：
  ```csharp
  var snapshot = await _clipboard.CaptureAsync(cancellationToken);   // :39 抓快照
  try {
      await _clipboard.SetTextAsync(text, cancellationToken);        // :42 覆盖
      ... // :43-47 三次延时共约 280ms 的键入序列
  } finally {
      if (_restoreClipboard()) {
          await Task.Delay(150, CancellationToken.None);
          await _clipboard.RestoreAsync(snapshot, CancellationToken.None);  // :53-55 无条件写回旧快照
      }
  }
  ```
- **攻击场景**：从 `CaptureAsync` 到 `RestoreAsync`（约 400ms+）之间，用户（或剪贴板管理器/密码管理器自动复制）写入的新内容会被**静默覆盖**为旧快照；密码管理器"复制后自动清剪贴板"的时序也会被打乱，可能把本应清除的敏感文本留在板上（与设计意图相反）。
- **修复建议**：恢复前检查 `GetClipboardSequenceNumber` 与快照时序号，序列号已变化（除自己 `SetText` 造成的那次）则放弃恢复；缩小键入延时窗口；提供"恢复剪贴板"可选项（已存在 `RestoreClipboard`，保持默认即可）。

#### 4-4　严重性：**低**　热键解析失败静默回落到 Enter；裸键注册放大误触发

- **文件:行号**：`src/ClapBack.Windows/App.xaml.cs:117-134`、`src/ClapBack.Windows/MainWindow.xaml.cs:31`
- **证据**：
  ```csharp
  return key.StartsWith("F", ...) && ushort.TryParse(key[1..], out var functionNumber) && functionNumber is >= 1 and <= 24
      ? (ushort)(0x70 + functionNumber - 1)
      : (ushort)0x0D;      // 任何无法识别的键名 => Enter 全局热键
  ```
- **攻击场景**：用户在设置里输入错误键名（如 "Ctrl+Shift+K"）后，全局热键变成 **Enter**——每次在任何应用按 Enter 都会触发录音/发送状态机，与 4-1 叠加后误发概率急剧上升。
- **修复建议**：无法解析的键名应报错拒绝注册，而不是回落；热键注册强制要求修饰键。

---

### 专项 5：密钥与配置

#### 5-1　严重性：**低**　Credential Manager 调用基本正确，但删除不查返回值、密钥内存不置零

- **文件:行号**：`src/ClapBack.Windows/Infrastructure/WindowsCredentialStore.cs:15-18`、`:28-30`、`:57-60`、`:76`、`:10`
- **证据**：
  ```csharp
  if (!CredWrite(ref credential, 0))
      throw new InvalidOperationException($"无法保存 Windows 凭据，错误码：{Marshal.GetLastWin32Error()}");  // :57-60 检查返回值，异常只含错误码
  ...
  CredDelete(name, Generic, 0);   // :76 返回值被忽略
  ...
  private const uint PersistLocalMachine = 2;   // :10 CRED_PERSIST_LOCAL_MACHINE（跨登录会话持久）
  ```
- **分析**：`CredWrite` 正确检查返回值且异常消息不含密钥本体（好）；`CredRead` 失败统一返回 `null`，未区分 `ERROR_NOT_FOUND` 与真实错误（静默失败，云模式会表现为"未配置"）；`CredDelete` 失败无感知；blob 用 `Marshal.AllocCoTaskMem`/`FreeCoTaskMem` 释放但**不置零**，UTF8 解码后的密钥字符串在托管堆上存活至 GC。
- **攻击场景**：删除失败导致"以为已清除"的 API Key 残留凭据库；内存转储可读出密钥（与 1-3 组合可被恶意子进程取走）。
- **修复建议**：检查 `CredDelete` 返回值并提示；`CredRead` 区分 not-found 与错误；写入后 `CryptographicOperations.ZeroMemory(bytes)`，优先使用 `CRED_PERSIST_SESSION` 或解释为何需要 LOCAL_MACHINE；确认凭据库 ACL（同一用户可见，符合预期）。

#### 5-2　严重性：**中**　云端 BaseURL 未强制 HTTPS，Bearer 密钥与录音可被诱导明文外发

- **文件:行号**：`src/ClapBack.Stt/WhisperCompatibleTranscriptionProvider.cs:123-131`、`:54`；`src/ClapBack.Core/Models/ClapBackSettings.cs:53`
- **证据**：
  ```csharp
  if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/audio/transcriptions", UriKind.Absolute, out var endpoint))
      throw new InvalidOperationException("语音识别服务 URL 无效");
  ...
  request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);   // :54
  ```
  `UriKind.Absolute` 接受 `http://`、`ftp://` 等任意 scheme（仅默认 `SttBaseUrl = "https://api.openai.com/v1"` 是 https）。
- **攻击场景**：篡改 `settings.json`（或社会工程用户填入 `http://...`）→ API Key 与**录音内容**明文发往攻击者服务器；即使用户主动配置自建服务，http 也无任何告警。
- **修复建议**：`BuildEndpoint` 校验 `endpoint.Scheme == Uri.UriSchemeHttps`（可允许 `http://127.0.0.1/localhost` 白名单用于本地网关）；拒绝其余 scheme。

#### 5-3　严重性：**低**　`CLAPBACK_STT_API_KEY` 环境变量回退行为明确失败、不静默降级（正面，附注意事项）

- **文件:行号**：`src/ClapBack.Desktop/App.axaml.cs:33`、`src/ClapBack.Stt/WhisperCompatibleTranscriptionProvider.cs:42-46`
- **证据**：
  ```csharp
  _ => Task.FromResult(Environment.GetEnvironmentVariable("CLAPBACK_STT_API_KEY")));
  ...
  if (string.IsNullOrWhiteSpace(apiKey))
      throw new InvalidOperationException("尚未配置语音识别 API Key");
  ```
- **分析**：未配置时显式报错（好），不会静默发送空密钥；Windows 主路径用 Credential Manager（好）。注意事项：环境变量对同用户所有进程可见，且会被未校验子进程继承（1-3）；文档应提示优先使用凭据库。
- **修复建议**：环境变量仅保留为开发回退并在 UI 提示来源；结合 1-3 清理子进程环境。

#### 5-4（正面项）　密钥不硬编码、不进日志、不进异常消息

- **文件:行号**：`src/ClapBack.Windows/SettingsWindow.xaml.cs:44,103-110`、`src/ClapBack.Stt/WhisperCompatibleTranscriptionProvider.cs:80,133-137`
- **证据**：API Key 通过 `PasswordBox` 读写凭据库（`_credentialStore.GetAsync("ClapBack.Stt.ApiKey")`），仓库内无硬编码密钥（全仓库 grep 无 key/secret 字面量）；错误消息只含 `Sanitize(responseBody)`（截断 300 字符）与状态码，不回显请求头。

---

### 专项 6：依赖与构建

#### 6-1　严重性：**低**　第三方包版本清单与漏洞核查结果

- **文件:行号**：`src/ClapBack.Windows/ClapBack.Windows.csproj`（NAudio 2.2.1）、`src/ClapBack.Desktop/ClapBack.Desktop.csproj`（Avalonia 11.3.9 / Avalonia.Desktop 11.3.9 / Avalonia.Themes.Fluent 11.3.9）、`tests/ClapBack.Tests/ClapBack.Tests.csproj`（Microsoft.NET.Test.Sdk 17.11.1、xunit 2.9.2、xunit.runner.visualstudio 2.8.2）
- **核查结论**：
  - NuGet 官方漏洞数据（`vulnerability.base.json`，数据更新时间 2023-12-13）中 `naudio`、`avalonia*`、`xunit*`、`microsoft.net.test.sdk` **均无漏洞条目**；
  - GitHub Advisory Database 按 `ecosystem:nuget` 检索 Avalonia、NAudio 均返回 0 条 advisory；
  - 审核环境网络受限，`dotnet list package --vulnerable --include-transitive` 无法连接 nuget.org 复核（报 `NU1900: 获取包漏洞数据时出错`），**结论以公开漏洞库检索为准**。
- **修复建议**：在 CI 中固定执行 `dotnet list package --vulnerable`；启用 Dependabot/Renovate；NAudio 2.2.1 已落后主线（3.x），评估升级。

#### 6-2　严重性：**低**　Directory.Build.props 无供应链加固项

- **文件:行号**：`Directory.Build.props:1-7`
- **证据**：仅 `LangVersion=latest`、`AnalysisLevel=latest`、`InvariantGlobalization=true`；各 csproj 有 `TreatWarningsAsErrors=true`、`Nullable=enable`（正面）。
- **缺失**：NuGet lock file（`packages.lock.json` + `RestoreLockedMode`）、中央包管理（CPM）、`Deterministic=true`、签名（`SignAssembly`/`PublicSign`）、二进制日志审计。
- **修复建议**：启用 lockfile + `RestoreLockedMode`，CI 校验 lock 未漂移；考虑 SourceLink 与包签名。

#### 6-3　严重性：**中**　发布产物（PublishSingleFile 自包含单文件）无签名、无哈希清单

- **文件:行号**：`README.md:69`（`-p:PublishSingleFile=true -p:PublishReadyToRun=true`）、`scripts/build-macos-app.sh:19`（macOS 侧 `PublishSingleFile=false`）
- **证据**：全仓库无 signtool/Authenticode/公证流程；无发布哈希清单文件。
- **攻击场景**：分发链（Release 附件、镜像站、网盘）被替换为捆绑后门的 ClapBack.exe 时，用户无法甄别；SmartScreen 拦截后用户习惯性"仍要运行"，与 1-1/3-1 的运行时投毒形成完整链。
- **修复建议**：对主 exe 与 `runtime\llama-funasr-sensevoice.exe` 做代码签名（Windows Authenticode / macOS notarization）；发布时附 SHA-256 清单并在首次运行/安装时自校验。

---

### 专项 7：路径与文件处理

#### 7-1　严重性：**中**　用户可控路径逃逸应用目录（汇总面）

- **文件:行号**：`src/ClapBack.Stt/LocalSenseVoiceTranscriptionProvider.cs:262-270`（`ResolvePath`）、`:223`（`File.OpenRead(modelPath)`）、`src/ClapBack.Stt/HuggingFaceModelDownloader.cs:55,105`（建目录 + 覆盖写）
- **全仓库 `Path.Combine` / `File.*` 目标清单**（grep 全量核对）：
  | 位置 | 目标 | 来源 |
  |---|---|---|
  | `HuggingFaceModelDownloader.cs:55,105,166` | 模型目标/临时文件 | settings（可逃逸） |
  | `LocalSenseVoiceTranscriptionProvider.cs:83-85,223,338` | 临时 wav / 模型读取 / 删除 | GUID temp、settings |
  | `MacOsFunAsrTranscriptionProvider.cs:59,213,265` | 临时 wav / 解析候选 | GUID temp、解析链 |
  | `MacOsMicrophoneRecorder.cs:60,124,219,230` | 临时 wav / 解析候选 | 同上 |
  | `DesktopSettingsStore.cs:14-17,24,38` | `%AppData%\ClapBack\desktop-settings.json` | 固定 |
  | `JsonSettingsStore.cs:21-24,35,53-60` | `%AppData%\ClapBack\settings.json(.tmp)` | 固定 |
  | `tests\...\HuggingFaceModelDownloaderTests.cs:13,27,52,71` | 测试临时目录（GUID） | 测试 |
  | `ClapBack.MacExperiment/Program.cs:12` | `File.OpenRead(args[0])` | 命令行（本地 CLI） |
- **攻击场景**：网络输入**不会**直接决定路径（下载响应只写入预先决定的目标，正面）；但本地 settings 篡改可造成：任意文件读（哈希计算 `File.OpenRead`）、任意文件覆盖（`File.Move(overwrite:true)`）、任意 exe 执行（`FileName=runtimePath`）。这是本地恶意软件的提权/持久化辅助面（当前为同用户权限，非跨用户提权）。
- **修复建议**：同 2-5：路径规范化后强制前缀白名单；拒绝绝对路径与 `..` 逃逸；设置文件用 DPAPI 加密敏感字段并设 ACL 仅本人可写。

#### 7-2　严重性：**低**　settings 写入使用固定临时名，%AppData% 目录无 ACL 加固

- **文件:行号**：`src/ClapBack.Windows/Infrastructure/JsonSettingsStore.cs:54-60`、`src/ClapBack.Desktop/DesktopSettingsStore.cs:35-38`
- **证据**：
  ```csharp
  var temporaryPath = _path + ".tmp";        // 固定名（非 GUID）
  await using (var stream = File.Create(temporaryPath)) { ... }
  File.Move(temporaryPath, _path, true);     // 原子替换（好）
  ```
- **分析**：`File.Move` 原子替换是正确做法；固定 `.tmp` 名位于用户私有 `%AppData%`，同用户进程可预占/篡改（无跨用户风险）；`DesktopSettingsStore.Save` 用 `File.WriteAllText` 非原子（崩溃可致设置损坏，功能问题）。目录未做 `DirectorySecurity` 收紧。
- **修复建议**：临时名加 GUID；写入后 `Flush(true)`；可选收紧目录 ACL。

---

### 专项 8：Python sidecar

#### 8-1　严重性：**低**　`macos_funasr_record.py`：参数校验充分，无 subprocess

- **文件:行号**：`tools/macos_funasr_record.py:54-56`、`:105-110`
- **证据**：
  ```python
  if args.max_seconds <= 0 or args.sample_rate <= 0 or args.channels <= 0:
      print("录音参数无效", file=sys.stderr)
      return 2
  ...
  args.output.parent.mkdir(parents=True, exist_ok=True)
  with wave.open(str(args.output), "wb") as wav:
  ```
- **分析**：argparse 严格定义参数；输出路径由调用方（.NET 侧 GUID 临时名）给出；`mkdir(parents=True)` 理论上可按调用方指定创建任意目录（调用方=同用户进程，无提权）；设备选择黑名单（`:37` `excluded = ("steam", "streaming", "blackhole", "loopback", "aggregate")`）主动规避虚拟声卡劫持（好）。无任何子进程调用。
- **修复建议**：可对 `--output` 做临时目录前缀校验（纵深防御）。

#### 8-2　严重性：**中**　`macos_funasr_server.py`：`--model` 可指向任意 ModelScope 仓库，无任何校验；音频路径信任 stdin

- **文件:行号**：`tools/macos_funasr_server.py:38`、`:59`、`:67-77`
- **证据**：
  ```python
  parser.add_argument("--model", default="iic/SenseVoiceSmall")
  ...
  model = AutoModel(model=args.model, hub="ms", device=device, disable_update=True)
  ...
  request = json.loads(line)
  audio = Path(str(request["audio"]))
  result = model.generate(input=str(audio), cache={}, language=language, ...)
  ```
- **攻击场景**：`--model` 来自 `settings.MacOsFunAsrModel`（`MacOsFunAsrTranscriptionProvider.cs:133` 经 ArgumentList 传入，无注入但值可控）→ 经 ModelScope 自动下载任意仓库权重到本地缓存（与 2-1/2-3 同类供应链问题，且 FunASR 缓存同样无哈希校验）；`request["audio"]` 路径完全信任 stdin 发送方（信任边界=本地管道，同用户父进程，合理），但配合 1-2 的脚本劫持可被伪造。模型加载还可在被投毒的 `funasr==1.4.3` 包里执行任意代码（pip 依赖面）。
- **修复建议**：对 `--model` 做白名单（仅 `iic/SenseVoiceSmall` 等内置 ID）；校验 ModelScope 下载缓存的哈希（ModelScope Hub 提供 revision/etag）；`tools/requirements-macos-funasr.txt` 已固定大部分版本（注意 `sounddevice>=0.5.1` 为浮动下界，见 8-5）。

#### 8-3　严重性：**低**　`macos_funasr_transcribe.py`：存在性校验到位，`--model` 问题同 8-2

- **文件:行号**：`tools/macos_funasr_transcribe.py:63-65`、`:78-90`
- **证据**：
  ```python
  if not args.audio.is_file():
      print(f"音频文件不存在：{args.audio}", file=sys.stderr)
      return 2
  ```
- **分析**：独立 CLI 版单次转写脚本，参数校验、`--device` 限定 `auto/cpu/mps` 选择集；`--model` 同样直通 `AutoModel(model=args.model, hub="ms")`。无 subprocess。

#### 8-4　严重性：**低**　JSON sidecar 协议解析健壮，但"伪造转写→自动发送"存在链式风险

- **文件:行号**：`src/ClapBack.Stt/MacOsFunAsrTranscriptionProvider.cs:223-258`、`:82-88`；`tools/macos_funasr_server.py:13-19`
- **证据**：
  ```csharp
  var marker = line.IndexOf(ProtocolPrefix, StringComparison.Ordinal);
  if (marker < 0) { continue; }                       // 忽略 tqdm/诊断噪声
  ...
  using var document = JsonDocument.Parse(json);      // 解析失败继续等待完整消息
  ```
  ```python
  PROTOCOL_PREFIX = "CLAPBACK_JSON:"
  print(PROTOCOL_PREFIX + json.dumps(payload, ensure_ascii=False), flush=True)
  ```
- **分析**：协议以 `CLAPBACK_JSON:` 前缀 + JSON 行通信，`ReadProtocolLineAsync` 对噪声行、部分行做了容错（好）；信任边界是本地 stdin/stdout 管道，与父进程同权限，设计合理。残余风险：sidecar 被 1-2 劫持或被投毒时，可返回**任意转写文本**，叠加 4-1 的 AutoSend 即可把攻击者文本自动打进游戏/聊天窗口（链式：脚本劫持→伪造转写→自动发送）。
- **修复建议**：修复 1-2/4-1 即断链；可选在发送前对转写结果做 UI 确认（尤其当目标窗口校验失败时）。

#### 8-5　严重性：**低**　pip 依赖存在浮动版本

- **文件:行号**：`tools/requirements-macos-funasr.txt:7`（`sounddevice>=0.5.1`）
- **证据**：`funasr==1.4.3`、`modelscope==1.39.1`、`torch==2.9.1`、`torchaudio==2.9.1`、`soundfile==0.14.0` 均已固定，唯 `sounddevice>=0.5.1` 为下界浮动。
- **修复建议**：固定确切版本并配合 hash-pinning（`pip install --require-hashes`）。

---

## 【优先修复清单】（按严重性排序）

| # | 严重性 | 发现 | 位置 | 一句话动作 |
|---|---|---|---|---|
| 1 | 高 | 2-1 / 2-2 | `HuggingFaceModelDownloader.cs:48-51,137-140`；`LocalModelCatalog.cs:5-16`；`LocalSenseVoiceTranscriptionProvider.cs:221-231` | 在 `LocalModelCatalog` 硬编码每个模型的 SHA-256，下载后/缓存命中一律强制比对，空哈希拒绝执行 |
| 2 | 高 | 1-1 / 3-1 | `LocalSenseVoiceTranscriptionProvider.cs:254-260` | 校验 runtime exe 哈希/签名后再执行；runtime 目录收紧 ACL |
| 3 | 高 | 4-1 | `OverwatchTextSender.cs:23,33-36,43-47` | 发送前校验目标窗口进程/类名白名单，SetForegroundWindow 失败或窗口已变即中止 |
| 4 | 中 | 2-3 | `HuggingFaceModelDownloader.cs:87-103`；`LocalModelCatalog.cs:30-58` | revision 固定为 commit SHA；HF/ModelScope 双源同哈希才接受；来源透传到 UI |
| 5 | 中 | 2-5 / 7-1 | `LocalSenseVoiceTranscriptionProvider.cs:262-270` | 路径规范化 + 前缀白名单，拒绝绝对路径/`..` 逃逸 |
| 6 | 中 | 5-2 | `WhisperCompatibleTranscriptionProvider.cs:123-131` | 强制 https（本地回环除外） |
| 7 | 中 | 4-2 / 4-3 | `WindowsClipboardService.cs:7,14,23-34`；`OverwatchTextSender.cs:38-56` | 完整格式快照/恢复 + 剪贴板序列号 TOCTOU 防护 |
| 8 | 中 | 1-2 | `MacOsFunAsrTranscriptionProvider.cs:202-218`；`MacOsMicrophoneRecorder.cs:204-224` | 移除父目录遍历与 CWD 优先解析 |
| 9 | 中 | 6-3 | `README.md:69` | 发布产物代码签名 + SHA-256 清单 |
| 10 | 中 | 8-2 | `tools/macos_funasr_server.py:38,59` | `--model` 白名单 + 模型缓存哈希校验 |
| 11 | 低 | 1-3 / 1-4 | `LocalSenseVoiceTranscriptionProvider.cs:145-146,309-330` | 子进程环境变量白名单；Kill 失败可观测 |
| 12 | 低 | 4-4 | `App.xaml.cs:117-134` | 热键解析失败拒绝注册，强制修饰键 |
| 13 | 低 | 5-1 | `WindowsCredentialStore.cs:76,28-30` | 检查 CredDelete 返回值；密钥内存置零 |
| 14 | 低 | 2-4 / 7-2 / 8-5 / 6-2 | 各处 | 孤儿 .download 清理、GUID 临时名、依赖固定版本 + lockfile、CI 漏洞扫描常态化 |

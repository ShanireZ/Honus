# Honus.Agent — 采集端骨架（C#/.NET）

考试机上的信号采集端。采 OS 元数据信号 + 事件触发/随机基线截图，盖哈希链后经 WebSocket（事件）/ HTTP（图片）上报服务器。**这是 M1 骨架**：核心信号采集可跑，传输的接收循环/重连/续传、签名校验、L2/L3 分析留有 `TODO`。

## 运行环境
- .NET 8 SDK，目标 `net8.0-windows`（Windows-only：UIAutomation / WMI / System.Drawing 抓屏）。
- **需管理员权限**运行：WMI 取 `CommandLine`、ETW、全局剪贴板监听都依赖提权。

## 构建 / 运行
```powershell
cd D:\WorkSpace\honus\agent
dotnet restore
dotnet build -c Release

# 单文件自包含发布(分发到考试机)
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# 运行(传配置路径,缺省 agent.config.json)
cp agent.config.sample.json agent.config.json   # 改好里面的 server/psk/白名单
.\bin\Release\net8.0-windows\win-x64\publish\Honus.Agent.exe agent.config.json
```

`psk` 生成（base64 的 32 字节）：
```powershell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
```

## 结构
```
Model/AgentEvent.cs        事件/RawSignal 模型 + SignalType
Model/Json.cs              全局 JSON 约定(camelCase + snake_case 枚举)
Config/AgentConfig.cs      配置(从 json 加载)
Signals/ISignalSource.cs   信号源接口
Signals/ForegroundWindowSource.cs  前台窗口标题+进程(轮询)
Signals/BrowserUrlSource.cs        浏览器地址栏 URL(UIAutomation)★第一防线
Signals/ProcessWatcher.cs          进程启停(WMI)
Signals/ClipboardWatcher.cs        大段粘贴(剪贴板,自带 STA 线程)
Signals/UsbWatcher.cs              USB 到达(WMI)
Capture/ScreenshotCapturer.cs      抓屏→缩放→WebP→pHash→上传
Capture/PerceptualHash.cs          dHash + 汉明距离
Integrity/HashChain.cs             哈希链 + HMAC 签名
Transport/Envelope.cs              事件信封序列化
Transport/UplinkClient.cs          WebSocket 事件 + HTTP 图片
Buffer/LocalBuffer.cs              断网缓冲(文件落地)
Program.cs                         装配 + 管线 + 基线/心跳循环
```

## 设计要点（与 architecture / api-contract 对齐）
- **URL 监控是第一防线**：判题站走白名单，浏览器出现任何非白名单 URL → risk 80 + 抓图。读不到 URL（隐身/冷门浏览器）→ 降级 risk 40 + 强制抓图。
- **随机基线截图不去重**：专为抓 IDE AI 插件（补全不触发进程/网络事件，只能靠定期看屏）。触发型截图才做 pHash 去重（汉明 ≤3）。
- **隐私**：剪贴板只记长度/行数，**不上传明文**。截图原图发服务器（局域网内），云 OCR 由服务器侧按收口策略处理，Agent 不直连云。
- **防篡改**：每条事件入哈希链 + HMAC 签名；`Handle` 用锁串行化盖章，保证链序与 seq 一致。

## 已知 TODO（M1 之后）
- UplinkClient：握手鉴权、接收循环（ack/config_update/capture_now）、断线重连 + `LocalBuffer.ReplayAsync` 续传、图片 `X-Honus-Sig`。
- 多显示器抓图（当前只抓主屏——见威胁矩阵"第二显示器"盲区）。
- 切窗爆发源（alt_tab_burst）、ETW 替代 WMI 降延迟。
- ClipboardWatcher 干净关停消息泵。
- Agent 防卸载 / 心跳掉线告警（服务器侧）。

## 构建排错
- 若 `System.Windows.Automation` 找不到：确认 `<UseWPF>true</UseWPF>`，必要时保留 `UIAutomationClient`/`UIAutomationTypes` 的 `<Reference>`。
- 若剪贴板无回调：确认以**桌面会话**（非服务 Session 0）运行；`ClipboardWatcher` 的 STA 线程消息泵需桌面。

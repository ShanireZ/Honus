# AGENTS.md — Honus

> 本文件遵循并指回工作区根准则 [`../AGENTS.md`](../AGENTS.md)。先读根准则，再读本文件。

## 项目一句话
**Honus** — 本地局域网**考试监考系统**，防止学员在编程 / OJ 考试中用 AI 做题或联网搜题。学员**本地 IDE 写 C++ + 网页判题**提交；服务器为局域网内 1+ 台笔记本。架构 = **纯检测 + 取证**（已决定不做网络/主机预防层），**元数据优先、图像为辅**，系统只初筛、人工裁决。权威设计见 [docs/architecture-v0.2.md](docs/architecture-v0.2.md)。

## 语言
所有文档、注释、提交信息一律用中文。

## 组件与技术栈
- **共享契约** [contracts/](contracts/)（`Honus.Contracts`，net8.0）：线协议 / canonical / HMAC / 枚举 / 事件模型。Agent 与 Server **共用同一实现**，保证哈希链与签名两端逐字节一致。
- **采集核心** [agentcore/](agentcore/)（`Honus.Agent.Core`，net8.0）：平台无关的传输（WS/HTTP + 握手/hello/ack + **断线重连指数退避** + **续传**）、断网缓冲、配置、哈希链封装。刻意非 -windows，便于被测试直接引用。
- **采集端 Agent**（考试机，每台一个）：C#/.NET 单文件 exe，需管理员权限（ETW / UIAutomation / WMI）。Windows 专属部分（抓屏 / 信号源）。代码 [agent/](agent/)（`Honus.Agent`，net8.0-windows，引用 Core）。
- **监考服务器**（笔记本）：接收 + 分析 + 落库 + Web 看板。**.NET 8 / ASP.NET Core**（minimal API + WebSocket）+ **Microsoft.Data.Sqlite** + 文件系统（+ M3 起 sqlite-vec）。代码 [server/](server/)（`Honus.Server`，net8.0）。**仅服务器对外联网，且只为云 OCR**。
- **监考端 / 复核台**：实时看板 + 可疑队列复核。纯原生单页看板在 [server/wwwroot/](server/wwwroot/)。
- **测试**：[tests/](tests/)（`Honus.Server.Tests`，xUnit）——端到端覆盖 WS 握手/验签/幂等、图片去重、击键、人工裁决、canonical 黄金格式。

## 设计铁律（任何改动都必须守）
1. **预防层为零，检测必须扎实** — 控不了考场网络、也不做主机防火墙，联网搜题 / 网页 AI 只能靠 URL / 进程 / 截图**检测取证**（事后），不可阻断。**浏览器 URL 监控是第一防线**。
2. **唯一出网 = 云 OCR** — 除 L2 OCR 外，所有数据（元数据 / 原图 / 向量 / 看板）不出局域网。上云的图必须**最小化上传 + 裁剪 + 打码身份**，**原图永不出网**。见 architecture §5。
3. **系统只初筛、人工裁决** — 任何风险分 / 命中只是线索，处分由人判。
4. **元数据优先** — 能用 OS 信号判的不拍图；图只给可疑时刻 + 随机基线（专抓 IDE 插件）留证。
5. **诚实标注盲区** — 手机 / 第二设备 / Agent 未覆盖的多屏是结构盲区，靠物理监考兜，不假装覆盖。

## 关键决策（已锁定，见 architecture §0）
网页判题 + 本地 IDE · 无网络预防层 · 无主机防火墙 · C#/.NET Agent · 服务器集中 + 外部云 OCR · 1080p WebP q75 随机 30–90s · SQLite + 文件 + sqlite-vec · 留存 30 天后关键数据转 archive。

## 留存与归档
热数据（SQLite live + 文件）保留 **30 天**；30 天后**关键数据**（可疑/已判事件 + 其证据图 + OCR/Logo 结果 + 裁决记录 + 考试元数据 + 哈希锚）转入 archive DB，其余（干净基线图 / 低危例行事件 / 心跳）清理。详见 architecture §13、[schema/schema-archive.sql](schema/schema-archive.sql)。

## 目录
- [docs/architecture-v0.2.md](docs/architecture-v0.2.md) — 总体架构（**权威设计**）
- [docs/api-contract-m1.md](docs/api-contract-m1.md) — M1 接口契约（Agent↔Server 协议 + 数据模型）
- [schema/schema.sql](schema/schema.sql) — SQLite **live** DB DDL
- [schema/schema-archive.sql](schema/schema-archive.sql) — SQLite **archive** DB DDL
- [contracts/](contracts/) — 共享线协议库（`Honus.Contracts`）
- [agentcore/](agentcore/) — 平台无关采集核心（`Honus.Agent.Core`：传输/续传/重连/缓冲）
- [agent/](agent/) — Windows 采集端（`Honus.Agent`：抓屏/信号源）
- [server/](server/) — 监考服务器 + 看板（`Honus.Server`，见 [server/README.md](server/README.md)）
- [tests/](tests/) — 端到端测试（`Honus.Server.Tests`）
- [Honus.sln](Honus.sln) — 解决方案（4 个项目）

## 构建 / 测试（需 .NET 8 SDK，无需 VS）
```
dotnet build Honus.sln -c Debug      # 全量编译(Agent 走 net8.0-windows)
dotnet test  Honus.sln -c Debug      # 运行端到端测试
```

## 状态
**M1 最小闭环已实现并通过端到端验证**：
- ✅ `Honus.Contracts` + `Honus.Agent.Core` + `Honus.Agent`（编译通过·0 警告）+ `Honus.Server`（WS/HTTP ingest + 落库 + 看板）。
- ✅ **Agent 端可靠性完成**：握手鉴权、hello/hello_ack、ack、**断线重连（指数退避）**、**断网缓冲 + 续传**（`UplinkClient` + `LocalBuffer`，服务器幂等去重），图片 HMAC 签名 + 补传。
- ✅ **config_update 热更新**：服务器 `POST /api/exams/{examId}/config` → `AgentHub` 推送给在线 Agent（新连/重连在 hello 时补推）→ Agent `LiveConfig` 原子应用（白名单/阈值/截图参数，下一轮采集即生效）。
- ✅ **证据图跨重连关联**：触发型抓图**客户端预生成 imageId**（`X-Honus-Image-Id`，服务器沿用、跳过 pHash 去重、幂等），离线缓冲 + 断线重连补传后 `evidenceImageId` 关联不断。
- ✅ **19 项测试全绿**（server ingest 11 + LocalBuffer 2 + 重连/续传/证据关联 3 + LiveConfig+config推送 3）+ 真机跑通（Agent 事件/图片 → 落库/去重/入队 → 看板热力/复核/证据图）。
- ⏳ 待办：M2 云 OCR + L3 Logo + 风险评分；M3 CLIP / 完整哈希链复验 / 归档作业。里程碑见 architecture §15。

## 提交约定
默认不提交，除非用户明确要求。commit 信息用中文，简洁。

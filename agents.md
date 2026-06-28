# AGENTS.md — Honus

> 本文件遵循并指回工作区根准则 [`../AGENTS.md`](../AGENTS.md)。先读根准则，再读本文件。

## 项目一句话
**Honus** — 本地局域网**考试监考系统**，防止学员在编程 / OJ 考试中用 AI 做题或联网搜题。学员**本地 IDE 写 C++ + 网页判题**提交；服务器为局域网内 1+ 台笔记本。架构 = **纯检测 + 取证**（已决定不做网络/主机预防层），**元数据优先、图像为辅**，系统只初筛、人工裁决。权威设计见 [docs/architecture-v0.2.md](docs/architecture-v0.2.md)。

## 语言
所有文档、注释、提交信息一律用中文。

## 组件与技术栈
- **采集端 Agent**（考试机，每台一个）：C#/.NET 单文件 exe，需管理员权限（ETW / UIAutomation / WMI）。代码骨架 [agent/](agent/)。
- **监考服务器**（笔记本）：接收 + 分析 + 落库 + Web 看板；SQLite + 文件系统 + sqlite-vec。**仅服务器对外联网，且只为云 OCR**。
- **监考端 / 复核台**：实时看板 + 可疑队列复核。

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
- [agent/](agent/) — C#/.NET 采集端骨架（`Honus.Agent`）

## 状态
M1（最小闭环）设计完成，进入实现。里程碑见 architecture §15。

## 提交约定
默认不提交，除非用户明确要求。commit 信息用中文，简洁。

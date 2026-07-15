# 维度七 · 漂移审查（Drift — 代码/文档/设计意图不一致）

> 审查口径：代码与文档、代码与架构设计意图、DDL 与实做之间的不一致。

---

## F1 〔P3〕`architecture-v0.2.md` 内部对 sqlite-vec 表述不一致
**证据**
- D9 组件表（`architecture-v0.2.md:23`）：`存储后端 | SQLite + 文件系统 + sqlite-vec`。
- 数据流感慨图（`architecture-v0.2.md:37`）：`接收·校验·落库(SQLite + 文件 + sqlite-vec)`。
- 但同文档（`:126` / `:141`）已正确说明「`vec_images` 虚表预留但未启用、embedding 存普通 `image_embeddings` BLOB 表、C# 暴力余弦、不依赖 sqlite-vec 原生扩展」。

**影响**：权威设计文档自相矛盾；新人按 D9/§7 会误以为系统依赖 sqlite-vec 扩展。

**修复**：D9 与 §7 数据流删除 sqlite-vec，统一为「SQLite + 文件系统 +（预留）C# 暴力余弦检索」。

## F2 〔P3〕`schema.sql` 仍含 `vec_images` 虚表 DDL，与「未用 sqlite-vec」实做冲突
**证据**
- `schema/schema.sql:106-111` 声明 `vec_images USING vec0`；`schema.sql:113-120` 注释自承「M3 实做走普通表 + C# 暴力余弦」。
- `server/Data/Schema.cs:26` 运行时 `Where(!Contains("USING vec0"))` 剥离才不崩。

**影响**：死 DDL，与全局结论「未用 sqlite-vec」（README:53/98、AGENTS.md:104）冲突，易被误读。

**修复**：见 02-A1（删 DDL 或显式标注 + CI 断言）。

## F3 〔P3〕`schema.sql:122` 注释「判题网页前端埋点上报」与实现不符
**证据**
- `schema.sql:122`：`-- 击键节奏(判题网页前端埋点上报)`。
- 实际写入路径：`server/Ingest/KeystrokeIngest.cs`——外部判题后端经 `POST /ingest/keystroke` + `X-Horus-KSig`（KSK 会话签名）写入；前端埋点已撤（README:34 / AGENTS.md「待做：击键前端埋点已撤」）。

**影响**：注释误导，读档人以为还走前端埋点。

**修复**：改为 `-- 击键节奏(外部判题后端经 KSK 旁路 POST /ingest/keystroke 写入；前端埋点已撤)`。

## F4 〔P2〕M5 「看板健康告警」意图与实现漂移
**证据**
- 意图：AGENTS.md:101/104「M5 = 检测 + 上报 + **看板健康告警**」。
- 实现：`Suspicion.KindFor`（`Suspicion.cs:35-37`）把 M5 三类入 `suspicious_queue`；前端 `KIND_META` 缺标签（01-U2）→ 监考员看到 raw `screen_obscured` 串，而非可读健康告警。
- 同时 `suspected_suspend`（`RiskModel=0`）既不入队也无独立呈现（05-M8），等于意图里的「健康告警」只实现了一半。

**影响**：设计意图（可读健康告警）与落地（raw 串 + 部分信号静默）明显漂移。

**修复**：
1. 短期：补 3 个前端标签（01-U2），让健康信号可读。
2. 中期：按 01-U3 方案区分 `health` vs `os_signal` vs `vision` 来源，落实「健康告警」独立呈现。

**状态（2026-07-15）**：✅ 已实施。F4 的两条修复均随 01-U2（补标签）/ 01-U3（选方案 A：`source` 列 + 独立「采集健康」面板）落地。`suspected_suspend`（05-M8）仍按原设计 `RiskModel=0` 仅作健康提示、不入队，本次未改（属另一待办，非本决策范围）。

## F5 〔P3〕`README.md:34` 称「225 项测试全绿」需与当前实际对齐
**证据**：README / AGENTS.md 多处写「225 项测试全绿」「测试数 130→…」。随 M3/M4/M5 增改，测试数已变化，建议在 CI 或文档里用变量/动态数字，避免硬编码过时计数漂移至失真。

**修复**：文档改为「测试持续全绿（见 CI 报告）」或引用 `dotnet test` 实际输出，不硬编码具体数字。

---

## 优先级建议
1. F4（P2，意图漂移，随 01-U2/U3 修）。
2. F1 / F2 / F3（P3，文档与 DDL 漂移，顺手清）。
3. F5（P3，数字去硬编码）。

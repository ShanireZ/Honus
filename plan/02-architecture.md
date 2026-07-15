# 维度二 · 架构审查（Architecture）

> 审查对象：`Horus.sln` 下分层（`Horus.Contracts` / `Horus.Agent.Core` / `Horus.Agent` / `Horus.Server` / `tests`）、数据流、DB 访问、`server/Program.cs` 装配。
> 审查口径：分层合理性、耦合度、可扩展性、契约一致性。

---

## 总体评价（正面）
- **分层清晰、职责单一**：`Contracts` 承载共享线协议 / HMAC / 规范化（字节级一致，避免双端漂移）；`Agent.Core` 平台无关（传输 / 缓冲 / 重连），`Agent` 仅做 Windows 采集；`Server` 专注 ingest / 分析 / 看板 / 归档。层级边界明确，未见跨层反向依赖。
- **契约字节级共享**：`Crypto` / `EventCanonical`（`contracts/Crypto.cs`）双端共用同一实现，hash 链（`hashPrev→hashSelf`）与签名可逐字节复验——这是 M3 完整性复验能成立的根基，设计正确。
- **DB 读写分离合理**：单写连接（写锁串行）+ 独立只读连接池（WAL 并发读），看板 6 个 GET 走只读连接与写路径互不阻塞（AGENTS.md:66）。规模天花板设计务实。

---

## A1 〔P3〕`schema.sql` 仍声明 `vec_images` sqlite-vec 虚拟表（误导死代码）

**证据**
- `schema/schema.sql:106-111` 仍声明 `CREATE VIRTUAL TABLE IF NOT EXISTS vec_images USING vec0(...)`。
- 实做已改为普通 `image_embeddings` BLOB 表 + C# 暴力余弦（schema.sql:113-120 注释已说明「M3 实做·C# 暴力余弦·不依赖 sqlite-vec」；README:53/98、AGENTS.md:104 一致）。
- `server/Data/Schema.cs:26` 在 `Apply` 时 `.Where(st => !st.Contains("USING vec0"))` **运行时剥离**该语句才不崩溃。

**影响**
该虚表当前是「预留但未启用」的死 DDL；由于靠 `Schema.cs` 剥离才不报错，属于易被忽视的误导代码，新人会误以为系统真在用 sqlite-vec（与「未用 sqlite-vec」的全局结论冲突，见 07-漂移）。

**修复方案**
- 推荐：直接从 `schema.sql` 删除 `vec_images` 虚表 DDL，以 `image_embeddings` 为唯一真相。
- 若坚持「预留大规模检索余量」：改为显式注释 `/* 预留·永不启用·Schema.Apply 会跳过 USING vec0 */`，并加一条 CI 断言确认 `USING vec0` 语句被跳过、且运行时不建该虚表。

---

## A2 〔P3〕API 契约层缺乏独立测试，回归无防护

**证据**
- `server/Api/Endpoints.cs` 约 761 行、~20 个端点（login / authmode / exams CRUD / suspicious decide / capture / archive run / search-image / preflight 等）。
- `tests/` 下 16 个测试文件，但 `Endpoints` 关键字命中 **0 文件**（其余覆盖 ingest / integrity / archive / oidc / image-search / hardening / reliability 等）。`TestApp.cs` 可能提供部分集成覆盖，但无针对契约形状/状态码的聚焦测试。

**影响**
契约一旦改动（字段增删、状态码、鉴权门），看板/采集端可能在无测试报警的情况下失配。属于架构层面的「可测试性/可扩展性」缺口。

**修复方案**
- 新增 `EndpointsTests.cs`：用 `TestApp` 起服务 + `HttpClient` 跑关键路径断言（login 401→200、authmode 形状、exams 列表、suspicious decide 状态机、search-image 形状、capture `pushed` 语义）。至少覆盖「鉴权门 + 关键读写 + 错误码」。
- 将契约快照（请求/响应 JSON 形状）纳入测试，作为回归基线。

---

## A3 〔P3〕`capture_now` 采集端 handler 缺启动期断言（防御性）

**证据**
- `UplinkClient.OnCaptureNow`（`agentcore/Transport/UplinkClient.cs:33`）为可空 `Action<string>?`；`agent/Program.cs:153` 注入。若未来初始化顺序变化导致未注入，服务端 `pushed:true` 但 Agent 不抓图，形成「静默失效」。

**影响**
属潜在架构脆弱点（非已确认 bug），部署方难排查。

**修复方案**
- 在 Agent 启动完成自检中断言 `uplink.OnCaptureNow != null`，否则日志告警（甚至 fail-fast，取决于部署容许度）。

---

## 优先级建议
1. A1（P3，删死代码 / 加注释 + CI 断言）→ 顺手清。
2. A2（P3，补契约测试）→ 与 05-缺失 的测试缺口一并规划。
3. A3（P3，防御性断言）→ 低优先。

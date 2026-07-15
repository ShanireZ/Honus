# 维度四 · BUG 审查（Runtime / Logic / Boundary）

> 审查口径：运行时错误、逻辑错误、边界条件、未覆盖的潜在缺陷源。
> 说明：本项目 225 项测试全绿、契约字节级共享、hash 链复验、logout 竞态已修（见 AGENTS.md）。**本次静态审查未发现 P0/P1 级崩溃 bug**，以下为 P2/P3 级逻辑/边界风险与「未测路径」隐患。

---

## B1 〔P2〕`ImageSearchStore.TopN` 近正交图不过滤，检索质量下降

**状态（2026-07-15）**：✅ 已实施。`ServerConfig` 新增 `EmbedCosineFloor`（默认 0.2）；`ImageSearchStore.TopN` 改为 `.Where(x => x.score >= cfg.EmbedCosineFloor)`（由端点传入 `cfg.EmbedCosineFloor`），近正交（score≈0）帧被过滤；`server.config.sample.json` 同步暴露该配置与注释。

**证据**
- `server/Analysis/Search/ImageSearchStore.cs:48-54`：
  ```csharp
  .Where(x => x.score > -1)      // 余弦 ∈ [-1,1]，近正交图 score≈0 仍通过
  .OrderByDescending(x => x.score)
  .Take(n <= 0 ? 20 : n)
  ```
- 过滤 `score > -1` 仅丢弃「完全反向」匹配；CLIP 余弦对不相关图常 ≈0（甚至略正），仍会进入结果。当语料 < topN 时，「按图搜图」会混入大量 score≈0 的无关帧。

**影响**：非崩溃，但检索质量降级，监考员在结果条里看到大量无关缩略图，干扰研判。

**修复方案**
- 将阈值改为可配的 `cosineFloor`（CLIP 经验值 ~0.2）：`.Where(x => x.score >= cfg.EmbedCosineFloor)`。
- 或在返回中保留 `score`，由前端按阈值隐藏低分结果；阈值建议在 `server.config.sample.json` 暴露。

---

## B2 〔P3〕`search-image` 的 `topN` 仅做下界兜底，无上界 clamp

**状态（2026-07-15）**：✅ 已实施（随 B1）。`Endpoints.cs` 端点 `topN = Math.Clamp((int?)body?["topN"] ?? 20, 1, 100)`；`ImageSearchStore.TopN` 内部 `Math.Clamp(n, 1, 100)` 双保险，杜绝超大 `topN` 输入。

**证据**
- `Endpoints.cs:77` `int topN = (int?)body?["topN"] ?? 20;`；`ImageSearchStore.TopN` 仅 `n <= 0 ? 20 : n`（`ImageSearchStore.cs:53`）。
- 传入超大 `topN`（如 100000）虽因语料仅单场几千而实际影响有限，但属未防御的输入。

**修复方案**：`topN = Math.Clamp(topN, 1, 100);`（或配置上界），与 B1 一并修。

---

## B3 〔P3〕M5 kind 映射与风险分无单测（潜在 bug 源）

**状态（2026-07-15）**：✅ 已实施。新增 `tests/SuspicionTests.cs`（3 项理论）：`Suspicion.KindFor` 全分支（含 `SuspectedSuspend→"suspected_suspend"`、`WindowFocus→"suspect"` 兜底）、`RiskModel.Derive` M5 分值（60/55/55/0）、`SourceForKind`（`health` vs `suspicion`）。把「新增 kind 必补前端标签」变成可断言契约（联动 08-T4）。

**证据**
- `Suspicion.KindFor`（`Suspicion.cs:35-37`）对 M5 三类映射、`RiskModel` 中 M5 分值（`ScreenshotObscured=60 / CapabilityDegraded=55 / WatchdogRestart=55`，`suspected_suspend=0`）在 `tests/` 中**0 文件**覆盖（关键字 `ScreenObscured` / `Suspicion` 命中 0）。
- 一旦这些值或映射被改（如新增第 4 个健康信号、改分值），无测试报警，且会直接连累 01-U2 的渲染（缺标签→raw 串）。

**修复方案**：新增 `SuspicionTests.cs` 覆盖 `KindFor` 全部分支（含 M5 三类与默认 `suspect` 兜底）、`RiskModel` 各 SignalType 分值（含 M5）。这同时把「新增 kind 必补前端标签」变成可断言的契约。

---

## B4 〔P3〕`capture_now` 端到端无测试（潜在回归）

**状态（2026-07-15）**：✅ 已实施。新增 `tests/CaptureTests.cs`（2 项）：在线 Agent `POST /api/agents/ag-A07/capture` → `pushed:true` 且经 WS 收到 `capture_now` 帧；不在线 Agent → `pushed:false`。用 `app.ConnectEventsAsync` + `Ws` 助手断言。

**证据**：`tests/` 中 `capture_now` 仅 1 处弱引用，无「服务端 push → Agent 抓图 → 图片入证据流」的端到端断言（见 05-缺失）。

**修复方案**：在 `ReliabilityTests` 或新增 `CaptureTests` 中模拟 Agent 连上后，服务端 `POST /capture` → 断言 `pushed:true` 且随后收到一张 `trigger=capture_now` 的证据图。

---

## 已核对「非 bug」项（避免误报）
- **按图搜图**：端点（`Endpoints.cs:71`）、前端按钮显隐（`app.js:217/1125`）、`searchSimilar`（`app.js:1141`）均接通；嵌入器关时 `imageSearchEnabled=false` 按钮隐藏，行为正确——**非失效**。
- **capture_now**：后端 + 采集端接通，仅缺 UI（归 01-U1 / 05-缺失），**无运行时 bug**。
- **哈希链复验 / 归档 / OIDC / 预检 / 裁决**：均有实现与测试支撑。

---

## 优先级建议
1. B1（P2，改一行阈值，检索质量立竿见影）。
2. B2+B1 合并改 `TopN`。
3. B3 / B4（P3，补测试，前置 02-A2）。

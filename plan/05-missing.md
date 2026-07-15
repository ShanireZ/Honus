# 维度五 · 缺失审查（Missing）

> 审查口径：未实现的功能、未处理的异常、缺失的测试、缺失的对接文档/示例。

---

## M1 〔P2〕看板「点名抓图」按钮缺失（功能未连 UI）
**状态（2026-07-15）**：✅ 已实施（见 01-U1）。座位详情抽屉「点名抓图」按钮已接 `POST /api/agents/{agentId}/capture`，仅在线时可用。

## M2 〔P2〕M5 健康信号前端标签缺失（后端已入队，前端未呈现）
**状态（2026-07-15）**：✅ 已实施（见 01-U2 / 03-D1）。`KIND_META` 增补三类 M5 标签。

## M3 〔P3〕API 契约测试缺失
**状态（2026-07-15）**：✅ 已实施（见 02-A2）。新增 `tests/EndpointsTests.cs`。

## M4 〔P3〕M5 kind 映射 / 风险分 / capture_now 端到端测试缺失
**状态（2026-07-15）**：✅ 已实施（见 04-B3 / 04-B4）。新增 `SuspicionTests.cs` + `CaptureTests.cs`。

## M5 〔P3〕视觉分析（Vision）覆盖偏弱
**状态（2026-07-15）**：✅ 已实施。新增 `tests/VisionTests.cs`：`VisionVerdict.Kind()` 全分支映射（web_ai/search/ide_plugin→ide_plugin_suspect、remote_tool/other|none→suspect）、Susicious 标记、以及 `VisionAnalysisService.Enqueue` → `ocr_results` 落库 + `analysis_state=1` 被轮询拾回（visionMock:true）。

## M6 〔P3〕KSK 击键旁路缺乏对接文档 / 示例
**状态（2026-07-15）**：✅ 已实施。新增 `docs/ksk-keystroke-integration.md`：签名构造 `HMAC(KSK,"keystroke\n"+sha256(body))`、幂等 `submission_id` 语义、字段映射表、Python/Node/curl 最小示例、服务器打分/入队规则、配置与安全检查清单。

## M7 〔P3〕预检未覆盖 CLIP 模型存在性
**状态（2026-07-15）**：✅ 已实施（联动 03-D4）。`/api/preflight` 新增 `clip_model` 检查项：嵌入器关时为 `ok`；onnx provider 但 `model.onnx`（按 `EmbedOnnxModelPath` 或 `DataDir/model.onnx`）缺失时为 `fail`；remote provider 为 `ok`。看板据以提示「按图搜图不可用原因」。

## M8 〔P3〕`suspected_suspend` 信号无看板呈现路径
**状态（2026-07-15）**：✅ 已实施（随 01-U3 方案 A）。`Suspicion.SourceForKind` 把 `suspected_suspend` 归入 `health`；`EventIngest` 入队门改为「风险达阈值 ∨ 强制复核 ∨ 是 health 类」——`suspected_suspend`（`RiskModel=0`）因此强制入队到 `suspicious_queue` 且 `source='health'`，由独立「采集健康」面板呈现（score 0 显示为「—」）；`KIND_META` 补 `suspected_suspend`「疑似挂起」标签。`RiskModel` 注释同步修正为「走采集健康面板」。

---

## 优先级建议
1. M1 + M2（P2，与 01 同源，先做）。
2. M3 + M4 + M5（P3，测试债，排期补）。
3. M6 + M7 + M8（P3，文档/预检/呈现缺口）。

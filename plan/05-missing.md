# 维度五 · 缺失审查（Missing）

> 审查口径：未实现的功能、未处理的异常、缺失的测试、缺失的对接文档/示例。

---

## M1 〔P2〕看板「点名抓图」按钮缺失（功能未连 UI）
- 后端 `POST /api/agents/{agentId}/capture` 与采集端 `OnCaptureNow` 已就绪（见 01-U1）。
- 看板无任何触发入口 → **功能对最终用户缺失**。修复见 01-U1。

## M2 〔P2〕M5 健康信号前端标签缺失（后端已入队，前端未呈现）
- 见 01-U2 / 03-D1。后端 `Suspicion.KindFor` 已入队三类 M5 kind，前端 `KIND_META` 缺标签 → 渲染 raw 串。

## M3 〔P3〕API 契约测试缺失
- `Endpoints.cs` ~20 端点无独立契约测试（`Endpoints` 关键字命中 0 文件）。见 02-A2。

## M4 〔P3〕M5 kind 映射 / 风险分 / capture_now 端到端测试缺失
- 见 04-B3 / 04-B4。

## M5 〔P3〕视觉分析（Vision）覆盖偏弱
- `tests/` 中 `Vision` 仅 2 文件弱覆盖；`VisionVerdict.Kind()` 映射、`VisionAnalysisService` 原子认领 / 补偿重扫（analysis_attempts 闩锁）缺乏聚焦断言。
- 建议补：`VisionVerdictTests`（Category→kind 全分支）、`VisionAnalysisServiceTests`（attempts 上限防死循环、临时失败保持 `analysis_state=0` 由补偿拾回）。

## M6 〔P3〕KSK 击键旁路缺乏对接文档 / 示例
- `keystroke_samples` 由外部判题后端经 `POST /ingest/keystroke` + `X-Horus-KSig`（HMAC(KSK, "keystroke\n"+sha256(body))）写入（`server/Ingest/KeystrokeIngest.cs:42-101`；`Program.cs:352` 注册）。
- Horus 仓库内**无判题前端/后端示例或对接说明**，部署方（尤其是外部判题系统）不知如何接。
- 修复：在 `docs/` 增加「KSK 击键旁路对接」小节 + 最小示例（签名构造、幂等 `submission_id`、字段映射）。

## M7 〔P3〕预检未覆盖 CLIP 模型存在性
- `/api/preflight`（`Endpoints.cs:98`）检查鉴权配置 / cpplearn 可达 / 白名单覆盖，但未查「CLIP 模型（model.onnx）是否存在」。
- 修复：加一项 `clip_model` 检查（文件存在 + 维度匹配），看板提示「按图搜图不可用原因」（联动 03-D4）。

## M8 〔P3〕`suspected_suspend` 信号无看板呈现路径
- `RiskModel.SuspectedSuspend=0`（仅健康提示、不入队），但 AGENTS.md:104 把 `suspected_suspend` 列为 4 个 M5 健康信号之一。它既不入 `suspicious_queue`（score 0），也无独立健康面板——等于**静默丢弃**。
- 修复：要么在座位在线状态/健康面板呈现 `suspected_suspend`，要么在文档中显式说明「该信号仅作 Agent 侧日志、看板不呈现」，避免读档人误以为漏实现。

---

## 优先级建议
1. M1 + M2（P2，与 01 同源，先做）。
2. M3 + M4 + M5（P3，测试债，排期补）。
3. M6 + M7 + M8（P3，文档/预检/呈现缺口）。

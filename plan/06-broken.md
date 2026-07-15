# 维度六 · 失效审查（Broken — 已实现但不可用）

> 审查口径：代码里存在、但运行时实际不工作 / 行为错误的功能。
> 结论：**本次审查未发现已确认的「已实现但失效」功能**。以下仅列「潜在失效风险」与「需 owner 确认的边界」。

---

## 已逐项核对、确认可用（避免误报为失效）
| 功能 | 端点/代码 | 前端 | 结论 |
|---|---|---|---|
| 按图搜图 | `Endpoints.cs:71` `POST /api/exams/{id}/search-image` | `app.js:217` 取 `imageSearchEnabled`、`app.js:1125` 显隐、`app.js:1141` `searchSimilar` | **可用**（嵌入器关时按钮隐藏，行为正确） |
| 点名抓图 | `Endpoints.cs:612` + `UplinkClient.cs:176` + `agent/Program.cs:153` | 无按钮 | **后端+采集端可用，仅缺 UI**（归 01-U1 / 05-M1，非失效） |
| 哈希链复验 | `EventIngest` + `integrity` 端点 | — | 可用（有测试） |
| 归档作业 | `ArchiveService` | — | 可用（有测试） |
| OIDC / RBAC | `IngestAuth` / `admin_sessions` | 登录门 | 可用 |
| 考前预检 | `Endpoints.cs:98` | 预检按钮 | 可用 |
| 可疑裁决 | `Endpoints.cs:635` `decide` | 队列裁决 | 可用 |

---

## X1 〔P3·潜在〕`capture_now` 若 Agent 端 `OnCaptureNow` 未注入 → 静默失效
- `UplinkClient.OnCaptureNow` 为可空 `Action?`（`UplinkClient.cs:33`）。若初始化顺序异常导致未注入，服务端返回 `pushed:true` 但 Agent 不抓图。
- 属**潜在失效**（非已确认），修复见 02-A3（启动期断言）。

## X2 〔P3·潜在〕CLIP 模型缺失时「按图搜图」整链路不可用且无引导
- 非代码 bug（`imageSearchEnabled=false` 正确隐藏按钮），但部署方若忘记放 `model.onnx`，该能力静默消失、无预检提示。
- 修复见 05-M7（预检加 CLIP 模型检查）+ 03-D4（禁用态引导）。

---

## 优先级建议
- 本维度**无 P0–P2 项**。仅 X1/X2 两个 P3 防御性项，随 02-A3 / 05-M7 / 03-D4 一并处理即可。
- 若你确认「点名抓图按钮」是**有意不暴露**到看板，则 01-U1 应降级为「按预期行为」，不属于缺失也不属于失效。

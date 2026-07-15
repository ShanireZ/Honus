# 维度六 · 失效审查（Broken — 已实现但不可用）

> 审查口径：代码里存在、但运行时实际不工作 / 行为错误的功能。
> 结论：**本次审查未发现已确认的「已实现但失效」功能**。以下仅列「潜在失效风险」与「需 owner 确认的边界」。

---

## 已逐项核对、确认可用（避免误报为失效）
| 功能 | 端点/代码 | 前端 | 结论 |
|---|---|---|---|
| 按图搜图 | `Endpoints.cs:71` `POST /api/exams/{id}/search-image` | `app.js:217` 取 `imageSearchEnabled`、`app.js:1125` 显隐、`app.js:1141` `searchSimilar` | **可用**（嵌入器关时按钮隐藏，行为正确） |
| 点名抓图 | `Endpoints.cs:612` + `UplinkClient.cs:176` + `agent/Program.cs:153` | 座位抽屉「点名抓图」按钮（`app.js`） | **可用**（UI 已补齐，归 01-U1，2026-07-15 已实施） |
| 哈希链复验 | `EventIngest` + `integrity` 端点 | — | 可用（有测试） |
| 归档作业 | `ArchiveService` | — | 可用（有测试） |
| OIDC / RBAC | `IngestAuth` / `admin_sessions` | 登录门 | 可用 |
| 考前预检 | `Endpoints.cs:98` | 预检按钮 | 可用 |
| 可疑裁决 | `Endpoints.cs:635` `decide` | 队列裁决 | 可用 |

---

## X1 〔P3·潜在〕`capture_now` 若 Agent 端 `OnCaptureNow` 未注入 → 静默失效
**状态（2026-07-15）**：✅ 已实施（见 02-A3）。`agent/Program.cs` 启动断言 + `UplinkClient.OnCaptureNow` 注释点明，未注入即告警。

## X2 〔P3·潜在〕CLIP 模型缺失时「按图搜图」整链路不可用且无引导
**状态（2026-07-15）**：✅ 已实施（见 05-M7 + 03-D4）。预检 `clip_model` 检查 + 灯箱禁用态「按图搜图」按钮 + tooltip 提示「未部署 CLIP 模型(model.onnx)」。

---

## 优先级建议
- 本维度**无 P0–P2 项**。仅 X1/X2 两个 P3 防御性项，随 02-A3 / 05-M7 / 03-D4 一并处理即可。
- ~~「点名抓图按钮是否有意不暴露」~~ → **已确认暴露到看板**（2026-07-15 实施），01-U1 维持「缺失→已修」，非「按预期行为」。

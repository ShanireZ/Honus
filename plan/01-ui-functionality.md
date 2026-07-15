# 维度一 · 界面与功能审查（UI & Functionality）

> 审查对象：`server/wwwroot/`（看板单页应用 `index.html` / `app.js` / `styles.css`）、与看板交互的后端端点 `server/Api/Endpoints.cs`、采集端 `agent/` + `agentcore/`。
> 审查口径：功能完整性、可用性、交互逻辑正确性（按钮是否接得到后端、渲染是否正确）。
> 严重度定义：**P0** 阻断/安全/数据损坏 → **P1** 主功能失效 → **P2** 功能缺口或明显正确性/体验缺陷 → **P3** 整洁度/可选优化。

---

## U1 〔P2〕看板缺「监考员点名抓图」触发入口（capture_now 不可达）

**证据**
- 后端已端到端实现：`Endpoints.cs:611-614` 的 `POST /api/agents/{agentId}/capture`（D2 承诺）向在线 Agent 推 `capture_now`；采集端 `agentcore/Transport/UplinkClient.cs:176-177` 解析 `capture_now` 并触发 `OnCaptureNow`；`agent/Program.cs:153` 把 `OnCaptureNow` 接成 `_ = capturer.CaptureAsync(reason, dedupAgainstLast: false)`。
- 但是：`index.html` 的 topbar 仅含 开始 / 结束 / 全场登出 / 白名单 / 预检（见 AGENTS.md:105「topbar 三按钮」描述，未含抓图）；`app.js` 中**无任何 `capture` / `抓图` / `点名` handler**；`KIND_META` 与座位抽屉均无触发入口。

**影响**
D2 在「后端 + 采集端」层面已真实可用（AGENTS.md:87 已记「D2 capture_now 帧从文档承诺变真」），但监考员**无法从看板发起点名抓图**——功能对最终用户不可达，等于半完成。

**修复方案**
1. 在 topbar 或（更自然）在「座位抽屉 / 在线座位操作菜单」增加「点名抓图」按钮。
2. 按钮仅在该 seat 的 agent 在线时可用（可复用 `/api/exams/{id}/seats` 或心跳在线判定），离线置灰。
3. 点击后 `POST /api/agents/{agentId}/capture`（body 可选 `{reason:"proctor_call"}`），依据返回 `pushed` 给轻提示；成功后该图进入证据流并可在灯箱查看。
4. 建议同时在前端补一个 `capture_now` 端到端测试（见 05-缺失 / 04-BUG）。

**状态（2026-07-15）**：✅ 已实施。在「座位详情」抽屉顶部增加「点名抓图」按钮（`app.js` `openSeatDrawer` + `captureNow`），仅在该座位 `online && agentId` 时显示；点击调用 `POST /api/agents/{agentId}/capture` 并依据返回 `pushed` toast 提示。后端 D2 原已就绪，无需改动。mock 模式同样可用。

---

## U2 〔P2〕M5 健康信号在可疑队列中渲染为原始 snake_case

**证据**
- 后端入队：`Suspicion.KindFor`（`server/Analysis/Suspicion.cs:35-37`）对 `ScreenshotObscured → "screen_obscured"`、`CapabilityDegraded → "capability_degraded"`、`WatchdogRestart → "watchdog_restart"` 入队到 `suspicious_queue`；`RiskModel` 对这三类的分值为 `ScreenshotObscured=60 / CapabilityDegraded=55 / WatchdogRestart=55`。
- 前端缺标签：`app.js` 的 `KIND_META`（`app.js:15-26`）共 10 个标签，覆盖 `web_ai / search / non_whitelist_proc / large_paste / usb / ide_plugin_suspect / browser_unreadable / non_whitelist_web / remote_tool / suspect`——**唯独缺上述 3 个 M5 kind**。
- 回退逻辑：`kindMeta()`（`app.js:28`）对未命中项返回 `{ label: kind || "未知", ... }`，即把 `screen_obscured` 原样显示成灰色原始串。

**影响**
AGENTS.md:101/104 明确 M5 目标是「检测 + 上报 + **看板健康告警**」，但实际监考员在可疑队列里看到的是 `screen_obscured` 这类机器串，违背「健康告警」可读意图，且无法与真正作弊线索区分。

**修复方案**
在 `KIND_META` 增补（配色沿用暗色取证主题，与既有 `bd/bg/fg` 风格一致）：
```js
screen_obscured:    { label: "截图被遮蔽",   fg: "#ffd27f", bg: "#3a2c07", bd: "#a9841f" },
capability_degraded:{ label: "采集能力降级", fg: "#ffbe6b", bg: "#3a2a0e", bd: "#a9741f" },
watchdog_restart:   { label: "看门狗重启",   fg: "#7fd3ff", bg: "#12303f", bd: "#2f6f92" },
```
并补一条 `Suspicion.KindFor` → `KIND_META` 的「kind 全表」文档，避免后人新增 kind 时漏标签（根因见 08-错漏 / 07-漂移）。

**状态（2026-07-15）**：✅ 已实施。`KIND_META` 已增补 `screen_obscured`「屏幕遮挡」/`capability_degraded`「能力降级」/`watchdog_restart`「看门狗重启」三组标签（暗金配色，与既有取证主题一致）。

---

## U3 〔P3〕健康信号与作弊线索混排，信息层级不清

**证据/影响**
M5 健康信号（如 `screenshot_obscured` 可能仅是全屏应用，`suspected_suspend` 在 `RiskModel` 中 `=0` 仅作健康提示、不入队）以「可疑项」形态进入 `suspicious_queue`，与真正作弊线索（AI 网站 / 远控工具 / 大段粘贴）混在同一张表、同一渲染通道，无 `source/category` 区分。监考员易误判或漏看健康告警。

**修复方案（已拍板：选 方案 A）**
- ✅ **方案 A（已实施）**：在 `suspicious_queue` 增加 `source` 列（`suspicion` / `health`），M5 三类（`screen_obscured` / `capability_degraded` / `watchdog_restart`）入队时标 `source='health'`，其余（含 vision / keystroke 入队）默认 `suspicion`。看板用独立「采集健康」面板呈现 `health` 类，不混入可疑复核、不计入作弊裁决率（`/suspicious` 默认 `source='suspicion'`，新增 `/health` 只读端点）。
- 方案 B（未采用）：M5 不进 `suspicious_queue`。因与既有归档/取证链耦合、改造成本高，且 A 已满足「解耦呈现」诉求，故未采用。

**状态（2026-07-15）**：✅ 已实施。后端 `source` 列 + 迁移 + `EnqueueSuspicious` 赋值 + `/health` 端点 + 裁决拦截 health；前端分段切换「可疑复核 / 采集健康」、health 行只读并跳转座位详情。

---

## 优先级建议
1. U2（P2，纯前端标签，低风险高收益，半小时可修）→ 优先。
2. U1（P2，需加按钮 + 联调，但功能已就绪）→ 紧随。
3. U3（P3，设计决策，先定方案再动）。

## 待确认（需你/owner 拍板）
- U3 的 A/B 方案选哪个？是否接受健康信号继续留在 `suspicious_queue`（仅加 `source` 区分）？
- U1 的「点名抓图」按钮若当初是**有意不暴露**到看板（例如仅留 API 给自动化调用），则 U1 可降级为「按预期行为」，请确认。

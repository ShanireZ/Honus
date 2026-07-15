# 维度八 · 错漏审查（Typos / Naming / Stale Comments）

> 审查口径：拼写、命名规范、过时/误导性注释、缺注释导致的误读。本维度多为 P3 整洁度项，单独列出便于「顺手清」。

---

## T1 〔P3〕`schema.sql:122` 过时注释（与实现不符）
- 原文：`-- 击键节奏(判题网页前端埋点上报)`。
- 问题：前端埋点已撤，真实路径是外部判题后端 KSK 旁路（见 07-F3）。属字面错漏 + 误导。
- 修复：改为 `-- 击键节奏(外部判题后端经 KSK 旁路 POST /ingest/keystroke 写入；前端埋点已撤)`。

## T2 〔P3〕`architecture-v0.2.md` 残留旧术语 / 过时存储表述
- D9（`:23`）与 §7 数据流（`:37`）仍写 sqlite-vec（见 07-F1）。属文档错漏，需同步修正。

## T3 〔P3〕`ImageSearchStore.TopN` 过滤 `score > -1` 意图不清
- `ImageSearchStore.cs:51` 的 `.Where(x => x.score > -1)` 本意疑似「丢弃完全反向匹配」，但实际近正交图（≈0）仍通过，语义与注释/命名不符，易让维护者误以为「已过滤无关图」。
- 修复：改名/加注释明确阈值语义，或改为可配 `cosineFloor`（见 04-B1）。

## T4 〔P3〕`SignalType` 枚举 → kind 命名风格混用，缺映射总表
- `ScreenshotObscured`（PascalCase 枚举）→ `screen_obscured`（snake_case kind）；`WatchdogRestart` → `watchdog_restart`。契约统一 snake_case 是对的，但**缺乏一张「SignalType → kind → 前端标签」总表**，导致后人新增 kind 时（如第 4 个健康信号）极易漏补 `KIND_META` 标签（这正是 01-U2 的根因）。
- 修复：在 `docs/` 或 `Suspicion.cs` 顶部维护一张总表注释；并加测试断言「`KIND_META` 覆盖 `Suspicion.KindFor` 所有可能返回值」（联动 04-B3）。

## T5 〔P3〕`vec_images` 虚表命名暗示「已启用」实则预留
- `schema.sql:106` `vec_images` 命名与「未启用」状态不符（见 02-A1 / 07-F2）。若保留，建议注释明确「预留·永不启用」，避免读档人误判。

## T6 〔P3〕建议补充的缺注释点
- `UplinkClient.OnCaptureNow`（`agentcore/Transport/UplinkClient.cs:33`）可空，建议在字段上方注释「未注入则 capture_now 静默失效（见 02-A3）」。
- `ImageEmbedService.Enabled`（`ImageEmbedService.cs:18`）建议注释明确「嵌入器未注册（off/mock 未配）时为 false → 整服务 no-op」，便于读档人理解 `imageSearchEnabled` 来源链。

---

## 优先级建议
- T1 / T2 随 07-F3 / 07-F1 一并改（同源）。
- T3 随 04-B1 改。
- T4 是根因性整洁项，建议与 04-B3 测试一起做，长期防回归。
- T5 / T6 顺手补。

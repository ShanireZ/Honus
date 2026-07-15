# 维度三 · 设计审查（Design / UI Consistency）

> 审查对象：`server/wwwroot/styles.css`、看板视觉语言、响应式、`KIND_META` 配色一致性、`index.html` 结构。
> 审查口径：UI 一致性、响应式适配、信息设计层级。

---

## 总体评价（正面）
- 暗色「取证控制台」主题统一：`styles.css` 使用 CSS 变量（`--bg/--fg/--accent/--danger` 等），组件风格一致。
- 响应式已有基础断点：`@media (max-width: 1100px)` 把右栏（抽屉/详情）堆叠到主区下方（AGENTS.md 结构一致）。

---

## D1 〔P3〕`KIND_META` 缺 3 个 M5 标签 → 部分可疑项缺配色/中文，打破主题一致

**状态（2026-07-15）**：✅ 已实施（随 01-U2 一并修）。`KIND_META` 增补 `screen_obscured`「屏幕遮挡」/`capability_degraded`「能力降级」/`watchdog_restart`「看门狗重启」三组标签，并补 `suspected_suspend`「疑似挂起」标签；严格沿用既有 `fg/bg/bd` 暗色取证配色范式。

**证据**
- `app.js:15-26` 的 `KIND_META` 缺 `screen_obscured / capability_degraded / watchdog_restart` 三标签；回退（`app.js:28`）用统一灰底 + 原始 kind 串。
- 见 01-U2 同一条目（功能维度），此处强调**设计一致性**受损：同一张可疑队列里，10 类有专属配色、3 类退化为灰底机器串，视觉语言断裂。

**修复方案**：同 01-U2，增补 3 标签并严格沿用既有 `fg/bg/bd` 配色范式。

---

## D2 〔P3〕健康信号与作弊线索视觉层级未区分（信息设计）

**状态（2026-07-15）**：✅ 已实施（随 01-U3 方案 A）。看板以独立「采集健康」面板（冷色、只读、`source='health'`）呈现 M5 健康信号，与「可疑复核」（红/橙、可裁决、`source='suspicion'`）在颜色/位置/权重上明确分层；M5 kind 配色沿用 D1 暗金冷调，与违规橙红区分。

**证据/影响**
M5 健康信号与作弊线索共用同一渲染通道、同一张表（见 01-U3）。设计上应让「采集健康」与「违规嫌疑」在颜色/位置/权重上明显分层，否则监考员注意力被稀释。

**修复方案**：与 01-U3 方案联动——`health` 类用冷色（蓝/灰）角标、独立小面板或独立 Tab，与违规（红/橙）区分。

---

## D3 〔P3〕窄屏断点覆盖不足，灯箱/抽屉移动宽度可用性未验证

**状态（2026-07-15）**：✅ 已实施。`styles.css` 增加 `@media (max-width: 760px)` 断点：座位热力图网格缩为 `minmax(84px,1fr)` + gap 7px 防横向溢出；灯箱 `max-width:92vw` 可滚动；`#similarResults` `max-width:94vw` 横向滚动；`.panel__head` / `.topbar` `flex-wrap` 在小窗堆叠。

**证据**
- `styles.css` 仅 `@media (max-width: 1100px)` 一个主要断点（右栏堆叠）。
- 座位热力图网格在 760px 以下（平板/小窗）是否横向溢出、灯箱大图与「按图搜图」结果条在窄屏是否可滚动，未见针对性断点与实测。

**修复方案**
- 增加 `@media (max-width: 760px)` 断点：热力图改为可横向滚动容器或换行更小单元格；灯箱图片 `max-width:92vw`、相似结果条横向滚动。
- 在预检/ smoketest 中补一条「窗口宽度 375/768/1280 三档布局不溢出」的目检清单。

---

## D4 〔P3〕「按图搜图」不可用状态缺乏引导设计

**状态（2026-07-15）**：✅ 已实施。`app.js` 灯箱在嵌入器未配时**保留禁用态「按图搜图」按钮**（`btn.hidden=!imageSearchEnabled; btn.disabled=!usable`），`title` 提示「按图搜图不可用:未部署 CLIP 模型(model.onnx)」；`searchSimilar` 对 `!state.imageSearchEnabled` 直接 toast 并返回。联动 05-M7：预检新增 `clip_model` 检查项，指明不可用原因。

**证据**
- `imageSearchEnabled`（`app.js:192` 默认 `false`）由 `/api/authmode` 下发（`Endpoints.cs:67`）；嵌入器未配（无 `model.onnx`）时按钮常隐。
- 监考员看不到按钮，也无任何「为何不可用 / 如何启用」的提示。

**修复方案**：嵌入器未启用时，灯箱保留一个禁用态「按图搜图」按钮 + tooltip「需部署 CLIP 模型（model.onnx）」，并在 `/api/preflight` 增加「CLIP 模型存在性」检查项（见 05-缺失）。

---

## 优先级建议
1. D1（P3，随 01-U2 一并修，无额外成本）。
2. D2（P3，与 01-U3 方案联动）。
3. D3 / D4（P3，独立小优化，按排期）。

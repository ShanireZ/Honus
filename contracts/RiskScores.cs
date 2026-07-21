namespace Horus.Contracts;

/// 风险分常量(集中定义,Agent 自报与服务器复判共用同一份,避免两路"真相来源"漂移 · 质量#2)。
///
/// 值即 0–100 风险分。系统只初筛、人工裁决(架构铁律 §3),故取值偏「宁高勿低」——
/// 高分进可疑队列交人工复核,低分仅作软信号;漏报(放走作弊)比误报(多一次人工复核)代价更高。
///
/// Agent 端自报分与服务器 <c>RiskModel.Derive</c> 复判分都引用此处,任一处调分只需改这一份。
public static class RiskScores
{
    // ---- 浏览器 URL ----
    public const int BrowserUrlAi = 80;            // 命中 AI 站黑名单(服务器独立复判)
    public const int BrowserUrlSearch = 70;        // 命中搜索引擎黑名单
    public const int BrowserUrlNonWhitelist = 80;  // 非白名单站(无 AI/搜索标签,但确非白名单)
    public const int BrowserUrlUnreadable = 40;    // 地址栏读不到 → 降级:强制抓图交人工看

    // ---- 进程 / 远控 ----
    public const int RemoteTool = 70;              // 远控工具进程(teamviewer/anydesk/…)
    public const int NonWhitelistProc = 70;        // 非白名单进程

    // ---- 剪贴板 / 粘贴 ----
    public const int LargePaste = 60;              // 大段粘贴

    // ---- 设备 ----
    public const int Usb = 50;                     // USB 设备接入

    // ---- 窗口行为 ----
    public const int AltTabBurst = 40;             // 频繁 Alt+Tab

    // ---- M5 采集端硬化健康信号 ----
    public const int ScreenObscured = 60;          // 屏幕遮挡 / 黑帧
    public const int CapabilityDegraded = 55;      // 采集能力降级(非管理员 / 信号源启动失败)
    public const int WatchdogRestart = 55;         // 采集进程异常重启(规避取证)

    // ---- 击键(发自判题后端,服务器复判同源)----
    public const int KeystrokeIdleThenBlock = 70;  // 空窗后突现整段代码
    public const int KeystrokePaste = 60;          // 粘贴
    public const int KeystrokeBurst = 55;          // 超人输入速度
}

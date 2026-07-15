using System.Text.Json;
using Horus.Contracts;

namespace Horus.Server.Analysis;

/// M1 服务器侧初筛:由事件类型 + payload 判定可疑类别(kind)。
/// 系统只初筛,处分由人裁决(architecture 铁律 §3)。风险分由 Agent 本地初判,
/// 服务器 risk ≥ 阈值即入 suspicious_queue;M2/M3 再叠加 OCR/Logo/击键。
public static class Suspicion
{
    /* SignalType → kind → 前端标签 总表（T4：新增信号时据此同步补 KIND_META，防漏标签）
     * ScreenshotObscured   → screen_obscured     → 屏幕遮挡      (health)
     * CapabilityDegraded   → capability_degraded → 能力降级      (health)
     * WatchdogRestart      → watchdog_restart    → 看门狗重启    (health)
     * SuspectedSuspend     → suspected_suspend    → 疑似挂起      (health)
     * BrowserUrl(ai)       → web_ai              → AI 网站       (suspicion)
     * BrowserUrl(search)   → search              → 搜题         (suspicion)
     * BrowserUrl(other)    → non_whitelist_web   → 非白名单网站  (suspicion)
     * BrowserUrl(unreadable)→ browser_unreadable → 浏览器不可读  (suspicion)
     * ProcessStart         → non_whitelist_proc  → 非白名单进程  (suspicion)
     * Clipboard            → large_paste         → 大段粘贴      (suspicion)
     * Usb                  → usb                 → USB 设备      (suspicion)
     * (其它/兜底)           → suspect             → 可疑         (suspicion)
     * 视觉判定(ide_plugin/remote_tool)经 VisionVerdict.Kind() 映射,同源归 suspicion。
     */
    // 命中黑名单即细分标签。黑名单与 RiskModel 共用同一份,避免风险判据与标签判据漂移。
    private static string[] AiHosts => RiskModel.AiHosts;
    private static string[] SearchHosts => RiskModel.SearchHosts;

    /// 返回 kind;返回 null 表示该事件不单独入队(例如低于阈值的软信号)。
    public static string KindFor(SignalType type, JsonElement payload)
    {
        switch (type)
        {
            case SignalType.BrowserUrl:
                if (TryStr(payload, "note", out string? note) && note == "url_unreadable")
                    return "browser_unreadable";
                if (TryStr(payload, "url", out string? url) && !string.IsNullOrEmpty(url))
                {
                    string host = HostOf(url);
                    // 与 RiskModel 共用按 DNS 标签的匹配(避免 richardbard→bard 等子串误标),判据与标签不漂移。
                    if (RiskModel.HostMatchesAny(host, AiHosts)) return "web_ai";
                    if (RiskModel.HostMatchesAny(host, SearchHosts)) return "search";
                }
                return "non_whitelist_web";   // 非 AI/非搜索的非白名单站:中性标签,别误贴 web_ai
            case SignalType.ProcessStart: return "non_whitelist_proc";
            case SignalType.Clipboard:    return "large_paste";
            case SignalType.Usb:          return "usb";
            // M5 采集端硬化:入队的健康信号给专属 kind(看板可辨识)。
            case SignalType.ScreenshotObscured: return "screen_obscured";
            case SignalType.CapabilityDegraded: return "capability_degraded";
            case SignalType.WatchdogRestart:    return "watchdog_restart";
            case SignalType.SuspectedSuspend:   return "suspected_suspend";   // M8:统一经「采集健康」面板呈现
            default:                      return "suspect";
        }
    }

    /// 健康信号(采集端硬化三件套)入队时归属 source='health',仅在「采集健康」面板呈现、不可裁决;
    /// 其余作弊线索默认 source='suspicion'。判据与 KindFor 的三分支保持一致,避免漂移。
    public static string SourceForKind(string kind)
    {
        return kind is "screen_obscured" or "capability_degraded" or "watchdog_restart" or "suspected_suspend"
            ? "health" : "suspicion";
    }

    private static string HostOf(string url)
    {
        // 解析失败返回空串(与 RiskModel.HostOf 一致):不拿整条 URL 去撞黑名单标签,免 path/query 假阳性(F5)。
        try { return new Uri(url).Host.ToLowerInvariant(); }
        catch { return ""; }
    }

    private static bool TryStr(JsonElement obj, string prop, out string? val)
    {
        val = null;
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.String)
        {
            val = e.GetString();
            return true;
        }
        return false;
    }
}

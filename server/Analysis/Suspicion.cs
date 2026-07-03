using System.Text.Json;
using Horus.Contracts;

namespace Horus.Server.Analysis;

/// M1 服务器侧初筛:由事件类型 + payload 判定可疑类别(kind)。
/// 系统只初筛,处分由人裁决(architecture 铁律 §3)。风险分由 Agent 本地初判,
/// 服务器 risk ≥ 阈值即入 suspicious_queue;M2/M3 再叠加 OCR/Logo/击键。
public static class Suspicion
{
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
            default:                      return "suspect";
        }
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

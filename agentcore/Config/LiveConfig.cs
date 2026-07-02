using System.Text.Json;

namespace Horus.Agent.Config;

/// 运行时可热更新的配置(白名单 / 阈值 / 截图参数)。信号源与抓屏器持有它、每次读取,
/// 服务器下发 config_update 时 Apply(json) 原子替换,下一轮采集即生效。线程安全:
/// 引用型字段整体替换(不就地改集合),int 字段 volatile。
public sealed class LiveConfig
{
    private volatile HashSet<string> _hosts;
    private volatile HashSet<string> _procs;
    private volatile int _largePaste;
    private volatile int _targetHeight;
    private volatile int _webpQuality;
    private volatile int _baselineMin;
    private volatile int _baselineMax;

    public LiveConfig(AgentConfig cfg)
    {
        _hosts = new(cfg.WhitelistHosts.Select(h => h.Trim()), StringComparer.OrdinalIgnoreCase);
        _procs = new(cfg.WhitelistProcs.Select(p => p.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        _largePaste = cfg.LargePasteThreshold;
        _targetHeight = cfg.TargetHeight;
        _webpQuality = cfg.WebpQuality;
        _baselineMin = cfg.BaselineMinSeconds;
        _baselineMax = cfg.BaselineMaxSeconds;
    }

    public bool IsWhitelistedHost(string host) => _hosts.Contains(host);
    public bool IsWhitelistedProc(string procNoExt) => _procs.Contains(procNoExt);
    public int LargePasteThreshold => _largePaste;
    public int TargetHeight => _targetHeight;
    public int WebpQuality => _webpQuality;
    public int BaselineMinSeconds => _baselineMin;
    public int BaselineMaxSeconds => _baselineMax;

    /// 应用服务器下发的配置(camelCase);仅更新出现的字段,并做基本约束保护。
    public void Apply(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object) return;

        if (TryArray(c, "whitelistHosts", out List<string> hosts))
            _hosts = new(hosts, StringComparer.OrdinalIgnoreCase);
        if (TryArray(c, "whitelistProcs", out List<string> procs))
            _procs = new(procs.Select(p => p.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        // 上下限钳制:防止(可能来自被冒用的下发端)极端值造成资源风暴,如 targetHeight=10万、baseline=1s。
        if (TryInt(c, "largePasteThreshold", out int lp)) _largePaste = Math.Clamp(lp, 1, 100_000);
        if (TryInt(c, "targetHeight", out int th)) _targetHeight = Math.Clamp(th, 240, 2160);
        if (TryInt(c, "webpQuality", out int wq)) _webpQuality = Math.Clamp(wq, 1, 100);

        // baseline 区间:只下发一端时不篡改另一端(以现值为界);两端都下发且矛盾才交换。
        int newMin = _baselineMin, newMax = _baselineMax;
        bool minSent = TryInt(c, "baselineMinSeconds", out int bmin);
        bool maxSent = TryInt(c, "baselineMaxSeconds", out int bmax);
        if (minSent) newMin = Math.Clamp(bmin, 5, 3600);
        if (maxSent) newMax = Math.Clamp(bmax, 5, 3600);
        if (minSent && maxSent) { if (newMin > newMax) (newMin, newMax) = (newMax, newMin); }
        else if (minSent) newMin = Math.Min(newMin, newMax);   // 只下发 min:不超过现 max,不动 max
        else if (maxSent) newMax = Math.Max(newMax, newMin);   // 只下发 max:不低于现 min,不动 min
        _baselineMin = newMin;
        _baselineMax = newMax;
    }

    private static bool TryInt(JsonElement o, string key, out int v)
    {
        v = 0;
        return o.TryGetProperty(key, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v);
    }

    private static bool TryArray(JsonElement o, string key, out List<string> v)
    {
        v = new List<string>();
        if (!o.TryGetProperty(key, out JsonElement e) || e.ValueKind != JsonValueKind.Array) return false;
        foreach (JsonElement item in e.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
            {
                string s = item.GetString()!.Trim();   // 去尾随空格:防下发端配置不洁致精确匹配失效、判题站误判非白名单
                if (s.Length > 0) v.Add(s);
            }
        return true;
    }
}

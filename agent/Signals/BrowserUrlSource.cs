using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Horus.Agent.Config;
using Horus.Agent.Model;
using Horus.Contracts;

namespace Horus.Agent.Signals;

/// 读取前台浏览器地址栏 URL —— 本系统对抗网页 AI 的第一防线。
/// 判题站域名白名单放行;出现非白名单 URL → 高风险 + 触发抓图。
/// 注意:UIAutomation 抓地址栏对浏览器版本/语言敏感;隐身/冷门浏览器可能读不到 →
///       降级为"浏览器进程级告警 + 强制抓图"(交人工看截图)。
public sealed class BrowserUrlSource : ISignalSource
{
    public string Name => "browser-url";
    public event Action<RawSignal>? Signal;

    private static readonly string[] BrowserProcs = { "chrome", "msedge", "firefox", "brave", "opera" };
    private const string UnreadableSentinel = "\0url_unreadable\0";   // _lastUrl 的"不可读"哨兵,用于去重

    private readonly LiveConfig _live;                 // 白名单可热更新
    private readonly TimeSpan _interval;
    private readonly System.Threading.Timer _timer;   // 显式限定:UseWindowsForms 注入了 Forms.Timer 造成歧义
    private string _lastUrl = "";

    // 地址栏元素缓存(BUG#3 加固):同前台窗口(hwnd)复用已定位的地址栏 AutomationElement,
    // 跳过每次全树 UIA 遍历,降低 CPU 开销与读错概率;窗口切换/缓存失效才重新探测。
    private IntPtr _cachedHwnd;
    private AutomationElement? _addressBarCache;

    public BrowserUrlSource(LiveConfig live, TimeSpan? interval = null)
    {
        _live = live;
        _interval = interval ?? TimeSpan.FromSeconds(2);
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.Zero, _interval);
    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    private void Poll()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) { InvalidateCache(); return; }

        GetWindowThreadProcessId(hwnd, out uint pid);
        string proc;
        try { proc = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
        catch { InvalidateCache(); return; }
        if (Array.IndexOf(BrowserProcs, proc) < 0) { InvalidateCache(); return; }   // 前台不是浏览器

        string? url = TryReadAddressBar(hwnd);

        if (string.IsNullOrEmpty(url))
        {
            // 是浏览器但读不到 URL → 降级告警 + 抓图。**只在进入"不可读"状态时发一次**,
            // 否则每 poll(约 2s)都发会刷爆 events + 可疑队列;持续期由随机基线抓图覆盖。
            if (_lastUrl == UnreadableSentinel) return;
            _lastUrl = UnreadableSentinel;
            Signal?.Invoke(new RawSignal(SignalType.BrowserUrl,
                new() { ["process"] = proc, ["url"] = null, ["note"] = "url_unreadable" },
                Risk: RiskScores.BrowserUrlUnreadable, TriggerCapture: true, CaptureReason: "browser_url_unreadable"));
            return;
        }

        if (url == _lastUrl) return;
        _lastUrl = url;

        bool whitelisted = IsWhitelisted(url);
        Signal?.Invoke(new RawSignal(SignalType.BrowserUrl,
            new() { ["process"] = proc, ["url"] = url, ["whitelisted"] = whitelisted },
            Risk: whitelisted ? 0 : RiskScores.BrowserUrlNonWhitelist,
            TriggerCapture: !whitelisted,
            CaptureReason: whitelisted ? null : "browser_non_whitelist_url"));
    }

    private bool IsWhitelisted(string url)
    {
        try { return _live.IsWhitelistedHost(new Uri(url).Host); }
        catch { return false; }
    }

    /// 清空地址栏缓存(前台窗口切换 / 进程非浏览器 / 读取失败时)。
    private void InvalidateCache()
    {
        _cachedHwnd = IntPtr.Zero;
        _addressBarCache = null;
    }

    /// 地址栏定位加固(BUG#3):同 hwnd 复用缓存元素,跳过全树遍历;缓存失效才按「toolbar→edit」窄条件重探,
    /// 失败再放宽到全树(保持原行为兜底),仍读不到则清空缓存下次重探。
    private string? TryReadAddressBar(IntPtr hwnd)
    {
        // 1) 复用缓存:同前台窗口且缓存有效 → 直接读值(廉价,免 UIA 遍历)。
        if (_cachedHwnd == hwnd && _addressBarCache is not null)
        {
            string? v = ReadValue(_addressBarCache);
            if (v is not null) return v;
            // 缓存元素已失效(窗口结构变)→ 下方重探。
        }

        // 2) 探测:先窄后宽。
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) { InvalidateCache(); return null; }

            AutomationElement? addr = FindAddressBar(root);
            if (addr is not null)
            {
                _cachedHwnd = hwnd;
                _addressBarCache = addr;
                return ReadValue(addr);
            }
        }
        catch { /* UIA 偶发异常,跳过本次 */ }
        InvalidateCache();
        return null;
    }

    /// 先在前台窗口的 toolbar 容器后代里找 Edit(地址栏通常位于工具栏内),命中即返回;
    /// 找不到(冷门/隐身结构)再退化为全树 Edit 遍历(保持原行为 · 兜底)。两路都收窄到「像 URL」的 Edit,
    /// 避免把数值输入框等误当地址栏。
    private static AutomationElement? FindAddressBar(AutomationElement root)
    {
        var editCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
        var toolbarCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar);
        foreach (AutomationElement tb in root.FindAll(TreeScope.Descendants, toolbarCond))
        {
            foreach (AutomationElement e in tb.FindAll(TreeScope.Descendants, editCond))
                if (LooksLikeUrl(e)) return e;
        }
        // 退化:全树(原逻辑),并收窄到「像 URL」的 Edit。
        foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, editCond))
            if (LooksLikeUrl(e)) return e;
        return null;
    }

    /// 控件值「像 URL」:非空、且以 http 开头或含 '.'。(与旧逻辑一致,集中于此便于单测/复用)
    private static bool LooksLikeUrl(AutomationElement e)
    {
        string? v = ReadValue(e);
        return v is not null && (v.StartsWith("http", StringComparison.OrdinalIgnoreCase) || v.Contains('.'));
    }

    private static string? ReadValue(AutomationElement e)
    {
        if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && p is ValuePattern vp)
        {
            string val = vp.Current.Value;
            if (!string.IsNullOrWhiteSpace(val)) return Normalize(val);
        }
        return null;
    }

    private static string Normalize(string raw)
        => raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? raw : "http://" + raw;

    public void Dispose() => _timer.Dispose();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}

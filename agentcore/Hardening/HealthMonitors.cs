namespace Horus.Agent.Hardening;

/// M5 防挂起:采集循环每轮喂进当前 wall-clock(Unix 秒),若相邻两次跳变远超期望间隔 → 判定进程曾被
/// suspend / 系统睡眠 / 锁屏(Task.Delay 与进程一同被挂起,恢复后 gap = 间隔 + 挂起时长)。纯逻辑·可单测。
public sealed class SuspendMonitor
{
    private readonly double _expectedIntervalSec;
    private readonly double _toleranceFactor;
    private double _lastSec;
    private bool _primed;

    /// expectedIntervalSec:喂入节奏(如心跳 30s);toleranceFactor:超过 期望×此 才算挂起(默认 3×,躲开正常抖动)。
    public SuspendMonitor(double expectedIntervalSec, double toleranceFactor = 3.0)
    {
        _expectedIntervalSec = expectedIntervalSec <= 0 ? 1 : expectedIntervalSec;
        _toleranceFactor = toleranceFactor < 1 ? 1 : toleranceFactor;
    }

    /// 观测一次 wall-clock;若检出挂起返回 gap 毫秒,否则 null。首次调用只定基线。
    public double? Observe(double nowSec)
    {
        if (!_primed) { _primed = true; _lastSec = nowSec; return null; }
        double gap = nowSec - _lastSec;
        _lastSec = nowSec;
        double threshold = _expectedIntervalSec * _toleranceFactor;
        return gap > threshold ? gap * 1000.0 : (double?)null;
    }

    public double ThresholdMs => _expectedIntervalSec * _toleranceFactor * 1000.0;
}

/// 采集能力健康度:各信号源(admin / etw / uia / wmi)连续失败达阈值才判"降级"(防单次瞬时失败抖动误报)。纯逻辑·可单测。
public sealed class CapabilityTracker
{
    private readonly int _threshold;
    private readonly Dictionary<string, int> _consecFails = new(StringComparer.Ordinal);
    private readonly HashSet<string> _degraded = new(StringComparer.Ordinal);

    public CapabilityTracker(int consecutiveFailThreshold = 3)
        => _threshold = consecutiveFailThreshold < 1 ? 1 : consecutiveFailThreshold;

    /// 记一次成功:清零计数;若此前降级则返回 true(表示"恢复",调用方可上报恢复)。
    public bool RecordSuccess(string capability)
    {
        _consecFails[capability] = 0;
        return _degraded.Remove(capability);
    }

    /// 记一次失败:连续失败达阈值且此前未降级 → 返回 true(表示"刚跨入降级",调用方上报一次 capability_degraded)。
    public bool RecordFailure(string capability)
    {
        int n = _consecFails.TryGetValue(capability, out int v) ? v + 1 : 1;
        _consecFails[capability] = n;
        if (n >= _threshold && _degraded.Add(capability)) return true;   // 刚跨入降级(Add 返回 true 表示原不在集合)
        return false;
    }

    public bool IsDegraded(string capability) => _degraded.Contains(capability);
    public IReadOnlyCollection<string> Degraded => _degraded;
}

/// M5 保活取证:判定本次启动是否"异常重启"(上次进程被结束而非正常退出)。
/// 约定:启动时写标记 "running";正常退出写 "clean"。启动读到旧标记仍是 "running" → 上次异常终止(被杀 / 崩溃 / 断电)。
/// 纯逻辑(FS 读写由 Agent 做),此处只判定。
public static class RestartClassifier
{
    public const string Running = "running";
    public const string Clean = "clean";

    /// prevMarker = 启动时读到的旧标记内容(无文件传 null)。返回是否异常重启。
    /// 首次部署(无标记)不算异常。
    public static bool IsUnexpectedRestart(string? prevMarker)
        => string.Equals(prevMarker?.Trim(), Running, StringComparison.Ordinal);
}

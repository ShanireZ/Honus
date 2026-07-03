namespace Horus.Agent.Hardening;

/// M5 防遮蔽:从**廉价像素统计**判断截图是否被遮蔽(黑屏 / 纯色覆盖 / DRM 保护窗口黑帧 / 尺寸异常)。
/// **分类器与采集解耦**:Agent 端从 Bitmap 算出 luma 方差 + 尺寸喂进来,此处纯逻辑判定 → 可单测。
/// 哲学:只上报"疑似遮蔽",不阻断;第二显示器/虚拟桌面仍是结构盲区(靠物理监考)。
public enum ObscureReason
{
    None,
    BadSize,     // 宽/高 ≤ 0(截图失败)
    TooSmall,    // 远小于预期分辨率(疑似截到极小窗口 / 采集异常)
    Solid,       // luma 方差≈0 → 近纯色(全黑 / 单色覆盖 / DRM 黑帧)
    LowEntropy,  // luma 方差很低 → 画面几乎无内容(疑似大面积遮挡)
}

/// 一张截图的廉价统计量(0–255 luma 尺度)。DistinctBuckets = 下采样直方图非空桶数(可选·0 表示未提供)。
public readonly record struct ScreenStats(int Width, int Height, double LumaVariance, int DistinctBuckets = 0);

/// 判定阈值(可从配置热更新)。默认保守:只稳抓"近纯色 / 坏尺寸 / 过小",避免误伤深色 IDE(有文字→方差不低)。
/// **引用类型 record**(非 record struct):`new()` 会走主构造函数默认值;record struct 的 `new()` 是零值会绕过默认参数。
public sealed record ScreenQualityThresholds(
    double SolidVariance = 2.0,     // ≤ 此值视为纯色
    double LowEntropyVariance = 8.0, // < 此值视为低熵(仍很保守·深色带代码的截图方差远高于此)
    int MinEdge = 240);              // 宽或高 < 此值视为过小

public static class ScreenQuality
{
    public static readonly ScreenQualityThresholds Default = new();

    /// 判定遮蔽原因;None = 正常。
    public static ObscureReason Classify(ScreenStats s, ScreenQualityThresholds? t = null)
    {
        ScreenQualityThresholds th = t ?? Default;
        if (s.Width <= 0 || s.Height <= 0) return ObscureReason.BadSize;
        if (s.Width < th.MinEdge || s.Height < th.MinEdge) return ObscureReason.TooSmall;
        if (s.LumaVariance <= th.SolidVariance) return ObscureReason.Solid;
        if (s.LumaVariance < th.LowEntropyVariance) return ObscureReason.LowEntropy;
        return ObscureReason.None;
    }

    public static bool IsObscured(ScreenStats s, ScreenQualityThresholds? t = null)
        => Classify(s, t) != ObscureReason.None;

    /// 契约用小写标签(进事件 payload.reason)。
    public static string ReasonLabel(ObscureReason r) => r switch
    {
        ObscureReason.BadSize => "bad_size",
        ObscureReason.TooSmall => "too_small",
        ObscureReason.Solid => "solid",
        ObscureReason.LowEntropy => "low_entropy",
        _ => "none",
    };
}

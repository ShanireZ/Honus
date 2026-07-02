using System.Globalization;
using Horus.Server.Config;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Horus.Server.Analysis.Vision;

/// §5 隐私收口:送云视觉 LLM **之前**对截图做派生处理 —— **打码身份**(遮住学员姓名/学号矩形)+ **裁剪**(只留可疑/浏览器区)
/// + **降采样**(压 token/成本、少送无关像素),再重编码 WebP。**原图字节只读,原图永不出网**;送云的只有本方法产出的派生字节。
///
/// 矩形都用**归一化坐标**(x,y,w,h ∈ [0,1]·分辨率无关):打码可多个(`;` 分隔),裁剪单个。
/// 打码在**裁剪前**(坐标相对原图,稳定)。无任何收口配置 → 直通原字节(不解码,零开销,mock 测试也走此路)。
public static class VisionImagePrep
{
    /// 返回派生字节;无配置直通原字节;**打码有配置却解码失败 → 返回 null(宁跳过也不泄漏身份)**。
    public static byte[]? Prepare(byte[] original, ServerConfig cfg)
    {
        List<(float x, float y, float w, float h)> redacts = ParseRects(cfg.VisionRedactRects);
        bool hasRedact = redacts.Count > 0;
        bool hasCrop = TryRect(cfg.VisionCropRect, out (float x, float y, float w, float h) cropN);
        bool hasResize = cfg.VisionMaxEdge > 0;

        if (!hasRedact && !hasCrop && !hasResize) return original;   // 无收口 → 直通(不解码)

        try
        {
            using var inMs = new MemoryStream(original);
            using Image<Rgba32> image = Image.Load<Rgba32>(inMs);

            foreach ((float x, float y, float w, float h) nr in redacts) RedactNorm(image, nr);   // 先打码(相对原图)
            if (hasCrop) CropNorm(image, cropN);                                                  // 再裁剪
            if (hasResize && Math.Max(image.Width, image.Height) > cfg.VisionMaxEdge)             // 再降采样(长边上限)
                image.Mutate(m => m.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(cfg.VisionMaxEdge, cfg.VisionMaxEdge) }));

            using var outMs = new MemoryStream();
            image.SaveAsWebp(outMs, new WebpEncoder { Quality = 75 });
            return outMs.ToArray();
        }
        catch
        {
            return hasRedact ? null : original;   // 要打码却解不开 → 跳过(不泄漏);否则回退原图
        }
    }

    private static void RedactNorm(Image<Rgba32> img, (float x, float y, float w, float h) nr)
    {
        Rectangle rect = ToPixels(nr, img.Width, img.Height);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        img.ProcessPixelRows(accessor =>
        {
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = rect.Left; x < rect.Right; x++) row[x] = new Rgba32(0, 0, 0, 255);   // 实心黑遮挡
            }
        });
    }

    private static void CropNorm(Image<Rgba32> img, (float x, float y, float w, float h) cn)
    {
        Rectangle rect = ToPixels(cn, img.Width, img.Height);
        if (rect.Width > 0 && rect.Height > 0 && rect != img.Bounds) img.Mutate(m => m.Crop(rect));
    }

    private static Rectangle ToPixels((float x, float y, float w, float h) n, int W, int H)
    {
        var r = new Rectangle(
            (int)Math.Round(n.x * W), (int)Math.Round(n.y * H),
            (int)Math.Round(n.w * W), (int)Math.Round(n.h * H));
        return Rectangle.Intersect(r, new Rectangle(0, 0, W, H));
    }

    private static List<(float, float, float, float)> ParseRects(string? s)
    {
        var list = new List<(float, float, float, float)>();
        if (string.IsNullOrWhiteSpace(s)) return list;
        foreach (string part in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (TryRect(part, out (float x, float y, float w, float h) r)) list.Add(r);
        return list;
    }

    private static bool TryRect(string? s, out (float x, float y, float w, float h) r)
    {
        r = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        string[] p = s.Split(',');
        if (p.Length != 4) return false;
        if (F(p[0], out float x) && F(p[1], out float y) && F(p[2], out float w) && F(p[3], out float h))
        {
            r = (x, y, w, h);
            return true;
        }
        return false;

        static bool F(string t, out float v) => float.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}

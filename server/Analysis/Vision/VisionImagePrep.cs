using Horus.Server.Config;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Horus.Server.Analysis.Vision;

/// §5 送云前的派生处理 —— **降采样**(压 token/成本、少送无关像素)+ **剥离元数据**(EXIF/XMP/ICC 不随派生图出网),
/// 再重编码 WebP。**原图字节只读,只送本方法产出的派生字节**。
///
/// 注(owner 决策·2026-07-02):**不再做打码身份 / 裁剪**(按考场 UI 逐一配矩形的运维负担 > 收益,且供应商=小米 MiMo 境内云·PIPL 无跨境)。
/// 只保留分辨率无关的降采样;`visionMaxEdge` 长边上限,`≤0` 则直通原字节(不解码,零开销,mock 测试走此路)。
public static class VisionImagePrep
{
    /// 返回派生字节;`visionMaxEdge≤0` 直通原字节;解码失败回退原字节(无隐私矩形可失守,直通即可)。
    public static byte[]? Prepare(byte[] original, ServerConfig cfg)
    {
        if (cfg.VisionMaxEdge <= 0) return original;   // 不降采样 → 直通(不解码)

        try
        {
            using var inMs = new MemoryStream(original);
            using Image<Rgba32> image = Image.Load<Rgba32>(inMs);

            // 剥离元数据:派生图只含像素,不随源图 EXIF/XMP/IPTC/ICC 出网(#15)。
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.IccProfile = null;

            if (Math.Max(image.Width, image.Height) > cfg.VisionMaxEdge)   // 长边超上限才降采样
                image.Mutate(m => m.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(cfg.VisionMaxEdge, cfg.VisionMaxEdge),
                }));

            using var outMs = new MemoryStream();
            image.SaveAsWebp(outMs, new WebpEncoder { Quality = 75 });
            return outMs.ToArray();
        }
        catch
        {
            return original;   // 解码失败 → 直通原字节(无打码矩形需守护)
        }
    }
}

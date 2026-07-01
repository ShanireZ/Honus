using System.Drawing;
using System.Drawing.Imaging;
using Honus.Agent.Config;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using IsImage = SixLabors.ImageSharp.Image;

namespace Honus.Agent.Capture;

/// 抓屏 → 缩放到目标高度 → WebP 编码 → pHash →(可选去重)→ 交上传委托,返回 imageId。
/// 抓图串行化(SemaphoreSlim),避免并发触发时 _lastPhash 竞争。
public sealed class ScreenshotCapturer : IDisposable
{
    private readonly LiveConfig _live;                                     // 目标高度 / WebP 质量可热更新
    private readonly Func<byte[], string, ulong, Task<string?>> _upload;   // (webp, trigger, phash) => imageId
    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly TimeSpan _minGap = TimeSpan.FromMilliseconds(1500);   // 触发型连发去抖
    private DateTime _lastUtc = DateTime.MinValue;
    private ulong _lastPhash;
    private bool _hasLast;

    public ScreenshotCapturer(LiveConfig live, Func<byte[], string, ulong, Task<string?>> upload)
    {
        _live = live;
        _upload = upload;
    }

    /// dedupAgainstLast=true:对静止画面做 pHash 去重(随机基线层传 false——每张都要留)。
    public async Task<string?> CaptureAsync(string trigger, bool dedupAgainstLast)
    {
        await _sem.WaitAsync().ConfigureAwait(false);
        try
        {
            if (dedupAgainstLast && DateTime.UtcNow - _lastUtc < _minGap) return null;
            _lastUtc = DateTime.UtcNow;

            byte[] webp;
            ulong phash;
            using (Bitmap bmp = GrabPrimaryScreen())
            using (IsImage img = ToImageSharp(bmp))
            {
                Downscale(img, _live.TargetHeight);
                phash = PerceptualHash.DHash(img);
                if (dedupAgainstLast && _hasLast && PerceptualHash.Hamming(phash, _lastPhash) <= 3)
                    return null;                                  // 与上一张几乎一致,丢弃
                _lastPhash = phash;
                _hasLast = true;
                webp = EncodeWebp(img, _live.WebpQuality);
            }
            return await _upload(webp, trigger, phash).ConfigureAwait(false);
        }
        finally { _sem.Release(); }
    }

    private static Bitmap GrabPrimaryScreen()
    {
        // TODO: 多显示器 → 遍历 Screen.AllScreens 各抓一张(覆盖"第二显示器"盲区)。
        var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size);
        return bmp;
    }

    private static IsImage ToImageSharp(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;
        return IsImage.Load(ms);
    }

    private static void Downscale(IsImage img, int targetHeight)
    {
        if (img.Height <= targetHeight) return;
        int w = (int)Math.Round(img.Width * (double)targetHeight / img.Height);
        img.Mutate(x => x.Resize(w, targetHeight));
    }

    private static byte[] EncodeWebp(IsImage img, int quality)
    {
        using var ms = new MemoryStream();
        img.Save(ms, new WebpEncoder { Quality = quality, FileFormat = WebpFileFormatType.Lossy });
        return ms.ToArray();
    }

    public void Dispose() => _sem.Dispose();
}

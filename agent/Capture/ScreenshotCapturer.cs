using System.Drawing;
using System.Drawing.Imaging;
using Horus.Agent.Config;
using Horus.Agent.Hardening;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using IsImage = SixLabors.ImageSharp.Image;

namespace Horus.Agent.Capture;

/// 抓屏 → 缩放到目标高度 → WebP 编码 → pHash →(可选去重)→ 交上传委托,返回主屏 imageId。
/// 抓图串行化(SemaphoreSlim),避免并发触发时去重状态竞争。
/// 多显示器(BUG#4):遍历 Screen.AllScreens 每张都抓一张(覆盖"第二显示器"盲区);主屏 imageId 作为事件关联锚点。
public sealed class ScreenshotCapturer : IDisposable
{
    private readonly LiveConfig _live;                                     // 目标高度 / WebP 质量可热更新
    private readonly Func<byte[], string, ulong, Task<string?>> _upload;   // (webp, trigger, phash) => imageId
    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly TimeSpan _minGap = TimeSpan.FromMilliseconds(1500);   // 触发型连发去抖
    private DateTime _lastUtc = DateTime.MinValue;
    // 每屏独立去重状态(按设备名 key,随显示器插拔动态):避免多屏互相干扰。
    private readonly Dictionary<string, (ulong Phash, bool Has)> _lastByScreen = new();

    /// M5 防遮蔽:每帧算出廉价 luma 统计喂给此回调(Program 接 ScreenQuality 分类 → 疑似遮蔽则上报)。可空=不检测。
    public Action<ScreenStats>? OnStats { get; set; }

    public ScreenshotCapturer(LiveConfig live, Func<byte[], string, ulong, Task<string?>> upload)
    {
        _live = live;
        _upload = upload;
    }

    /// dedupAgainstLast=true:对静止画面做 pHash 去重(随机基线层传 false——每张都要留)。
    /// 多显示器:每张屏各抓一张并分别上传;主屏(Primary)的 imageId 作为事件关联锚点返回。
    public async Task<string?> CaptureAsync(string trigger, bool dedupAgainstLast)
    {
        await _sem.WaitAsync().ConfigureAwait(false);
        try
        {
            if (dedupAgainstLast && DateTime.UtcNow - _lastUtc < _minGap) return null;
            _lastUtc = DateTime.UtcNow;

            // 多显示器:每张屏都抓(覆盖第二屏盲区 · BUG#4)。
            var screens = System.Windows.Forms.Screen.AllScreens;
            string? primaryId = null;
            bool statsReported = false;
            for (int i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                byte[] webp;
                ulong phash;
                using (Bitmap bmp = GrabScreen(screen))
                using (IsImage img = ToImageSharp(bmp))
                {
                    Downscale(img, _live.TargetHeight);
                    // M5:每帧算 luma 统计喂遮蔽检测。仅主屏喂(保持原 M5 语义:单路遮蔽判定),检测失败不影响主采集。
                    if (OnStats is not null && !statsReported && screen.Primary)
                        try { OnStats(ComputeLumaStats(img)); statsReported = true; } catch { /* 遮蔽检测异常吞掉 */ }
                    phash = PerceptualHash.DHash(img);

                    // 每屏独立去重(静止画面跳过),避免多屏互相干扰;key=设备名(随插拔动态)。
                    string key = screen.DeviceName;
                    _lastByScreen.TryGetValue(key, out var last);
                    if (dedupAgainstLast && last.Has && PerceptualHash.Hamming(phash, last.Phash) <= 3)
                        continue;   // 该屏静止,跳过上传(继续抓其余屏)
                    _lastByScreen[key] = (phash, true);

                    webp = EncodeWebp(img, _live.WebpQuality);
                }
                // 多屏时在 trigger 追加 :screen{i},服务端 isEvent 判定(StartsWith "event:")与基线抽样不受影响。
                string? id = await _upload(webp, trigger + (screens.Length > 1 ? $":screen{i}" : ""), phash).ConfigureAwait(false);
                if (screen.Primary) primaryId = id;     // 主屏作为事件关联锚点
                if (primaryId is null) primaryId = id;  // 无主屏时兜底
            }
            return primaryId;
        }
        finally { _sem.Release(); }
    }

    private static Bitmap GrabScreen(System.Windows.Forms.Screen screen)
    {
        // 多显示器:用各屏 Bounds(虚拟屏幕坐标)抓取,覆盖第二屏盲区(BUG#4)。
        var b = screen.Bounds;
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

    /// 廉价 luma 统计:降到 32×32 灰度算方差(近纯色→方差≈0)。W/H 取真实截图尺寸(判坏尺寸/过小)。
    private static ScreenStats ComputeLumaStats(IsImage img)
    {
        int w = img.Width, h = img.Height;
        using Image<L8> g = img.CloneAs<L8>();
        g.Mutate(x => x.Resize(32, 32));
        double sum = 0, sumSq = 0;
        int n = 0;
        g.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                Span<L8> row = acc.GetRowSpan(y);
                for (int i = 0; i < row.Length; i++) { double v = row[i].PackedValue; sum += v; sumSq += v * v; n++; }
            }
        });
        double mean = n > 0 ? sum / n : 0;
        double variance = n > 0 ? Math.Max(0, sumSq / n - mean * mean) : 0;
        return new ScreenStats(w, h, variance);
    }

    private static byte[] EncodeWebp(IsImage img, int quality)
    {
        using var ms = new MemoryStream();
        img.Save(ms, new WebpEncoder { Quality = quality, FileFormat = WebpFileFormatType.Lossy });
        return ms.ToArray();
    }

    public void Dispose() => _sem.Dispose();
}

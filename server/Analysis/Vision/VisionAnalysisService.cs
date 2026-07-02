using System.Text.Json;
using System.Threading.Channels;
using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Analysis.Vision;

/// 异步视觉分析后台服务:图入库后入队,后台线程逐张送 IVisionAnalyzer 判定 —— **不占 ingest 热路径**
/// (视觉调用可能几秒)。判定命中 → 落 ocr_results + 该图标证据 + 引用它的事件抬 server_risk + 入可疑队列。
/// 未配置分析器(cfg 关)时整链 no-op,采集完全不受影响。
///
/// **不丢证据**:入队用**原子占位**(uploaded_to_ocr 0→1 抢到才分析)杜绝重复分析/重复入队;队列满 / 服务器重启丢内存队列时,
/// **补偿重扫**(VisionBackstopMinutes)周期性拾回 uploaded_to_ocr=0 的触发型证据图重新入队 —— 触发型证据不会被静默丢分析。
public sealed class VisionAnalysisService : BackgroundService
{
    private sealed record ImgMeta(string Exam, string Seat, string Trigger, string File);

    private readonly Db _db;
    private readonly Storage _storage;
    private readonly ServerConfig _cfg;
    private readonly ILogger<VisionAnalysisService> _log;
    private readonly IVisionAnalyzer? _analyzer;
    private readonly Channel<string> _queue =
        Channel.CreateBounded<string>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private long _rejected;   // 队列满时被拒的入队数(补偿重扫会拾回);周期性告警

    public VisionAnalysisService(Db db, Storage storage, ServerConfig cfg, IServiceProvider sp, ILogger<VisionAnalysisService> log)
    {
        _db = db; _storage = storage; _cfg = cfg; _log = log;
        _analyzer = sp.GetService<IVisionAnalyzer>();   // 视觉关时未注册 → null → 整链 no-op
    }

    public bool Enabled => _analyzer is not null;

    /// 入队待分析图(§5 最小化:只送需要文字/语义判定的图)。视觉关或空 id → no-op。
    /// 队列满时 TryWrite 返回 false(不阻塞 ingest 热路径):计数告警,由补偿重扫拾回,不静默丢证据。
    public void Enqueue(string imageId)
    {
        if (_analyzer is null || string.IsNullOrEmpty(imageId)) return;
        if (!_queue.Writer.TryWrite(imageId)) Interlocked.Increment(ref _rejected);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_analyzer is null) return;
        _log.LogInformation("视觉分析已启用 engine={Engine} 阈值={Th}", _analyzer.Engine, _cfg.VisionConfidenceThreshold);
        Task backstop = RunBackstopAsync(ct);
        try
        {
            await foreach (string imageId in _queue.Reader.ReadAllAsync(ct))
            {
                try { await AnalyzeOneAsync(imageId, ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "视觉分析异常 image={Image}", imageId); }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
        try { await backstop; } catch { /* 停机 */ }
    }

    /// 补偿重扫:周期性拾回 uploaded_to_ocr=0 的**触发型**证据图(被队列拒收 / 服务器重启丢内存队列的)重新入队。
    /// 原子占位保证重入队幂等(正在分析的会占位失败,不重复分析)。VisionBackstopMinutes≤0 关闭。
    private async Task RunBackstopAsync(CancellationToken ct)
    {
        if (_cfg.VisionBackstopMinutes <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_cfg.VisionBackstopMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                long dropped = Interlocked.Exchange(ref _rejected, 0);
                if (dropped > 0) _log.LogWarning("视觉队列曾满,{N} 张图被拒入队(将由补偿重扫拾回)", dropped);

                var ids = _db.Read(conn =>
                {
                    var list = new List<string>();
                    using SqliteCommand c = conn.Cmd(
                        "SELECT image_id FROM images WHERE uploaded_to_ocr=0 AND trigger LIKE 'event:%' ORDER BY recv_ts LIMIT 500");
                    using SqliteDataReader r = c.ExecuteReader();
                    while (r.Read()) list.Add(r.GetString(0));
                    return list;
                });
                int requeued = 0;
                foreach (string id in ids) if (_queue.Writer.TryWrite(id)) requeued++; else break;   // 满了下轮再拾
                if (requeued > 0) _log.LogInformation("视觉补偿重扫:拾回 {N} 张未分析触发型证据图", requeued);
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    private async Task AnalyzeOneAsync(string imageId, CancellationToken ct)
    {
        // **原子占位**:UPDATE uploaded_to_ocr 0→1 抢到(rowcount=1)才分析,顺带取元数据 —— 单条写事务内完成,
        // 杜绝"读→分析→写"跨 await 的 TOCTOU(重复分析 / 重复入可疑队列)。抢不到=已分析或正被别的执行流占用。
        ImgMeta? meta = _db.Write<ImgMeta?>(conn =>
        {
            using (SqliteCommand claim = conn.Cmd(
                "UPDATE images SET uploaded_to_ocr=1 WHERE image_id=@id AND uploaded_to_ocr=0", ("@id", imageId)))
                if (claim.ExecuteNonQuery() == 0) return null;   // 已处理/占用 → 跳过
            using SqliteCommand c = conn.Cmd(
                "SELECT exam_id,seat_id,trigger,file_path FROM images WHERE image_id=@id", ("@id", imageId));
            using SqliteDataReader r = c.ExecuteReader();
            return r.Read() ? new ImgMeta(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)) : null;
        });
        if (meta is null) return;

        string? full = _storage.Resolve(meta.File);
        if (full is null || !File.Exists(full)) return;   // 文件已归档/清理 → 已占位,不重试
        byte[] original = await File.ReadAllBytesAsync(full, ct);

        // §5 送云前降采样 + 剥离元数据,只送派生字节(原图字节只读、永不出网)。
        byte[] derived = VisionImagePrep.Prepare(original, _cfg) ?? original;

        VisionVerdict? v = await _analyzer!.AnalyzeAsync(
            derived, new VisionContext(meta.Exam, meta.Seat, imageId, meta.Trigger), ct);
        if (v is null) return;   // 分析失败(已占位·fail-open 不重试,与原行为一致;云端错误由 adapter 记日志)

        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        _db.Write(conn =>
        {
            // 落 ocr_results(engine/text/hits/confidence);同图幂等
            using (SqliteCommand ins = conn.Cmd(
                @"INSERT INTO ocr_results (image_id,engine,text,hits,confidence,created_at)
                  VALUES (@id,@eng,@txt,@hits,@conf,@ts) ON CONFLICT(image_id) DO NOTHING",
                ("@id", imageId), ("@eng", _analyzer!.Engine), ("@txt", v.Text),
                ("@hits", JsonSerializer.Serialize(v.Hits)), ("@conf", (double)v.Confidence), ("@ts", now)))
                ins.ExecuteNonQuery();

            if (!v.Suspicious || v.Confidence < _cfg.VisionConfidenceThreshold) return;

            // 命中 → 该图标证据;引用它的触发型事件抬 server_risk(与元数据信号取 max);入可疑队列
            using (SqliteCommand ev = conn.Cmd("UPDATE images SET is_evidence=1 WHERE image_id=@id", ("@id", imageId)))
                ev.ExecuteNonQuery();

            long? eventId = null;
            using (SqliteCommand fe = conn.Cmd("SELECT id FROM events WHERE evidence_image_id=@id LIMIT 1", ("@id", imageId)))
            {
                object? o = fe.ExecuteScalar();
                if (o is not null and not DBNull) eventId = Convert.ToInt64(o);
            }
            if (eventId is not null)
                using (SqliteCommand br = conn.Cmd(
                    "UPDATE events SET server_risk=MAX(COALESCE(server_risk,0),@c) WHERE id=@eid",
                    ("@c", v.Confidence), ("@eid", eventId.Value)))
                    br.ExecuteNonQuery();

            var refs = new List<string> { $"image:{imageId}" };
            if (eventId is not null) refs.Add($"event:{eventId.Value}");
            using (SqliteCommand q = conn.Cmd(
                @"INSERT INTO suspicious_queue (exam_id,seat_id,ts,kind,score,status,refs,note)
                  VALUES (@e,@s,@ts,@k,@sc,'pending',@refs,@note)",
                ("@e", meta.Exam), ("@s", meta.Seat), ("@ts", now),
                ("@k", v.Kind()), ("@sc", v.Confidence), ("@refs", JsonSerializer.Serialize(refs)),
                ("@note", "vision:" + v.Evidence)))
                q.ExecuteNonQuery();
        });
    }
}

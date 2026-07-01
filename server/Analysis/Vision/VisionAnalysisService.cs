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
public sealed class VisionAnalysisService : BackgroundService
{
    private sealed record ImgMeta(string Exam, string Seat, string Trigger, string File, bool Done);

    private readonly Db _db;
    private readonly Storage _storage;
    private readonly ServerConfig _cfg;
    private readonly ILogger<VisionAnalysisService> _log;
    private readonly IVisionAnalyzer? _analyzer;
    private readonly Channel<string> _queue =
        Channel.CreateBounded<string>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });

    public VisionAnalysisService(Db db, Storage storage, ServerConfig cfg, IServiceProvider sp, ILogger<VisionAnalysisService> log)
    {
        _db = db; _storage = storage; _cfg = cfg; _log = log;
        _analyzer = sp.GetService<IVisionAnalyzer>();   // 视觉关时未注册 → null → 整链 no-op
    }

    public bool Enabled => _analyzer is not null;

    /// 入队待分析图(§5 最小化:只送需要文字/语义判定的图)。视觉关或空 id → no-op。
    public void Enqueue(string imageId)
    {
        if (_analyzer is null || string.IsNullOrEmpty(imageId)) return;
        _queue.Writer.TryWrite(imageId);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_analyzer is null) return;
        _log.LogInformation("视觉分析已启用 engine={Engine} 阈值={Th}", _analyzer.Engine, _cfg.VisionConfidenceThreshold);
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
    }

    private async Task AnalyzeOneAsync(string imageId, CancellationToken ct)
    {
        // 读图元数据(独立只读连接);已分析过(uploaded_to_ocr=1)则幂等跳过。
        ImgMeta? meta = _db.Read<ImgMeta?>(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                "SELECT exam_id,seat_id,trigger,file_path,uploaded_to_ocr FROM images WHERE image_id=@id", ("@id", imageId));
            using SqliteDataReader r = c.ExecuteReader();
            return r.Read() ? new ImgMeta(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) != 0) : null;
        });
        if (meta is null || meta.Done) return;

        string? full = _storage.Resolve(meta.File);
        if (full is null || !File.Exists(full)) return;
        byte[] bytes = await File.ReadAllBytesAsync(full, ct);

        // §5 收口:只送派生字节。**真裁剪浏览器区 / 打码身份留待接真端点时补**(见 architecture §5)—— 现为直传 stub。
        VisionVerdict? v = await _analyzer!.AnalyzeAsync(
            bytes, new VisionContext(meta.Exam, meta.Seat, imageId, meta.Trigger), ct);

        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        _db.Write(conn =>
        {
            // 标记已送分析(隐私审计 + 幂等,重放不重复分析)
            using (SqliteCommand mk = conn.Cmd("UPDATE images SET uploaded_to_ocr=1 WHERE image_id=@id", ("@id", imageId)))
                mk.ExecuteNonQuery();

            if (v is null) return;

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

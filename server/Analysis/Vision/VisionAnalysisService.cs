using System.Collections.Concurrent;
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
    // 在途去重:已入队尚未分析完的 imageId。防 ingest 首送与 backstop 反复把同一张仍在队列里的图重复入队(占满有界队列、
    // 把新图挤掉)。原子占位(uploaded_to_ocr 0→1)保证不重复**分析**,此集合保证不重复**入队**。
    private readonly ConcurrentDictionary<string, byte> _inflight = new();
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
        if (!_inflight.TryAdd(imageId, 0)) return;   // 已在队列/分析中 → 不重复入队
        if (!_queue.Writer.TryWrite(imageId)) { _inflight.TryRemove(imageId, out _); Interlocked.Increment(ref _rejected); }
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
                finally { _inflight.TryRemove(imageId, out _); }   // 出队处理完(成功/跳过/异常/取消)→ 释放在途标记(占位仍防重复分析)
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

                // trigger 过滤须与 ImageIngest 的入队判据一致:开了基线分析则重扫也纳入基线图(否则被拒/丢队的基线图永不拾回,
                // _rejected 告警对基线成虚假承诺);默认只重扫触发型证据图(§5 最小化)。
                string triggerFilter = _cfg.VisionAnalyzeBaseline ? "" : " AND i.trigger LIKE 'event:%'";
                var ids = _db.Read(conn =>
                {
                    var list = new List<string>();
                    // 拾回 analysis_state=0(未终结:临时云失败/被拒队/重启丢队)且 attempts 未超上限的图(闭合 F1)。
                    // 排除归档中/已归档考试的图(不给归档窗口塞新分析结果 → 避免孤儿 pending)。
                    using SqliteCommand c = conn.Cmd(
                        @"SELECT i.image_id FROM images i JOIN exams e ON i.exam_id=e.exam_id
                          WHERE i.analysis_state=0 AND i.analysis_attempts<@max" + triggerFilter + @"
                            AND e.status NOT IN ('archiving','archived')
                          ORDER BY i.recv_ts LIMIT 500", ("@max", _cfg.VisionMaxAttempts));
                    using SqliteDataReader r = c.ExecuteReader();
                    while (r.Read()) list.Add(r.GetString(0));
                    return list;
                });
                int requeued = 0;
                foreach (string id in ids)
                {
                    if (!_inflight.TryAdd(id, 0)) continue;                 // 仍在队列/分析中 → 不重复入队
                    if (_queue.Writer.TryWrite(id)) requeued++;
                    else { _inflight.TryRemove(id, out _); break; }         // 满了下轮再拾
                }
                if (requeued > 0) _log.LogInformation("视觉补偿重扫:拾回 {N} 张未分析触发型证据图", requeued);
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    private async Task AnalyzeOneAsync(string imageId, CancellationToken ct)
    {
        // **原子认领**:取元数据 → 查考试未封存 → UPDATE analysis_attempts+1 抢到(rowcount=1·attempts<上限·未终结)才分析 —— 同一写锁 body 内,
        // 杜绝"读→分析→写"跨 await 的 TOCTOU(重复分析 / 重复入可疑队列),并对归档中/已归档考试短路(不塞孤儿分析)。
        // **认领不置 uploaded_to_ocr**(那是"真出网"隐私审计,仅 SendsOffNetwork 分析器真送出才置)——闩锁改用 analysis_state/attempts,
        // 使临时云失败(v=null 不终结)可由补偿重扫按 attempts<上限 重试,不再永久漏析(闭合 F1/F2)。
        ImgMeta? meta = _db.Write<ImgMeta?>(conn =>
        {
            ImgMeta? m = null;
            using (SqliteCommand c = conn.Cmd(
                "SELECT exam_id,seat_id,trigger,file_path FROM images WHERE image_id=@id", ("@id", imageId)))
            using (SqliteDataReader r = c.ExecuteReader())
                if (r.Read()) m = new ImgMeta(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));
            if (m is null || conn.IsExamSealed(m.Exam)) return null;
            using SqliteCommand claim = conn.Cmd(
                "UPDATE images SET analysis_attempts=analysis_attempts+1 WHERE image_id=@id AND analysis_state=0 AND analysis_attempts<@max",
                ("@id", imageId), ("@max", _cfg.VisionMaxAttempts));
            return claim.ExecuteNonQuery() > 0 ? m : null;   // 抢到才分析;抢不到=已终结/占用/超上限
        });
        if (meta is null) return;

        string? full = _storage.Resolve(meta.File);
        if (full is null || !File.Exists(full)) { MarkTerminal(imageId); return; }   // 文件已归档/清理:确定态 → 终结,不重扫
        byte[] original = await File.ReadAllBytesAsync(full, ct);

        // §5 送云前降采样 + 剥离元数据,只送派生字节(原图字节只读、永不出网)。
        // 联网分析器(SendsOffNetwork=true):无法安全派生(解码失败)→ Prepare 返 null → 终结跳过(确定态·重扫也解不开),绝不泄未剥元数据的原图。
        byte[]? derived = VisionImagePrep.Prepare(original, _cfg, _analyzer!.SendsOffNetwork);
        if (derived is null)
        {
            _log.LogWarning("送云图无法安全派生(解码失败),跳过分析以免泄原图 image={Image}", imageId);
            MarkTerminal(imageId);   // 派生失败是确定态(坏图每次都解不开)→ 终结,免补偿重扫死循环
            return;
        }

        // 隐私审计:仅**联网分析器**在真正送出字节前置 uploaded_to_ocr=1(mock/本地不出网恒 0,不再撒谎)。
        if (_analyzer!.SendsOffNetwork)
            _db.Write(conn => { using SqliteCommand u = conn.Cmd(
                "UPDATE images SET uploaded_to_ocr=1 WHERE image_id=@id", ("@id", imageId)); u.ExecuteNonQuery(); });

        VisionVerdict? v = await _analyzer!.AnalyzeAsync(
            derived, new VisionContext(meta.Exam, meta.Seat, imageId, meta.Trigger), ct);
        if (v is null)
        {
            // 临时失败(超时/5xx/网络/空 content/解析失败):未达上限**不终结**(analysis_state 保持 0)→ 补偿重扫按 attempts<上限 重试(闭合 F1)。
            // 已达上限:置终态(state=1)放弃 —— 否则永久卡 state=0 僵尸态(claim/backstop 都按 attempts<上限 过滤,既不重试也不终结)。
            // 与确定态失败一致:终态但无 ocr_results 行 → 看板/审计可据"state=1 且无 ocr_results"区分"放弃"与"已析干净"。
            int abandoned = _db.Write(conn =>
            {
                using SqliteCommand u = conn.Cmd(
                    "UPDATE images SET analysis_state=1 WHERE image_id=@id AND analysis_state=0 AND analysis_attempts>=@max",
                    ("@id", imageId), ("@max", _cfg.VisionMaxAttempts));
                return u.ExecuteNonQuery();
            });
            if (abandoned > 0)
                _log.LogWarning("视觉分析连续 {N} 次临时失败,放弃 image={Image}(仍留作证据·无视觉判定)", _cfg.VisionMaxAttempts, imageId);
            else
                _log.LogWarning("视觉分析未得结果(临时失败),保留待补偿重扫 image={Image}", imageId);
            return;
        }

        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        _db.Write(conn =>
        {
            // 分析期间(await 云调用)考试可能已进入归档:此刻图行可能已被删,写结果会 FK 失败 / 造孤儿 pending → 短路(不终结,归档已接管)。
            if (conn.IsExamSealed(meta.Exam)) return;

            // 得到判定 → 终结(成功态,不再重扫)。
            using (SqliteCommand fin = conn.Cmd("UPDATE images SET analysis_state=1 WHERE image_id=@id", ("@id", imageId)))
                fin.ExecuteNonQuery();

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

            // F4 去重:若该事件已有 pending 队列项(ingest 期元数据信号入的),不再另插一条视觉重复项——
            // 而是把视觉证据并入该行 note 并把 score 抬到 max(避免同一事同图两条 pending、人工裁决两次、看板计数翻倍)。
            if (eventId is not null)
            {
                long? existing = null;
                using (SqliteCommand fx = conn.Cmd(
                    @"SELECT id FROM suspicious_queue WHERE exam_id=@e AND status='pending' AND refs LIKE @pat LIMIT 1",
                    ("@e", meta.Exam), ("@pat", $"%\"event:{eventId.Value}\"%")))
                {
                    object? o = fx.ExecuteScalar();
                    if (o is not null and not DBNull) existing = Convert.ToInt64(o);
                }
                if (existing is not null)
                {
                    using SqliteCommand up = conn.Cmd(
                        @"UPDATE suspicious_queue SET score=MAX(score,@sc), note=COALESCE(note,'')||@vn WHERE id=@id",
                        ("@sc", v.Confidence), ("@vn", " | vision:" + v.Evidence), ("@id", existing.Value));
                    up.ExecuteNonQuery();
                    return;   // 已合并进既有 pending,不另插
                }
            }

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

    /// 把图标为已终结(analysis_state=1):确定态失败(文件缺失 / 派生失败)不由补偿重扫重试。
    private void MarkTerminal(string imageId)
        => _db.Write(conn => { using SqliteCommand c = conn.Cmd(
            "UPDATE images SET analysis_state=1 WHERE image_id=@id", ("@id", imageId)); c.ExecuteNonQuery(); });
}

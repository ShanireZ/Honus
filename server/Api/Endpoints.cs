using System.Text.Json;
using System.Text.Json.Nodes;
using Honus.Server.Config;
using Honus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Honus.Server.Api;

/// 看板只读 API(/api/exams, /seats, /suspicious, /events, /images) + 管理/复核写 API
/// (建考试、结束考试、可疑裁决)。系统给线索,处分由人裁决。
public static class Endpoints
{
    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public static void MapApi(this WebApplication app)
    {
        Db db = app.Services.GetRequiredService<Db>();
        Storage storage = app.Services.GetRequiredService<Storage>();
        ServerConfig cfg = app.Services.GetRequiredService<ServerConfig>();

        // ---- 考试列表 ----
        app.MapGet("/api/exams", () =>
        {
            double cut = Now() - cfg.OnlineWindowSeconds;
            return db.Locked(conn =>
            {
                var list = new List<object>();
                using SqliteCommand c = conn.Cmd(
                    "SELECT exam_id,name,status,started_at,ended_at FROM exams ORDER BY created_at DESC");
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read())
                {
                    string examId = r.GetString(0);
                    list.Add(new
                    {
                        examId,
                        name = r.GetString(1),
                        status = r.GetString(2),
                        startedAt = NullDouble(r, 3),
                        endedAt = NullDouble(r, 4),
                        seatCount = Scalar(conn, "SELECT COUNT(*) FROM seats WHERE exam_id=@e", ("@e", examId)),
                        onlineCount = Scalar(conn,
                            "SELECT COUNT(DISTINCT seat_id) FROM agent_heartbeats WHERE exam_id=@e AND ts>=@cut",
                            ("@e", examId), ("@cut", cut)),
                        pendingSuspicious = Scalar(conn,
                            "SELECT COUNT(*) FROM suspicious_queue WHERE exam_id=@e AND status='pending'", ("@e", examId)),
                    });
                }
                return Results.Json(list);
            });
        });

        // ---- 座位热力 ----
        app.MapGet("/api/exams/{examId}/seats", (string examId) =>
        {
            double cut = Now() - cfg.OnlineWindowSeconds;
            double recentCut = Now() - cfg.RecentRiskWindowSeconds;
            return db.Locked(conn =>
            {
                var list = new List<object>();
                using SqliteCommand c = conn.Cmd(
                    @"SELECT s.seat_id, s.student_id, s.display_name, s.agent_id, s.machine_id,
                        (SELECT MAX(ts) FROM agent_heartbeats h WHERE h.exam_id=s.exam_id AND h.seat_id=s.seat_id),
                        (SELECT MAX(ts) FROM events e WHERE e.exam_id=s.exam_id AND e.seat_id=s.seat_id),
                        (SELECT COALESCE(MAX(risk),0) FROM events e WHERE e.exam_id=s.exam_id AND e.seat_id=s.seat_id AND e.ts>=@rc),
                        (SELECT COUNT(*) FROM events e WHERE e.exam_id=s.exam_id AND e.seat_id=s.seat_id),
                        (SELECT COUNT(*) FROM suspicious_queue q WHERE q.exam_id=s.exam_id AND q.seat_id=s.seat_id AND q.status='pending')
                      FROM seats s WHERE s.exam_id=@e ORDER BY s.seat_id",
                    ("@e", examId), ("@rc", recentCut));
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read())
                {
                    double? lastHb = NullDouble(r, 5);
                    double? lastEv = NullDouble(r, 6);
                    bool online = (lastHb is not null && lastHb >= cut) || (lastEv is not null && lastEv >= cut);
                    list.Add(new
                    {
                        seatId = r.GetString(0),
                        studentId = NullStr(r, 1),
                        displayName = NullStr(r, 2),
                        agentId = NullStr(r, 3),
                        machineId = NullStr(r, 4),
                        online,
                        lastHeartbeatTs = lastHb,
                        lastEventTs = lastEv,
                        maxRecentRisk = r.GetInt32(7),
                        eventCount = r.GetInt32(8),
                        suspiciousCount = r.GetInt32(9),
                    });
                }
                return Results.Json(list);
            });
        });

        // ---- 可疑队列 ----
        app.MapGet("/api/exams/{examId}/suspicious", (string examId, string? status) =>
        {
            string filter = string.IsNullOrEmpty(status) ? "pending" : status;
            return db.Locked(conn =>
            {
                var list = new List<object>();
                string sql = @"SELECT id,seat_id,ts,kind,score,status,refs,reviewer,decided_at,note
                               FROM suspicious_queue WHERE exam_id=@e";
                if (filter != "all") sql += " AND status=@st";
                sql += " ORDER BY score DESC, ts DESC LIMIT 500";

                using SqliteCommand c = filter != "all"
                    ? conn.Cmd(sql, ("@e", examId), ("@st", filter))
                    : conn.Cmd(sql, ("@e", examId));
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new
                    {
                        id = r.GetInt64(0),
                        seatId = r.GetString(1),
                        ts = r.GetDouble(2),
                        kind = r.GetString(3),
                        score = r.GetInt32(4),
                        status = r.GetString(5),
                        refs = ParseNode(NullStr(r, 6)),
                        reviewer = NullStr(r, 7),
                        decidedAt = NullDouble(r, 8),
                        note = NullStr(r, 9),
                    });
                }
                return Results.Json(list);
            });
        });

        // ---- 事件时间线 ----
        app.MapGet("/api/exams/{examId}/events", (string examId, string? seatId, int? limit) =>
        {
            int lim = Math.Clamp(limit ?? 200, 1, 2000);
            return db.Locked(conn =>
            {
                var list = new List<object>();
                string sql = "SELECT id,seat_id,seq,ts,recv_ts,type,payload,risk,evidence_image_id FROM events WHERE exam_id=@e";
                if (!string.IsNullOrEmpty(seatId)) sql += " AND seat_id=@s";
                sql += " ORDER BY id DESC LIMIT @lim";

                using SqliteCommand c = !string.IsNullOrEmpty(seatId)
                    ? conn.Cmd(sql, ("@e", examId), ("@s", seatId!), ("@lim", lim))
                    : conn.Cmd(sql, ("@e", examId), ("@lim", lim));
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new
                    {
                        id = r.GetInt64(0),
                        seatId = r.GetString(1),
                        seq = r.GetInt64(2),
                        ts = r.GetDouble(3),
                        recvTs = r.GetDouble(4),
                        type = r.GetString(5),
                        payload = ParseNode(NullStr(r, 6)),
                        risk = r.GetInt32(7),
                        evidenceImageId = NullStr(r, 8),
                    });
                }
                return Results.Json(list);
            });
        });

        // ---- 证据图字节 ----
        app.MapGet("/api/images/{imageId}", async (string imageId, HttpContext ctx) =>
        {
            string? rel = db.Locked(conn => Scalar<string?>(conn,
                "SELECT file_path FROM images WHERE image_id=@id", ("@id", imageId)));
            if (rel is null) { ctx.Response.StatusCode = 404; return; }
            string? full = storage.Resolve(rel);
            if (full is null || !File.Exists(full)) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = "image/webp";
            await ctx.Response.SendFileAsync(full);
        });

        // ---- 证据图元数据 ----
        app.MapGet("/api/images/{imageId}/meta", (string imageId) =>
            db.Locked(conn =>
            {
                using SqliteCommand c = conn.Cmd(
                    @"SELECT image_id,seat_id,ts,trigger,phash,width,height,bytes,is_evidence,uploaded_to_ocr
                      FROM images WHERE image_id=@id", ("@id", imageId));
                using SqliteDataReader r = c.ExecuteReader();
                if (!r.Read()) return Results.NotFound();
                return Results.Json(new
                {
                    imageId = r.GetString(0),
                    seatId = r.GetString(1),
                    ts = r.GetDouble(2),
                    trigger = r.GetString(3),
                    phash = r.GetString(4),
                    width = NullInt(r, 5),
                    height = NullInt(r, 6),
                    bytes = NullLong(r, 7),
                    isEvidence = r.GetInt32(8) != 0,
                    uploadedToOcr = r.GetInt32(9) != 0,
                });
            }));

        // ================= 管理 / 复核 (写) =================

        // 建/更新考试 + 座位绑定
        app.MapPost("/api/exams", async (HttpContext ctx) =>
        {
            JsonNode? body = await JsonNode.ParseAsync(ctx.Request.Body);
            if (body is null) return Results.BadRequest(new { error = "bad_json" });
            string examId = (string?)body["examId"] ?? "";
            string name = (string?)body["name"] ?? examId;
            if (string.IsNullOrEmpty(examId)) return Results.BadRequest(new { error = "missing_examId" });

            double now = Now();
            int seatCount = db.Locked(conn =>
            {
                using (SqliteCommand ex = conn.Cmd(
                    @"INSERT INTO exams (exam_id,name,status,started_at,created_at)
                      VALUES (@id,@n,'active',@ts,@ts)
                      ON CONFLICT(exam_id) DO UPDATE SET name=@n",
                    ("@id", examId), ("@n", name), ("@ts", now)))
                    ex.ExecuteNonQuery();

                int n = 0;
                if (body["seats"] is JsonArray seats)
                {
                    foreach (JsonNode? sn in seats)
                    {
                        if (sn is null) continue;
                        using SqliteCommand sc = conn.Cmd(
                            @"INSERT INTO seats (exam_id,seat_id,student_id,machine_id,agent_id,display_name)
                              VALUES (@e,@s,@stu,@m,@a,@d)
                              ON CONFLICT(exam_id,seat_id) DO UPDATE SET
                                student_id=@stu, machine_id=@m, agent_id=@a, display_name=@d",
                            ("@e", examId), ("@s", (string?)sn["seatId"] ?? ""),
                            ("@stu", (string?)sn["studentId"]), ("@m", (string?)sn["machineId"]),
                            ("@a", (string?)sn["agentId"]), ("@d", (string?)sn["displayName"]));
                        sc.ExecuteNonQuery();
                        n++;
                    }
                }
                return n;
            });
            return Results.Json(new { ok = true, examId, seatCount });
        });

        // 结束考试
        app.MapPost("/api/exams/{examId}/end", (string examId) =>
        {
            double now = Now();
            int changed = db.Locked(conn =>
            {
                using SqliteCommand c = conn.Cmd(
                    "UPDATE exams SET status='ended', ended_at=@ts WHERE exam_id=@e", ("@ts", now), ("@e", examId));
                return c.ExecuteNonQuery();
            });
            return changed > 0 ? Results.Json(new { ok = true, examId, status = "ended" }) : Results.NotFound();
        });

        // 可疑裁决(人工)
        app.MapPost("/api/suspicious/{id:long}/decide", async (long id, HttpContext ctx) =>
        {
            JsonNode? body = await JsonNode.ParseAsync(ctx.Request.Body);
            string status = (string?)body?["status"] ?? "";
            if (status is not ("confirmed" or "dismissed"))
                return Results.BadRequest(new { error = "status must be confirmed|dismissed" });
            string? reviewer = (string?)body?["reviewer"];
            string? note = (string?)body?["note"];
            double now = Now();

            return db.Locked(conn =>
            {
                using (SqliteCommand up = conn.Cmd(
                    @"UPDATE suspicious_queue SET status=@st, reviewer=@r, note=@n, decided_at=@ts WHERE id=@id",
                    ("@st", status), ("@r", reviewer), ("@n", note), ("@ts", now), ("@id", id)))
                {
                    if (up.ExecuteNonQuery() == 0) return Results.NotFound();
                }
                using SqliteCommand c = conn.Cmd(
                    @"SELECT id,seat_id,ts,kind,score,status,refs,reviewer,decided_at,note
                      FROM suspicious_queue WHERE id=@id", ("@id", id));
                using SqliteDataReader r = c.ExecuteReader();
                r.Read();
                var item = new
                {
                    id = r.GetInt64(0),
                    seatId = r.GetString(1),
                    ts = r.GetDouble(2),
                    kind = r.GetString(3),
                    score = r.GetInt32(4),
                    status = r.GetString(5),
                    refs = ParseNode(NullStr(r, 6)),
                    reviewer = NullStr(r, 7),
                    decidedAt = NullDouble(r, 8),
                    note = NullStr(r, 9),
                };
                return Results.Json(new { ok = true, item });
            });
        });
    }

    // ---- 读取小工具 ----
    private static long Scalar(SqliteConnection conn, string sql, params (string, object?)[] ps)
    {
        using SqliteCommand c = conn.Cmd(sql, ps);
        object? v = c.ExecuteScalar();
        return v is null || v is DBNull ? 0 : Convert.ToInt64(v);
    }

    private static T Scalar<T>(SqliteConnection conn, string sql, params (string, object?)[] ps)
    {
        using SqliteCommand c = conn.Cmd(sql, ps);
        object? v = c.ExecuteScalar();
        return v is null || v is DBNull ? default! : (T)v;
    }

    private static string? NullStr(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static double? NullDouble(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    private static int? NullInt(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static long? NullLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    private static JsonNode? ParseNode(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonNode.Parse(json); }
        catch { return JsonValue.Create(json); }
    }
}

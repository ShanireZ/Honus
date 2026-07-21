using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Ingest;

/// 击键节奏旁路(HTTP POST /ingest/keystroke),来自判题网页(经判题后端),不经 Agent。见 api-contract §2.2。
/// M1:落库 + 基础 risk 初判(整段粘贴 / 超人爆发 / 空窗后突现整段 → 可疑)。
/// M2:**会话鉴权**——判题后端持 KSK 对整条提交体签名(X-Horus-KSig),挡同网学员机伪造/栽赃他人击键。
public sealed class KeystrokeIngest(Db db, ServerConfig cfg)
{
    // 击键体专用上限(远小于图片的全局 2MB):timeline 是 keydown 时间戳数组,合法体通常几十 KB。
    // 超大体先拒,免为明显非法体缓冲 + 算 SHA256 验签(未鉴权者也能触发)。
    private const int MaxBodyBytes = 512 * 1024;

    public async Task HandleAsync(HttpContext ctx)
    {
        // 共用 ingest body 读取 + 显式上限(与 ImageIngest 同 helper · 质量#5):Content-Length 超限在分配
        // MemoryStream 前即拒(未鉴权者也能触发的缓冲放大),实测长度兜底。
        var (body, statusCode, err) = await IngestBody.ReadWithLimitAsync(ctx.Request, MaxBodyBytes, allowEmpty: true);
        if (body is null)
        {
            ctx.Response.StatusCode = statusCode!.Value;
            await ctx.Response.WriteAsJsonAsync(new { error = err });
            return;
        }

        // 击键鉴权:X-Horus-KSig = HMAC(KSK, "keystroke\n"+sha256(body))。任何篡改(含改 seatId 栽赃)都会破坏签名。
        if (cfg.KeystrokeAuthEnabled)
        {
            string ksig = ctx.Request.Headers["X-Horus-KSig"].ToString();
            string want = Auth.KeystrokeSig(cfg.Ksk!, body);
            if (string.IsNullOrEmpty(ksig) || !Crypto.FixedTimeEquals(ksig, want))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "bad_ksig" });
                return;
            }
        }

        JsonElement root;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "bad_json" }); return; }

        string examId = Str(root, "examId") ?? "";
        string seatId = Str(root, "seatId") ?? "";
        string? submissionId = Str(root, "submissionId");
        double ts = root.TryGetProperty("ts", out JsonElement tse) && tse.TryGetDouble(out double t)
            ? t : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        string? timeline = root.TryGetProperty("timeline", out JsonElement tl) ? tl.GetRawText() : null;
        JsonElement features = root.TryGetProperty("features", out JsonElement f) ? f : default;
        string? featuresJson = features.ValueKind == JsonValueKind.Object ? features.GetRawText() : null;

        int risk = RiskFrom(features);

        // 落库 + 入可疑队列在**同一写锁事务**内:避免二者分处两个事务时,归档窗口卡在中间导致入队一条
        // 指向已归档删除样本的孤儿 pending(与 EventIngest 同型修复)。sealed 检查已在事务开头。
        long id = db.Locked(conn =>
        {
            if (conn.IsExamSealed(examId)) return -1L;   // 归档中/已归档:短路不落库(避免归档窗口 late-ingest)

            // 幂等落库:同 (exam,seat,ts,submissionId) 已存在则不重插——防同网攻击者嗅到 HTTP 明文后
            // **原样重放**合法签名体(签名验得过但无去重键)灌可疑队列/DoS。签名防伪造/栽赃,幂等防重放。
            using SqliteCommand ins = conn.Cmd(
                @"INSERT INTO keystroke_samples (exam_id,seat_id,submission_id,ts,timeline,features,risk)
                  SELECT @e,@s,@sub,@ts,@tl,@ft,@risk
                  WHERE NOT EXISTS (SELECT 1 FROM keystroke_samples
                                    WHERE exam_id=@e AND seat_id=@s AND ts=@ts
                                      AND COALESCE(submission_id,'')=COALESCE(@sub,''))",
                ("@e", examId), ("@s", seatId), ("@sub", submissionId), ("@ts", ts),
                ("@tl", timeline), ("@ft", featuresJson), ("@risk", risk));
            if (ins.ExecuteNonQuery() == 0) return -1L;   // 重放/重复:不另存、不重复入队
            using SqliteCommand idc = conn.Cmd("SELECT last_insert_rowid()");
            long newId = Convert.ToInt64(idc.ExecuteScalar());

            // 达阈值 → 入可疑队列(kind 依特征细分),与落库同一事务
            if (risk >= cfg.RiskThreshold)
            {
                string kind = features.ValueKind == JsonValueKind.Object &&
                              features.TryGetProperty("idleThenBlock", out JsonElement itb) &&
                              itb.ValueKind == JsonValueKind.True
                    ? "ide_plugin_suspect" : "large_paste";
                string refs = JsonSerializer.Serialize(new[] { $"keystroke:{newId}" });
                using SqliteCommand c = conn.Cmd(
                    @"INSERT INTO suspicious_queue (exam_id,seat_id,ts,kind,score,status,refs)
                      VALUES (@e,@s,@ts,@k,@sc,'pending',@refs)",
                    ("@e", examId), ("@s", seatId), ("@ts", ts), ("@k", kind), ("@sc", risk), ("@refs", refs));
                c.ExecuteNonQuery();
            }
            return newId;
        });

        if (id < 0)   // 重放:幂等返回,不入队
        {
            await ctx.Response.WriteAsJsonAsync(new { stored = false, duplicate = true, risk });
            return;
        }

        await ctx.Response.WriteAsJsonAsync(new { stored = true, risk });
    }

    private static int RiskFrom(JsonElement features)
    {
        if (features.ValueKind != JsonValueKind.Object) return 0;
        bool idleThenBlock = features.TryGetProperty("idleThenBlock", out JsonElement itb) && itb.ValueKind == JsonValueKind.True;
        int pasteCount = features.TryGetProperty("pasteCount", out JsonElement pc) && pc.TryGetInt32(out int p) ? p : 0;
        double burst = features.TryGetProperty("maxBurstCharsPerSec", out JsonElement b) && b.TryGetDouble(out double bv) ? bv : 0;

        if (idleThenBlock) return RiskScores.KeystrokeIdleThenBlock;     // 空窗后突现整段代码 → 高风险
        if (pasteCount > 0) return RiskScores.KeystrokePaste;            // 粘贴
        if (burst > 120) return RiskScores.KeystrokeBurst;               // 超人输入速度
        return 0;
    }

    private static string? Str(JsonElement obj, string prop)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;
}

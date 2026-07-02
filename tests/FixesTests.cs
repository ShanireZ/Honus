using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Horus.Agent.Buffer;
using Horus.Contracts;
using Horus.Server.Analysis.Vision;
using Horus.Server.Config;
using Horus.Server.Data;
using Horus.Server.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Horus.Server.Tests;

// 2026-07-02 审查后修复的回归锁定测试。编号对应审查报告的 13 项。

/// #2 事件缓冲原子性 / #12 孤儿图清理。
public class FixLocalBufferTests
{
    private static string TempDir()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "horus-fix-" + Guid.NewGuid().ToString("N")[..10])).FullName;

    [Fact]   // #2:崩溃/掉电撕裂的半截行,读侧整行丢弃且不卡续传队列;换行修复使后续事件不被并入损坏。
    public async Task 崩溃撕裂坏行_快照丢弃_下一条不被并入损坏()
    {
        string dir = TempDir();
        try
        {
            var b = new LocalBuffer(dir);
            await b.EnqueueEventAsync(1, "{\"a\":1}");
            // 模拟崩溃:直接往 pending 追加一条**无换行结尾的半截行**(撕裂)
            File.AppendAllText(Path.Combine(dir, "events.pending.jsonl"), "2\t{\"a\":");
            // 重启 + 再入队:换行修复应使 seq=3 独占一行,不与坏行并成一行
            var b2 = new LocalBuffer(dir);
            await b2.EnqueueEventAsync(3, "{\"a\":3}");

            var snap = b2.SnapshotPendingEvents();
            Assert.Equal(2, snap.Count);                              // 坏的 seq=2 被丢弃(不续传垃圾卡队列)
            Assert.Contains(snap, x => x.seq == 1);
            Assert.Equal("{\"a\":3}", snap.Single(x => x.seq == 3).json);   // seq=3 完整,未被坏行并入损坏
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]   // #12:构造时清理孤儿图片文件(无 meta 的 webp / 残留 meta.tmp),有效配对保留。
    public void 孤儿图片文件_构造时清理_有效配对保留()
    {
        string dir = TempDir();
        try
        {
            string img = Path.Combine(dir, "images");
            Directory.CreateDirectory(img);
            File.WriteAllBytes(Path.Combine(img, "9.webp"), new byte[] { 1, 2, 3 });     // 孤儿:无 meta
            File.WriteAllText(Path.Combine(img, "8.meta.tmp"), "junk");                   // 未完成的原子 meta 写
            File.WriteAllBytes(Path.Combine(img, "5.webp"), new byte[] { 1 });           // 有效配对
            File.WriteAllText(Path.Combine(img, "5.meta"), "img_x\tevent:browser\t0000000000000000");

            _ = new LocalBuffer(dir);   // 构造即清理

            Assert.False(File.Exists(Path.Combine(img, "9.webp")));
            Assert.False(File.Exists(Path.Combine(img, "8.meta.tmp")));
            Assert.True(File.Exists(Path.Combine(img, "5.webp")));    // 有效配对不误删
            Assert.True(File.Exists(Path.Combine(img, "5.meta")));
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// #4 送云前派生:联网分析器必剥元数据 / 解码失败 fail-safe。
public class FixVisionPrepStripTests
{
    private static byte[] Webp(int w, int h)
    {
        using var img = new Image<Rgba32>(w, h, Color.White);
        using var ms = new MemoryStream();
        img.SaveAsWebp(ms);
        return ms.ToArray();
    }

    [Fact]   // maxEdge<=0 时联网分析器仍解码+剥元数据(不直通),只是不缩放。
    public void 联网分析器_maxEdge0_仍剥元数据不直通()
    {
        byte[] webp = Webp(50, 40);
        byte[]? outb = VisionImagePrep.Prepare(webp, new ServerConfig { VisionMaxEdge = 0 }, mustStrip: true);
        Assert.NotNull(outb);
        Assert.NotSame(webp, outb);               // 未直通:已重编码剥元数据
        using var img = Image.Load<Rgba32>(outb!);
        Assert.Null(img.Metadata.ExifProfile);
        Assert.Equal(50, img.Width);              // maxEdge<=0 → 不缩放
    }

    [Fact]   // 联网分析器解码失败 → 返回 null(fail-safe),绝不把未剥元数据的原字节送云。
    public void 联网分析器_解码失败_返回null()
        => Assert.Null(VisionImagePrep.Prepare(
            Encoding.ASCII.GetBytes("not-an-image"), new ServerConfig { VisionMaxEdge = 1600 }, mustStrip: true));

    [Fact]   // 本地/mock(mustStrip=false)maxEdge<=0 保持直通原字节(测试假图字节可达分析器)。
    public void 本地分析器_maxEdge0_直通原字节()
    {
        byte[] fake = Encoding.ASCII.GetBytes("AICHAT-mark");
        Assert.Same(fake, VisionImagePrep.Prepare(fake, new ServerConfig { VisionMaxEdge = 0 }, mustStrip: false));
    }
}

/// #5 归档 archive_events 保留 server_risk,risk 存原始 agent 自报值(锚点可复算)。
public class FixArchiveServerRiskTests
{
    private const double Day = 86400.0;
    private static string TempDir()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "horus-fix-sr-" + Guid.NewGuid().ToString("N")[..10])).FullName;

    [Fact]
    public void 归档保留server_risk_risk存原始值()
    {
        string dir = TempDir();
        try
        {
            using var db = new Db(Path.Combine(dir, "live.db"));
            var storage = new Storage(dir);
            db.Write(conn =>
            {
                using (SqliteCommand c = conn.Cmd("INSERT INTO exams (exam_id,name,status,started_at,ended_at,created_at) VALUES ('E1','x','ended',1000,1000,1000)")) c.ExecuteNonQuery();
                using (SqliteCommand c = conn.Cmd("INSERT INTO seats (exam_id,seat_id,agent_id) VALUES ('E1','A01','ag')")) c.ExecuteNonQuery();
                // 学员把"访问 AI 站"签成 risk=0,服务器复判 server_risk=80 → 有效风险 max=80≥阈值 = 关键。
                using (SqliteCommand c = conn.Cmd(
                    @"INSERT INTO events (exam_id,seat_id,agent_id,machine_id,seq,ts,recv_ts,type,payload,risk,server_risk,hash_self,sig)
                      VALUES ('E1','A01','ag','PC',1,1000,1000,'browser_url','{""url"":""x""}',0,80,'h','s')")) c.ExecuteNonQuery();
            });

            var cfg = new ServerConfig { RetentionDays = 30, ArchiveCriticalRisk = 50, ArchiveDbPath = "arch.db", ArchiveEnabled = false };
            var svc = new ArchiveService(db, storage, cfg, NullLogger<ArchiveService>.Instance);
            Assert.Equal(1, svc.RunOnce(1000 + 40 * Day).Archived);

            using var arc = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "arch.db"), Pooling = false }.ToString());
            arc.Open();
            using SqliteCommand c2 = arc.CreateCommand();
            c2.CommandText = "SELECT risk, server_risk FROM archive_events WHERE exam_id='E1'";
            using SqliteDataReader r = c2.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(0, r.GetInt32(0));    // risk 存原始 agent 自报(canonicalCore 签的正是它,锚点可复算)
            Assert.Equal(80, r.GetInt32(1));   // server_risk 旁注留证:归档库仍能查证"为何判高危"
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

/// #1 / #7 / #9 / #13b / #13c 服务器端集成回归。
public class FixServerApiTests
{
    private static async Task CreateExam(HttpClient http, string examId = "E1", string seat = "A07")
    {
        HttpResponseMessage r = await http.PostAsJsonAsync("/api/exams", new
        {
            examId, name = "x",
            seats = new[] { new { seatId = seat, agentId = "ag-A07", machineId = "PC-A07" } },
        });
        r.EnsureSuccessStatusCode();
    }

    [Fact]   // #7:免鉴权 login 收到畸形 JSON 应 400(而非未捕获异常 500)。
    public async Task 登录_畸形JSON_返回400()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        HttpResponseMessage resp = await http.PostAsync("/api/login", new StringContent("garbage", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]   // #7:建考试端点收到畸形 JSON 应 400。
    public async Task 建考试_畸形JSON_返回400()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        HttpResponseMessage resp = await http.PostAsync("/api/exams", new StringContent("{not-json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]   // #13b:examId 含路径危险字符 → 400;中文 examId 合法放行。
    public async Task 建考试_examId危险字符400_中文examId放行()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        HttpResponseMessage bad = await http.PostAsJsonAsync("/api/exams", new { examId = "a/b", name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        HttpResponseMessage ok = await http.PostAsJsonAsync("/api/exams", new { examId = "期末-2026", name = "期末" });
        ok.EnsureSuccessStatusCode();
    }

    [Fact]   // #13c:归档进行中(archiving)考试的 integrity 端点返回 applicable:false,不对半清理数据跑审计。
    public async Task 归档中考试_integrity返回applicable_false()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExam(http);
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn => { using SqliteCommand c = conn.Cmd("UPDATE exams SET status='archiving' WHERE exam_id='E1'"); c.ExecuteNonQuery(); });

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/integrity");
        Assert.Equal("archiving", rep.GetProperty("status").GetString());
        Assert.False(rep.GetProperty("applicable").GetBoolean());
    }

    [Fact]   // #1:图片上传到已封存(archived)考试被拒、不落库(late-ingest 隔离)。
    public async Task 图片上传到已封存考试_拒绝不落库()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExam(http);
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn => { using SqliteCommand c = conn.Cmd("UPDATE exams SET status='archived' WHERE exam_id='E1'"); c.ExecuteNonQuery(); });

        const string cid = "img_sealed00001", phash = "aabbccddeeff0011", ts = "1750000000.1";
        byte[] webp = Encoding.ASCII.GetBytes("RIFF-webp-sealed");
        string canon = Auth.ImageCanonicalHeaders("E1", "A07", "ag-A07", 1, "event:browser", phash, ts, cid);
        string sig = Auth.ImageSig(TestApp.Psk, canon, webp);
        var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/images") { Content = new ByteArrayContent(webp) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        req.Headers.Add("X-Horus-Exam", "E1");
        req.Headers.Add("X-Horus-Seat", "A07");
        req.Headers.Add("X-Horus-Agent", "ag-A07");
        req.Headers.Add("X-Horus-Seq", "1");
        req.Headers.Add("X-Horus-Trigger", "event:browser");
        req.Headers.Add("X-Horus-Phash", phash);
        req.Headers.Add("X-Horus-Ts", ts);
        req.Headers.Add("X-Horus-Sig", sig);
        req.Headers.Add("X-Horus-Image-Id", cid);
        HttpResponseMessage resp = await http.SendAsync(req);

        using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("stored").GetBoolean());
        long n = db.Read(conn => { using SqliteCommand c = conn.Cmd("SELECT COUNT(*) FROM images WHERE image_id=@id", ("@id", cid)); return Convert.ToInt64(c.ExecuteScalar()); });
        Assert.Equal(0, n);   // 未落库
    }

    [Fact]   // #9:同一 (exam,seat,agent) 多条心跳后,agent_heartbeats 只保留最新一行(裁剪,防无界追加)。
    public async Task 心跳裁剪_只保留最新一行()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExam(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);

        double t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        for (int i = 0; i < 3; i++)
        {
            string hb = Ws.SignedEventTs("E1", "A07", "ag-A07", "PC-A07", SignalType.Heartbeat,
                new() { ["status"] = "alive" }, 0, i + 1, t0 + i);
            await Ws.SendAsync(ws, hb);
            await Ws.ReceiveAsync(ws);
        }

        Db db = app.Services.GetRequiredService<Db>();
        long n = db.Read(conn =>
        {
            using SqliteCommand c = conn.Cmd("SELECT COUNT(*) FROM agent_heartbeats WHERE exam_id='E1' AND seat_id='A07' AND agent_id='ag-A07'");
            return Convert.ToInt64(c.ExecuteScalar());
        });
        Assert.Equal(1, n);   // 裁剪:只留最新 ts 一行
    }
}

// ===== 2026-07-02 第三轮审查修复的回归锁定 =====

/// F5:URL 黑名单改按 DNS 标签匹配,杜绝子串误伤(richardbard≠bard·foryou.com≠you.com·googleusercontent≠google)。
public class FixHostMatchTests
{
    [Theory]
    // 真命中(应判 AI/搜索):整段标签 / 域后缀
    [InlineData("chat.openai.com", true)]
    [InlineData("openai.com", true)]
    [InlineData("poe.com", true)]
    [InlineData("you.com", true)]
    [InlineData("www.you.com", true)]
    // 假阳性(旧子串匹配会误命中,新标签匹配不命中)
    [InlineData("richardbard.com", false)]   // 含 "bard" 子串但非整段标签
    [InlineData("foryou.com", false)]        // 含 "you.com" 子串但非域后缀
    [InlineData("myclaude-notes.local", false)]
    [InlineData("openai-mirror.cn", false)]  // 连字符镜像:标签匹配会漏(交白名单+人工兜底,换低假阳性)
    public void AI站按标签匹配_不再子串误伤(string host, bool expected)
        => Assert.Equal(expected, Horus.Server.Analysis.RiskModel.HostMatchesAny(host, Horus.Server.Analysis.RiskModel.AiHosts));

    [Theory]
    [InlineData("www.google.com", true)]
    [InlineData("google.com", true)]
    [InlineData("googleusercontent.com", false)]   // 含 "google" 子串但 "googleusercontent" 非整段 → 不误判搜索
    [InlineData("baidu.com", true)]
    public void 搜索站按标签匹配(string host, bool expected)
        => Assert.Equal(expected, Horus.Server.Analysis.RiskModel.HostMatchesAny(host, Horus.Server.Analysis.RiskModel.SearchHosts));

    [Fact]   // F11:空 host(about:blank/data:/解析失败)在配了白名单时不再被误判高危(返回 0)。
    public void 空host不误判高危()
    {
        using JsonDocument p = JsonDocument.Parse("{\"url\":\"about:blank\"}");
        var wl = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "judge.exam.cn" };
        int risk = Horus.Server.Analysis.RiskModel.Derive(SignalType.BrowserUrl, p.RootElement, wl, null);
        Assert.Equal(0, risk);
    }

    [Fact]   // F5 端到端:chat.openai.com 仍判 80(未因收严匹配漏掉真命中)。
    public void 真AI站仍判高危()
    {
        using JsonDocument p = JsonDocument.Parse("{\"url\":\"https://chat.openai.com/c/abc\"}");
        int risk = Horus.Server.Analysis.RiskModel.Derive(SignalType.BrowserUrl, p.RootElement, null, null);
        Assert.Equal(80, risk);
    }
}

/// F9:confidence 整数 1(=1%)不再被 0-1 概率启发式误放大成 100;严格 (0,1) 才放大。
public class FixConfidenceBoundaryTests
{
    private static int Conf(string json) => OpenAiCompatibleVisionAnalyzer.Parse(json)!.Confidence;

    [Fact] public void 整数1保持1_不放大()   => Assert.Equal(1, Conf("{\"suspicious\":true,\"confidence\":1}"));
    [Fact] public void 小数09仍放大到90()     => Assert.Equal(90, Conf("{\"suspicious\":true,\"confidence\":0.9}"));
    [Fact] public void 整数95不变()           => Assert.Equal(95, Conf("{\"suspicious\":true,\"confidence\":95}"));
}

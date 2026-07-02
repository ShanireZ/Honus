using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// M3 哈希链完整性复验:①ingest 时复算 hashSelf 拒收「hashSelf 不承诺其 payload」的帧;
/// ②/integrity 离线审计发现落库后篡改(hash 不符)与删除/重排(链断)。
public class IntegrityTests
{
    private const string Exam = "E1", Seat = "A07", Agent = "ag-A07", Machine = "PC-A07";

    private static async Task CreateExamAsync(HttpClient http)
    {
        HttpResponseMessage r = await http.PostAsJsonAsync("/api/exams", new
        {
            examId = Exam, name = "完整性测试",
            seats = new[] { new { seatId = Seat, agentId = Agent, machineId = Machine, displayName = "学员", studentId = "s01" } },
        });
        r.EnsureSuccessStatusCode();
    }

    /// 构造一条已封链事件,返回(信封 JSON, 本条 hashSelf) —— 便于把 hashSelf 串给下一条作 hashPrev。
    private static (string json, string hashSelf) BuildEvent(
        SignalType type, Dictionary<string, object?> payload, int risk, long seq, string hashPrev,
        string exam = Exam, string seat = Seat, string agent = Agent, string machine = Machine)
    {
        var core = new AgentEvent
        {
            ExamId = exam, SeatId = seat, AgentId = agent, MachineId = machine,
            Ts = 1750000000.0 + seq, Type = type, Payload = payload, Risk = risk, Seq = seq,
        };
        string hashSelf = EventCanonical.HashSelf(hashPrev, core, seq);
        string sig = EventCanonical.Sig(TestApp.Psk, hashSelf, seq);
        AgentEvent stamped = core with { HashPrev = hashPrev, HashSelf = hashSelf };
        return (Envelope.Serialize(stamped, sig), hashSelf);
    }

    /// 发送一条 window_focus,回读 ack。返回本条 hashSelf。
    private static async Task<string> SendChainedAsync(WebSocket ws, long seq, string hashPrev)
    {
        (string json, string hashSelf) = BuildEvent(
            SignalType.WindowFocus, new() { ["title"] = "题目" + seq, ["process"] = "acrobat" }, 0, seq, hashPrev);
        await Ws.SendAsync(ws, json);
        JsonElement ack = await Ws.ReceiveAsync(ws);
        Assert.Equal("ack", ack.GetProperty("type").GetString());   // 合法链每条都 ack(不误伤)
        return hashSelf;
    }

    [Fact]
    public async Task 完整链_离线审计全通过()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);

        string h1 = await SendChainedAsync(ws, 1, "GENESIS");
        string h2 = await SendChainedAsync(ws, 2, h1);
        await SendChainedAsync(ws, 3, h2);

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/integrity");
        Assert.True(rep.GetProperty("ok").GetBoolean());
        Assert.Equal(3, rep.GetProperty("totalEvents").GetInt32());
        Assert.Equal(3, rep.GetProperty("totalHashOk").GetInt32());
        Assert.Equal(3, rep.GetProperty("totalChainOk").GetInt32());
        JsonElement agents = rep.GetProperty("agents");
        Assert.Equal(1, agents.GetArrayLength());
        Assert.True(agents[0].GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ingest时_hashSelf不承诺payload_拒收bad_hash()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);

        // 为 payload=P(访问 AI 站)正确封链,得合法 hashSelf/sig;然后把信封里的 url 篡改为判题站(P'),
        // hashSelf/sig 保持不变 —— sig 仍验得过(只绑 hashSelf),但 hashSelf 不再承诺 P' → bad_hash。
        (string json, _) = BuildEvent(SignalType.BrowserUrl,
            new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false }, 80, 1, "GENESIS");
        string tampered = json.Replace("chat.openai.com", "judge.exam.cn");
        Assert.NotEqual(json, tampered);   // 确保确实改动了

        await Ws.SendAsync(ws, tampered);
        JsonElement err = await Ws.ReceiveAsync(ws);
        Assert.Equal("error", err.GetProperty("type").GetString());
        Assert.Equal("bad_hash", err.GetProperty("code").GetString());

        // 未落库
        JsonElement events = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/events?seatId={Seat}");
        Assert.Equal(0, events.GetArrayLength());
    }

    [Fact]
    public async Task 落库后篡改payload_审计报hash不符()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        await SendChainedAsync(ws, 1, "GENESIS");

        // 直接改库(模拟 DB 层篡改 / 存储损坏):payload 被改,但 hash_self 未随之更新 → 锚点不再自洽
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                "UPDATE events SET payload=@p WHERE exam_id=@e AND seq=1", ("@p", "{\"title\":\"改过了\"}"), ("@e", Exam));
            Assert.Equal(1, c.ExecuteNonQuery());
        });

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/integrity");
        Assert.False(rep.GetProperty("ok").GetBoolean());
        JsonElement agent = rep.GetProperty("agents")[0];
        JsonElement mism = agent.GetProperty("hashMismatches");
        Assert.Equal(1, mism.GetArrayLength());
        Assert.Equal(1, mism[0].GetProperty("seq").GetInt64());
        // 链连续本身没断(只改了内容,没删没插)→ continuityBreaks 为空
        Assert.Equal(0, agent.GetProperty("continuityBreaks").GetArrayLength());
    }

    [Fact]
    public async Task 迁移前旧事件缺machineId_标不可验_不误报篡改()
    {
        // 闭合审计 High:M3 前落库的旧事件 machine_id=NULL,canonicalCore 含 machineId 无从复算 →
        // 必须归为 unverifiable,**不能**误报"hash 不符(疑篡改)"污染取证结论。
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        await SendChainedAsync(ws, 1, "GENESIS");

        // 模拟迁移前旧行:machine_id 置 NULL(内容/hash 未动)
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd("UPDATE events SET machine_id=NULL WHERE exam_id=@e AND seq=1", ("@e", Exam));
            Assert.Equal(1, c.ExecuteNonQuery());
        });

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/integrity");
        Assert.True(rep.GetProperty("ok").GetBoolean());                       // 无篡改证据
        Assert.Equal(1, rep.GetProperty("totalUnverifiable").GetInt32());       // 但诚实标注 1 条不可验
        Assert.Equal(0, rep.GetProperty("totalHashOk").GetInt32());
        JsonElement agent = rep.GetProperty("agents")[0];
        Assert.Equal(1, agent.GetProperty("unverifiable").GetInt32());
        Assert.Equal(0, agent.GetProperty("hashMismatches").GetArrayLength());  // 关键:不误报篡改
    }

    [Fact]
    public async Task 已归档考试_integrity返回applicable_false_不伪装全绿()
    {
        // 闭合审计 Med:归档后 live 事件清空,端点不应返回空的 ok:true(误导"已核验干净")。
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using (WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent))
        {
            await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
            await Ws.ReceiveAsync(ws);
            await SendChainedAsync(ws, 1, "GENESIS");
        }
        (await http.PostAsync($"/api/exams/{Exam}/end", null)).EnsureSuccessStatusCode();

        var archive = app.Services.GetRequiredService<Horus.Server.Jobs.ArchiveService>();
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + 31 * 86400.0;
        Assert.Equal(1, archive.RunOnce(now).Archived);

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/integrity");
        Assert.Equal("archived", rep.GetProperty("status").GetString());
        Assert.False(rep.GetProperty("applicable").GetBoolean());
        Assert.False(rep.TryGetProperty("ok", out _));   // 不再有会被误读为"全绿"的 ok 字段

        // 不存在的考试 → 404
        HttpResponseMessage nf = await http.GetAsync("/api/exams/NOPE/integrity");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, nf.StatusCode);
    }

    [Fact]
    public async Task 删除中间事件_审计报链断()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        string h1 = await SendChainedAsync(ws, 1, "GENESIS");
        string h2 = await SendChainedAsync(ws, 2, h1);
        await SendChainedAsync(ws, 3, h2);

        // 删除中间那条(seq=2)→ 第 3 条的 hash_prev(=h2)不再等于新前驱(第 1 条 h1)→ 链断
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd("DELETE FROM events WHERE exam_id=@e AND seq=2", ("@e", Exam));
            Assert.Equal(1, c.ExecuteNonQuery());
        });

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/integrity");
        Assert.False(rep.GetProperty("ok").GetBoolean());
        JsonElement agent = rep.GetProperty("agents")[0];
        // 每条自身 hash 仍自洽(没改内容)→ hashMismatches 空;但链断在第 3 条
        Assert.Equal(0, agent.GetProperty("hashMismatches").GetArrayLength());
        JsonElement breaks = agent.GetProperty("continuityBreaks");
        Assert.Equal(1, breaks.GetArrayLength());
        Assert.Equal(3, breaks[0].GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task 归档中考试_事件被短路不落库_仍ack()
    {
        // #7:归档"读快照→DELETE WHERE exam_id"之间到达的 late-ingest 会被无锚点删。修=status='archiving' 后 ingest 短路。
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        // 直接置 archiving(模拟归档进行中)
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd("UPDATE exams SET status='archiving' WHERE exam_id=@e", ("@e", Exam));
            Assert.Equal(1, c.ExecuteNonQuery());
        });

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        await SendChainedAsync(ws, 1, "GENESIS");   // 内含 ack 断言:仍 ack(让 Agent 停发)

        JsonElement events = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/events?seatId={Seat}");
        Assert.Equal(0, events.GetArrayLength());   // 归档中:未落库(不会被随后的 DELETE 无锚点删)
    }

    [Fact]
    public async Task Agent重启后新链段_GENESIS锚点_不误报链断()
    {
        // #1:Agent 每次进程启动新建 HashChain,重启后首条 hash_prev=GENESIS,但 seq 续增。
        // 审计应把它认作**重启锚点**(合法段起点),而非删除/插入的 continuityBreak。
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        string h1 = await SendChainedAsync(ws, 1, "GENESIS");
        await SendChainedAsync(ws, 2, h1);
        await SendChainedAsync(ws, 3, "GENESIS");   // 模拟重启:seq 续到 3,新链段从 GENESIS 起

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/integrity");
        Assert.True(rep.GetProperty("ok").GetBoolean());                       // 重启不是篡改
        Assert.Equal(1, rep.GetProperty("totalRestartBoundaries").GetInt32());
        JsonElement agent = rep.GetProperty("agents")[0];
        Assert.Equal(0, agent.GetProperty("continuityBreaks").GetArrayLength());   // 关键:不误报链断
        Assert.Equal(1, agent.GetProperty("restartBoundaries").GetInt32());
    }

    [Fact]
    public async Task 落库后改payload并重算hashSelf但无PSK重签_审计凭sig识破()
    {
        // #1 连带:hash_self 是无密钥 SHA256,非 PSK 篡改者改 payload 后可重算使①自洽;
        // 唯 sig(HMAC-PSK)能识破。验证审计补的 sig 校验能抓到"hashSelf 自洽但 sig 不符"。
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        await SendChainedAsync(ws, 1, "GENESIS");

        // 攻击者(无 PSK):改 payload 为 P',重算 hash_self'(无密钥 SHA256)使锚点自洽;sig 仍绑旧 hash_self。
        var core2 = new AgentEvent
        {
            ExamId = Exam, SeatId = Seat, AgentId = Agent, MachineId = Machine,
            Ts = 1750000000.0 + 1, Type = SignalType.WindowFocus,
            Payload = new() { ["title"] = "forged" }, Risk = 0, Seq = 1,
        };
        string hs2 = EventCanonical.HashSelf("GENESIS", core2, 1);
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                "UPDATE events SET payload=@p, hash_self=@h WHERE exam_id=@e AND seq=1",
                ("@p", "{\"title\":\"forged\"}"), ("@h", hs2), ("@e", Exam));
            Assert.Equal(1, c.ExecuteNonQuery());
        });

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/integrity");
        Assert.False(rep.GetProperty("ok").GetBoolean());
        JsonElement agent = rep.GetProperty("agents")[0];
        JsonElement mism = agent.GetProperty("hashMismatches");
        Assert.Equal(1, mism.GetArrayLength());                            // hashSelf 自洽,但 sig 校验失败
        Assert.Contains("sig", mism[0].GetProperty("detail").GetString());
        Assert.Equal(0, agent.GetProperty("continuityBreaks").GetArrayLength());
    }
}

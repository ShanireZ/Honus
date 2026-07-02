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
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Honus.Contracts;
using Xunit;

namespace Honus.Server.Tests;

public class IngestTests
{
    private static async Task CreateExamAsync(HttpClient http, string examId = "E1", string seatId = "A07")
    {
        HttpResponseMessage r = await http.PostAsJsonAsync("/api/exams", new
        {
            examId,
            name = "测试考试",
            seats = new[] { new { seatId, agentId = "ag-" + seatId, machineId = "PC-" + seatId, displayName = "学员", studentId = "s01" } },
        });
        r.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task 事件_落库_入可疑队列_且幂等去重()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");

        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\",\"agentId\":\"ag-A07\"}");
        JsonElement ack = await Ws.ReceiveAsync(ws);
        Assert.Equal("hello_ack", ack.GetProperty("type").GetString());
        Assert.Equal(0, ack.GetProperty("maxSeq").GetInt64());

        string evt = Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.BrowserUrl,
            new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false }, 80, seq: 1);
        await Ws.SendAsync(ws, evt);
        JsonElement evAck = await Ws.ReceiveAsync(ws);
        Assert.Equal("ack", evAck.GetProperty("type").GetString());
        Assert.Equal(1, evAck.GetProperty("upto").GetInt64());

        JsonElement events = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/events?seatId=A07");
        Assert.Equal(1, events.GetArrayLength());
        Assert.Equal("browser_url", events[0].GetProperty("type").GetString());
        Assert.Equal(80, events[0].GetProperty("risk").GetInt32());
        // payload 是嵌套对象(非字符串)
        Assert.Equal("https://chat.openai.com/", events[0].GetProperty("payload").GetProperty("url").GetString());

        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(1, susp.GetArrayLength());
        Assert.Equal("web_ai", susp[0].GetProperty("kind").GetString());
        Assert.Equal(80, susp[0].GetProperty("score").GetInt32());
        Assert.Equal("pending", susp[0].GetProperty("status").GetString());

        // 重传同一 (agentId, seq, type) → 幂等,不重复落库/入队
        await Ws.SendAsync(ws, evt);
        await Ws.ReceiveAsync(ws); // ack
        JsonElement events2 = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/events?seatId=A07");
        Assert.Equal(1, events2.GetArrayLength());
        JsonElement susp2 = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(1, susp2.GetArrayLength());
    }

    [Fact]
    public async Task 验签失败_拒绝落库()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\",\"agentId\":\"ag-A07\"}");
        await Ws.ReceiveAsync(ws); // hello_ack

        // 用错误 PSK 签名 → sig 不匹配
        byte[] wrongPsk = Enumerable.Repeat((byte)0xFF, 32).ToArray();
        string bad = Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.BrowserUrl,
            new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false }, 80, 1, psk: wrongPsk);
        await Ws.SendAsync(ws, bad);
        JsonElement err = await Ws.ReceiveAsync(ws);
        Assert.Equal("error", err.GetProperty("type").GetString());
        Assert.Equal("bad_sig", err.GetProperty("code").GetString());

        JsonElement events = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/events?seatId=A07");
        Assert.Equal(0, events.GetArrayLength());
    }

    [Fact]
    public async Task 握手鉴权失败_拒绝连接()
    {
        using var app = new TestApp();
        await Assert.ThrowsAnyAsync<Exception>(
            () => app.ConnectEventsAsync("E1", "A07", "ag-A07", goodAuth: false));
    }

    [Fact]
    public async Task 图片上传_存盘_去重_可取回()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();

        byte[] webp = Encoding.ASCII.GetBytes("RIFF....WEBP-fake-bytes-0123456789");
        const string exam = "E1", seat = "A07", agent = "ag-A07", trigger = "event:browser";
        const string phash = "9f3c1a22b0e4d7f1", ts = "1750000000.456";
        const long seq = 5;

        string imageId = await UploadImageAsync(http, webp, exam, seat, agent, seq, trigger, phash, ts, expectDuplicate: false);

        // 相同 phash 再传 → 去重(duplicate=true,复用 imageId)
        string dupId = await UploadImageAsync(http, webp, exam, seat, agent, 6, trigger, phash, ts, expectDuplicate: true);
        Assert.Equal(imageId, dupId);

        // 取回字节
        HttpResponseMessage img = await http.GetAsync($"/api/images/{imageId}");
        Assert.Equal(HttpStatusCode.OK, img.StatusCode);
        Assert.Equal("image/webp", img.Content.Headers.ContentType!.MediaType);
        Assert.Equal(webp, await img.Content.ReadAsByteArrayAsync());

        // 元数据
        JsonElement meta = await http.GetFromJsonAsync<JsonElement>($"/api/images/{imageId}/meta");
        Assert.Equal(phash, meta.GetProperty("phash").GetString());
        Assert.Equal(webp.Length, meta.GetProperty("bytes").GetInt64());
        Assert.Equal(trigger, meta.GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task 击键节奏_空窗后突现整段_入可疑()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        HttpResponseMessage r = await http.PostAsJsonAsync("/ingest/keystroke", new
        {
            examId = "E1", seatId = "A07", submissionId = "sub1", ts = 1750000000.0,
            timeline = new[] { 1, 2, 3 },
            features = new { idleThenBlock = true, pasteCount = 0, maxBurstCharsPerSec = 30 },
        });
        r.EnsureSuccessStatusCode();
        JsonElement body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("stored").GetBoolean());
        Assert.Equal(70, body.GetProperty("risk").GetInt32());

        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(1, susp.GetArrayLength());
        Assert.Equal("ide_plugin_suspect", susp[0].GetProperty("kind").GetString());
        Assert.Equal(70, susp[0].GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task 人工裁决_确认后移出待复核()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.ProcessStart,
            new() { ["name"] = "cmd.exe", ["pid"] = 4321, ["whitelisted"] = false }, 70, 1));
        await Ws.ReceiveAsync(ws);

        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        long id = susp[0].GetProperty("id").GetInt64();
        Assert.Equal("non_whitelist_proc", susp[0].GetProperty("kind").GetString());

        HttpResponseMessage dec = await http.PostAsJsonAsync($"/api/suspicious/{id}/decide",
            new { status = "confirmed", reviewer = "监考A", note = "确认使用命令行" });
        dec.EnsureSuccessStatusCode();
        JsonElement decBody = await dec.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("confirmed", decBody.GetProperty("item").GetProperty("status").GetString());

        JsonElement pending = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious?status=pending");
        Assert.Equal(0, pending.GetArrayLength());
        JsonElement confirmed = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious?status=confirmed");
        Assert.Equal(1, confirmed.GetArrayLength());
    }

    [Fact]
    public async Task 座位在线_心跳后热力反映()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        // 初始:离线
        JsonElement seats0 = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/seats");
        Assert.Equal(1, seats0.GetArrayLength());
        Assert.False(seats0[0].GetProperty("online").GetBoolean());

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        // 心跳事件(ts 用当前时钟,才落在在线窗口内)
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        string hb = Ws.SignedEventTs("E1", "A07", "ag-A07", "PC-A07", SignalType.Heartbeat,
            new() { ["status"] = "alive" }, 0, 1, now);
        await Ws.SendAsync(ws, hb);
        await Ws.ReceiveAsync(ws);

        JsonElement seats1 = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/seats");
        Assert.True(seats1[0].GetProperty("online").GetBoolean());
    }

    // ---- 图片上传小工具 ----
    private static async Task<string> UploadImageAsync(HttpClient http, byte[] webp,
        string exam, string seat, string agent, long seq, string trigger, string phash, string ts, bool expectDuplicate)
    {
        string canon = Auth.ImageCanonicalHeaders(exam, seat, agent, seq, trigger, phash, ts);
        string sig = Auth.ImageSig(TestApp.Psk, canon, webp);

        var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/images");
        req.Content = new ByteArrayContent(webp);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        req.Headers.Add("X-Honus-Exam", exam);
        req.Headers.Add("X-Honus-Seat", seat);
        req.Headers.Add("X-Honus-Agent", agent);
        req.Headers.Add("X-Honus-Seq", seq.ToString());
        req.Headers.Add("X-Honus-Trigger", trigger);
        req.Headers.Add("X-Honus-Phash", phash);
        req.Headers.Add("X-Honus-Ts", ts);
        req.Headers.Add("X-Honus-Sig", sig);

        HttpResponseMessage resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectDuplicate, body.GetProperty("duplicate").GetBoolean());
        return body.GetProperty("imageId").GetString()!;
    }
}

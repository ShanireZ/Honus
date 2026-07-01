using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Honus.Agent.Buffer;
using Honus.Agent.Config;
using Honus.Contracts;

namespace Honus.Agent.Transport;

/// 事件走 WebSocket(实时),图片走 HTTP。**含握手鉴权、hello/ack、断线重连(指数退避)、续传**。
/// 可靠性:每条事件先落缓冲再尽力发送;服务器 ack(upto) 后压实;重连后按 seq 续传(服务器幂等去重)。
/// WS 连接与 HttpClient 均可注入(默认真实实现;测试注入 TestServer 客户端,实现内存端到端验证)。
public sealed class UplinkClient : IAsyncDisposable
{
    private readonly AgentConfig _cfg;
    private readonly LocalBuffer _buffer;
    private readonly HttpClient _http;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> _wsConnect;
    private readonly Uri _wsUri;
    private readonly Uri _httpUri;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private volatile WebSocket? _ws;
    private long _seq;
    private long _seqCeiling;                          // 已持久化的序号高水位
    private readonly object _seqLock = new();
    private const long SeqBlock = 256;                 // 每预留 256 个序号才落一次盘

    /// 监考员 capture_now → 触发一次抓图(Program 注入)。
    public Action<string>? OnCaptureNow;
    /// 服务器下发新配置(热更新)。M1 仅回调,由 Program 决定如何应用。
    public Action<JsonElement>? OnConfigUpdate;

    public UplinkClient(AgentConfig cfg, LocalBuffer buffer,
        HttpClient? http = null,
        Func<Uri, CancellationToken, Task<WebSocket>>? wsConnect = null)
    {
        _cfg = cfg;
        _buffer = buffer;
        _http = http ?? new HttpClient();
        _wsConnect = wsConnect ?? DefaultConnectAsync;
        _wsUri = new Uri($"{cfg.ServerWsBase}/ingest/events?examId={cfg.ExamId}&seatId={cfg.SeatId}&agentId={cfg.AgentId}");
        _httpUri = new Uri($"{cfg.ServerHttpBase}/ingest/images");

        // 序号从持久化高水位与缓冲最大 seq 之上继续,杜绝重启复用(否则新事件撞旧 seq 被服务器幂等吞掉)。
        long start = Math.Max(buffer.LoadSeqCeiling(), buffer.MaxBufferedSeq());
        _seq = start;
        _seqCeiling = start;
        if (start > 0) buffer.SaveSeqCeiling(start);
    }

    /// 单调递增序号。跨事件与图片共用;每超过高水位就预留一个 block 并落盘,重启后从高水位之上继续。
    public long NextSeq()
    {
        long s = Interlocked.Increment(ref _seq);
        if (s > Volatile.Read(ref _seqCeiling))
            lock (_seqLock)
                if (s > _seqCeiling) { _seqCeiling = s + SeqBlock; _buffer.SaveSeqCeiling(_seqCeiling); }
        return s;
    }

    /// 默认 WS 连接:带 X-Honus-Auth 握手头的 ClientWebSocket。
    private async Task<WebSocket> DefaultConnectAsync(Uri uri, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Honus-Auth", Auth.Handshake(_cfg.Psk, _cfg.ExamId, _cfg.SeatId, _cfg.AgentId));
        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        return ws;
    }

    /// 连接管理循环:连接 → hello → 续传 → 收帧,断线后指数退避重连。直到 ct 取消。
    public async Task RunAsync(CancellationToken ct)
    {
        int backoffMs = 1000;
        while (!ct.IsCancellationRequested)
        {
            WebSocket? ws = null;
            try
            {
                ws = await _wsConnect(_wsUri, ct).ConfigureAwait(false);
                _ws = ws;
                backoffMs = 1000;                                  // 连接成功,退避归零

                await SendHelloAsync(ws, ct).ConfigureAwait(false);
                await ReplayAsync(ws, ct).ConfigureAwait(false);   // 续传缓冲中的事件/图片
                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false); // 阻塞至断线
            }
            catch (OperationCanceledException) { break; }
            catch { /* 连接/收发失败 → 退避重连 */ }
            finally
            {
                _ws = null;
                if (ws is not null) { try { ws.Dispose(); } catch { /* ignore */ } }
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoffMs = Math.Min(backoffMs * 2, 30_000);           // 上限 30s
        }
    }

    private async Task SendHelloAsync(WebSocket ws, CancellationToken ct)
    {
        string hello = JsonSerializer.Serialize(new
        {
            v = 1, type = "hello",
            agentId = _cfg.AgentId, examId = _cfg.ExamId, seatId = _cfg.SeatId, machineId = _cfg.MachineId,
            agentVersion = "0.1.0", ts = Now(),
        });
        await SendRawAsync(ws, hello, ct).ConfigureAwait(false);
    }

    /// 重连后续传:**先补传图片,再重发事件**(与在线时"先传图后发事件"一致,
    /// 使引用该图的事件在服务器端能命中并标记 is_evidence)。服务器按 (agent,seq,type)/image_id 幂等去重。
    private async Task ReplayAsync(WebSocket ws, CancellationToken ct)
    {
        foreach ((long seq, string? imageId, string trigger, ulong phash, byte[] webp) in _buffer.SnapshotPendingImages())
        {
            if (ct.IsCancellationRequested) return;
            string? id = await PostImageAsync(webp, trigger, phash, seq, imageId, ct).ConfigureAwait(false);
            if (id is not null) _buffer.RemoveImage(seq);          // 补传成功即删(同一 imageId → 事件关联不断)
        }
        foreach ((long _, string json) in _buffer.SnapshotPendingEvents())
        {
            if (ct.IsCancellationRequested) return;
            await SendRawAsync(ws, json, ct).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(WebSocket ws, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            string? msg = await ReceiveTextAsync(ws, ct).ConfigureAwait(false);
            if (msg is null) break;                                // 断线/关闭
            try { await HandleFrameAsync(ws, msg, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { /* 单帧异常不影响连接 */ }
        }
    }

    private async Task HandleFrameAsync(WebSocket ws, string msg, CancellationToken ct)
    {
        using JsonDocument doc = JsonDocument.Parse(msg);
        JsonElement root = doc.RootElement;
        string type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
        switch (type)
        {
            case "hello_ack":
                if (root.TryGetProperty("maxSeq", out JsonElement ms) && ms.TryGetInt64(out long maxSeq))
                    AlignSeq(maxSeq);                              // 跨重启对齐序号,避免复用
                break;
            case "ack":
                if (root.TryGetProperty("seq", out JsonElement up) && up.TryGetInt64(out long ackedSeq))
                    _buffer.RemoveEvent(ackedSeq);                 // 逐条精确删除已持久化的 seq
                break;
            case "config_update":
                if (root.TryGetProperty("config", out JsonElement cfgEl))
                    OnConfigUpdate?.Invoke(cfgEl.Clone());
                break;
            case "capture_now":
                OnCaptureNow?.Invoke(root.TryGetProperty("reason", out JsonElement rs) ? rs.GetString() ?? "capture_now" : "capture_now");
                break;
            case "ping":
                await SendRawAsync(ws, "{\"v\":1,\"type\":\"pong\",\"ts\":" + Now().ToString(System.Globalization.CultureInfo.InvariantCulture) + "}", ct).ConfigureAwait(false);
                break;
            default: break;                                        // error / pong / 未知 → 忽略
        }
    }

    /// 发送一条已封装(含 hash/sig)的事件:**先落缓冲(持久),再尽力发送**。ack 到达后压实。
    public async Task SendEventAsync(string json, long seq, CancellationToken ct)
    {
        await _buffer.EnqueueEventAsync(seq, json).ConfigureAwait(false);
        WebSocket? ws = _ws;
        if (ws is { State: WebSocketState.Open })
        {
            try { await SendRawAsync(ws, json, ct).ConfigureAwait(false); }
            catch { /* 失败留待重连续传 */ }
        }
    }

    /// 上传一张 WebP,返回 imageId;失败落缓冲。
    /// 触发型抓图**预生成客户端 imageId**(事件据此关联,离线也有效,重连补传用同一 id);
    /// baseline 抓图不预生成,由服务器分配 + pHash 去重。
    public async Task<string?> UploadImageAsync(byte[] webp, string trigger, ulong phash, long seq, CancellationToken ct)
    {
        trigger = TriggerMap.ToContract(trigger);         // CaptureReason → 契约 trigger(否则落库脏值)
        string? clientId = trigger == "baseline_random" ? null : NewImageId();
        string? confirmed = await PostImageAsync(webp, trigger, phash, seq, clientId, ct).ConfigureAwait(false);
        if (confirmed is null)
        {
            await _buffer.EnqueueImageAsync(seq, clientId, trigger, phash, webp).ConfigureAwait(false);
            return clientId;   // 触发型返回预生成 id(事件关联);baseline 为 null
        }
        return confirmed;
    }

    private static string NewImageId() => "img_" + Guid.NewGuid().ToString("N");

    /// 实际 HTTP 上传(含 X-Honus-Sig 签名;clientId 非空则带 X-Honus-Image-Id 由服务器沿用)。
    /// 成功返回 imageId,失败返回 null(不落缓冲)。
    private async Task<string?> PostImageAsync(byte[] webp, string trigger, ulong phash, long seq, string? clientId, CancellationToken ct)
    {
        try
        {
            string phashHex = phash.ToString("x16");
            string ts = Now().ToString(System.Globalization.CultureInfo.InvariantCulture);
            string canon = Auth.ImageCanonicalHeaders(_cfg.ExamId, _cfg.SeatId, _cfg.AgentId, seq, trigger, phashHex, ts, clientId ?? "");
            string sig = Auth.ImageSig(_cfg.Psk, canon, webp);

            using var content = new ByteArrayContent(webp);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");

            using var req = new HttpRequestMessage(HttpMethod.Post, _httpUri) { Content = content };
            req.Headers.Add("X-Honus-Exam", _cfg.ExamId);
            req.Headers.Add("X-Honus-Seat", _cfg.SeatId);
            req.Headers.Add("X-Honus-Agent", _cfg.AgentId);
            req.Headers.Add("X-Honus-Seq", seq.ToString());
            req.Headers.Add("X-Honus-Trigger", trigger);
            req.Headers.Add("X-Honus-Phash", phashHex);
            req.Headers.Add("X-Honus-Ts", ts);
            req.Headers.Add("X-Honus-Sig", sig);
            if (clientId is not null) req.Headers.Add("X-Honus-Image-Id", clientId);   // 客户端预生成 id,服务器沿用

            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            await using Stream s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("imageId", out JsonElement idEl) ? idEl.GetString() : null;
        }
        catch { return null; }
    }

    // ---- 底层收发 ----
    private async Task SendRawAsync(WebSocket ws, string json, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private const int MaxFrameBytes = 1 * 1024 * 1024;   // 单条下行消息上限 1MB

    private static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false); }
            catch { return null; }
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (ms.Length > MaxFrameBytes) return null;   // 超限 → 断开
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void AlignSeq(long maxSeq)
    {
        long cur;
        do { cur = Interlocked.Read(ref _seq); if (maxSeq <= cur) return; }
        while (Interlocked.CompareExchange(ref _seq, maxSeq, cur) != cur);
        lock (_seqLock)
            if (maxSeq > _seqCeiling) { _seqCeiling = maxSeq + SeqBlock; _buffer.SaveSeqCeiling(_seqCeiling); }
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public async ValueTask DisposeAsync()
    {
        WebSocket? ws = _ws;
        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { /* ignore */ }
            ws.Dispose();
        }
        _sendLock.Dispose();
        _http.Dispose();
    }
}

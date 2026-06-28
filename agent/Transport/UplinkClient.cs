using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Honus.Agent.Buffer;
using Honus.Agent.Config;

namespace Honus.Agent.Transport;

/// 事件走 WebSocket(实时),图片走 HTTP。断网时落本地缓冲,恢复后续传。
/// M1 骨架:连接 + 发送 + 失败落缓冲。接收循环(ack/config_update/capture_now)、
/// 握手鉴权、断线重连 + 续传留 TODO。
public sealed class UplinkClient : IAsyncDisposable
{
    private readonly Uri _wsUri;
    private readonly Uri _httpUri;
    private readonly string _examId, _seatId, _agentId;
    private readonly LocalBuffer _buffer;
    private readonly ClientWebSocket _ws = new();
    private readonly HttpClient _http = new();
    private long _seq;

    public UplinkClient(AgentConfig cfg, LocalBuffer buffer)
    {
        _wsUri = new Uri($"{cfg.ServerWsBase}/ingest/events?examId={cfg.ExamId}&seatId={cfg.SeatId}&agentId={cfg.AgentId}");
        _httpUri = new Uri($"{cfg.ServerHttpBase}/ingest/images");
        _examId = cfg.ExamId;
        _seatId = cfg.SeatId;
        _agentId = cfg.AgentId;
        _buffer = buffer;
    }

    public long NextSeq() => Interlocked.Increment(ref _seq);

    public async Task ConnectAsync(CancellationToken ct)
    {
        // TODO: 握手鉴权 _ws.Options.SetRequestHeader("X-Honus-Auth", hmac(examId|seatId|agentId));
        //       连上后发 hello;起接收循环处理 hello_ack/ack/config_update/capture_now;断线重连 + 续传。
        try { await _ws.ConnectAsync(_wsUri, ct).ConfigureAwait(false); }
        catch { /* 离线起步:事件先落缓冲,待重连续传 */ }
    }

    /// 发送一条已封装(含 hash/sig)的事件 JSON;失败落缓冲。
    public async Task SendEventAsync(string json, long seq, CancellationToken ct)
    {
        try
        {
            if (_ws.State != WebSocketState.Open) { await _buffer.EnqueueEventAsync(seq, json); return; }
            await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch { await _buffer.EnqueueEventAsync(seq, json); }
    }

    /// 上传一张 WebP,返回服务器分配的 imageId;失败落缓冲并返回 null。
    public async Task<string?> UploadImageAsync(byte[] webp, string trigger, ulong phash, long seq, CancellationToken ct)
    {
        try
        {
            using var content = new ByteArrayContent(webp);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");

            using var req = new HttpRequestMessage(HttpMethod.Post, _httpUri) { Content = content };
            req.Headers.Add("X-Honus-Exam", _examId);
            req.Headers.Add("X-Honus-Seat", _seatId);
            req.Headers.Add("X-Honus-Agent", _agentId);
            req.Headers.Add("X-Honus-Seq", seq.ToString());
            req.Headers.Add("X-Honus-Trigger", trigger);
            req.Headers.Add("X-Honus-Phash", phash.ToString("x16"));
            // TODO: req.Headers.Add("X-Honus-Sig", hmac(headers + sha256(body)));

            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using Stream s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.GetProperty("imageId").GetString();
        }
        catch
        {
            await _buffer.EnqueueImageAsync(seq, trigger, phash, webp);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { /* ignore */ }
        _ws.Dispose();
        _http.Dispose();
    }
}

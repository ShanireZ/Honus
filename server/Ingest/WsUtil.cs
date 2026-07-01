using System.Net.WebSockets;
using System.Text;

namespace Horus.Server.Ingest;

/// WebSocket 文本帧收发小工具。一条 JSON 一帧。
public static class WsUtil
{
    private const int MaxFrameBytes = 1 * 1024 * 1024;   // 单条消息上限 1MB,防无界帧内存耗尽

    /// 接收一整条文本消息(拼接分片)。返回 null 表示对端关闭或超限。
    public static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf, ct); }
            catch (WebSocketException) { return null; }
            catch (OperationCanceledException) { return null; }

            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (ms.Length > MaxFrameBytes) return null;   // 超限 → 断开
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static async Task SendTextAsync(WebSocket ws, string json, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
    }
}

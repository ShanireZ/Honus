using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Horus.Server.Ingest;

/// 在线 Agent 连接注册表 + 每考试最新配置缓存。
/// 用于向连接中的 Agent **推送 config_update**(热更新白名单/阈值/截图参数)等下行帧。
public sealed class AgentHub
{
    private readonly ConcurrentDictionary<string, Conn> _conns = new();      // agentId → 当前连接
    private readonly ConcurrentDictionary<string, string> _config = new();   // examId → 最新配置 JSON

    /// 单个 Agent 连接。发送经信号量串行化(避免 ack 与 config 推送并发写同一 WS)。
    public sealed class Conn
    {
        public required string ExamId { get; init; }
        public required WebSocket Ws { get; init; }
        private readonly SemaphoreSlim _send = new(1, 1);

        public async Task SendAsync(string json, CancellationToken ct)
        {
            if (Ws.State != WebSocketState.Open) return;
            await _send.WaitAsync(ct).ConfigureAwait(false);
            try { await Ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { _send.Release(); }
        }
    }

    public Conn Register(string agentId, string examId, WebSocket ws)
    {
        var c = new Conn { ExamId = examId, Ws = ws };
        if (_conns.TryGetValue(agentId, out Conn? old))
            try { old.Ws.Abort(); } catch { /* 关旧连接:保证每 agent 至多一条活动连接 */ }
        _conns[agentId] = c;   // 重连覆盖旧连接
        return c;
    }

    /// 用序列化器构造 config_update 帧(不用字符串拼接,避免帧结构被破坏)。
    public static string BuildConfigFrame(string configJson)
        => JsonSerializer.Serialize(new { v = 1, type = "config_update", config = JsonNode.Parse(configJson) });

    public void Unregister(string agentId, Conn c)
    {
        // 仅当仍是同一连接才移除,避免误删更晚的重连
        if (_conns.TryGetValue(agentId, out Conn? cur) && ReferenceEquals(cur, c))
            _conns.TryRemove(agentId, out _);
    }

    public string? GetConfig(string examId) => _config.TryGetValue(examId, out string? v) ? v : null;

    /// 存最新配置并推送给该考试所有在线 Agent。返回成功推送到的连接数。
    public async Task<int> PushConfigAsync(string examId, string configJson, CancellationToken ct)
    {
        _config[examId] = configJson;
        string frame = BuildConfigFrame(configJson);
        int n = 0;
        foreach (KeyValuePair<string, Conn> kv in _conns)
        {
            if (kv.Value.ExamId != examId) continue;
            try { await kv.Value.SendAsync(frame, ct); n++; } catch { /* 掉线忽略 */ }
        }
        return n;
    }
}

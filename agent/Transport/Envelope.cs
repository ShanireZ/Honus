using System.Text.Json;
using Honus.Agent.Model;

namespace Honus.Agent.Transport;

/// 事件信封:{ v, type:"event", event:{...}, seq, sig }。见 api-contract-m1.md §1.2。
public static class Envelope
{
    public static string Serialize(AgentEvent e, string sig)
        => JsonSerializer.Serialize(new
        {
            v = 1,
            type = "event",
            @event = e,
            seq = e.Seq,
            sig,
        }, Json.Wire);
}

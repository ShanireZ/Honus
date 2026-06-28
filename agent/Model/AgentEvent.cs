using System.Text.Json.Serialization;

namespace Honus.Agent.Model;

/// 信号类型。序列化为 snake_case(见 Json.Wire),与 api-contract / schema 对齐。
public enum SignalType
{
    WindowFocus,
    BrowserUrl,
    ProcessStart,
    ProcessExit,
    Clipboard,
    AltTabBurst,
    Usb,
    Screenshot,
    Heartbeat,
}

/// 上报事件(封装后含哈希链字段)。字段顺序与 api-contract §0.1 canonical 一致。
public sealed record AgentEvent
{
    [JsonIgnore] public int V { get; init; } = 1;       // 协议版本走信封,不进 event 体

    public required string ExamId { get; init; }
    public required string SeatId { get; init; }
    public required string AgentId { get; init; }
    public required string MachineId { get; init; }
    public double Ts { get; init; }                      // Unix 秒(含小数),本机时钟
    public required SignalType Type { get; init; }
    public Dictionary<string, object?> Payload { get; init; } = new();
    public int Risk { get; init; }                       // 本地初判 0-100
    public string? EvidenceImageId { get; init; }
    public long Seq { get; init; }
    public string? HashPrev { get; init; }
    public string? HashSelf { get; init; }
}

/// 信号源产出的原始信号(未盖时间戳/序号/哈希)。
public sealed record RawSignal(
    SignalType Type,
    Dictionary<string, object?> Payload,
    int Risk = 0,
    bool TriggerCapture = false,
    string? CaptureReason = null);

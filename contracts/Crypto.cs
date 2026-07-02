using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Horus.Contracts;

/// 基础哈希 / HMAC 十六进制封装(小写)。
public static class Crypto
{
    public static string Sha256Hex(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public static string HmacHex(byte[] key, string s)
    {
        using var h = new HMACSHA256(key);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }

    /// 常量时间比较(先各自 SHA256 到固定 32 字节再比,连**长度**都不泄漏)。用于签名 / 管理令牌。
    public static bool FixedTimeEquals(string a, string b)
    {
        Span<byte> ha = stackalloc byte[32];
        Span<byte> hb = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(a), ha);
        SHA256.HashData(Encoding.UTF8.GetBytes(b), hb);
        return CryptographicOperations.FixedTimeEquals(ha, hb);
    }
}

/// 事件 canonical + 哈希/签名。**Agent 与 Server 共用同一实现,保证逐字节一致**。见 api-contract §0.1。
///   hashSelf = SHA256(hashPrev + "\n" + canonicalCore)
///   sig      = HMAC-SHA256(PSK, hashSelf + "\n" + seq)
/// canonicalCore 字段固定顺序: examId, seatId, agentId, machineId, ts, type, payload, risk, evidenceImageId, seq
public static class EventCanonical
{
    public static string Core(AgentEvent e, long seq)
        => JsonSerializer.Serialize(new
        {
            e.ExamId, e.SeatId, e.AgentId, e.MachineId,
            e.Ts, e.Type, e.Payload, e.Risk, e.EvidenceImageId, seq,
        }, Json.Wire);

    public static string HashSelf(string hashPrev, AgentEvent e, long seq)
        => Crypto.Sha256Hex(hashPrev + "\n" + Core(e, seq));

    /// **服务器侧**从落库/收到的**原始 wire 字段**复算 canonicalCore,与 <see cref="Core"/> 产出**逐字节一致**。
    /// 关键点(保证与 Agent 端 typed 序列化对齐):
    ///   • payload 用收到的**原始 JSON 文本**(`JsonElement.GetRawText()` / 库内 payload 列)原样拼接(WriteRawValue),
    ///     不经 Dictionary 往返 —— 杜绝数字格式 / 键序 / 嵌套类型的重序列化漂移。
    ///   • type 用收到的 snake_case 字符串(它正是枚举 `Json.Wire` 序列化后的值,如 "browser_url")。
    ///   • evidenceImageId 为 null 时**省略**该键(与 Core 的 WhenWritingNull 一致)。
    ///   • Utf8JsonWriter 默认用与 JsonSerializer 相同的 `JavaScriptEncoder.Default` 转义 + 相同的 double 最短往返格式。
    /// 用于哈希链完整性复验(闭合 architecture §10.1「服务器不重算 canonical」)。CanonicalTests 锁定与 Core 的逐字节一致。
    public static string CoreRaw(
        string examId, string seatId, string agentId, string machineId,
        double ts, string typeSnake, string payloadRaw, int risk, string? evidenceImageId, long seq)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("examId", examId);
            w.WriteString("seatId", seatId);
            w.WriteString("agentId", agentId);
            w.WriteString("machineId", machineId);
            w.WriteNumber("ts", ts);
            w.WriteString("type", typeSnake);
            w.WritePropertyName("payload");
            w.WriteRawValue(string.IsNullOrEmpty(payloadRaw) ? "{}" : payloadRaw);   // 原样拼接收到的 payload 文本
            w.WriteNumber("risk", risk);
            if (evidenceImageId is not null) w.WriteString("evidenceImageId", evidenceImageId);
            w.WriteNumber("seq", seq);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// 复算 hashSelf 并与给定值常量时间比较。true = 该 hashSelf 确实承诺(绑定)这组 payload / 字段。
    /// 注:仅证明「hashSelf ↔ payload/字段」内部自洽;是否链到真正的前驱由**连续性审计**另判(见 IntegrityAudit)。
    public static bool VerifyHashSelf(
        string hashPrev, string examId, string seatId, string agentId, string machineId,
        double ts, string typeSnake, string payloadRaw, int risk, string? evidenceImageId, long seq, string? hashSelf)
    {
        if (string.IsNullOrEmpty(hashSelf)) return false;
        string recomputed = Crypto.Sha256Hex(hashPrev + "\n" +
            CoreRaw(examId, seatId, agentId, machineId, ts, typeSnake, payloadRaw, risk, evidenceImageId, seq));
        return Crypto.FixedTimeEquals(recomputed, hashSelf);
    }

    /// 事件签名。**仅依赖 hashSelf 字符串与 seq**,故服务器无需重算 canonical 即可验签(M1)。
    public static string Sig(byte[] psk, string hashSelf, long seq)
        => Crypto.HmacHex(psk, hashSelf + "\n" + seq);

    /// 复验 sig 是否 = HMAC(PSK, hashSelf + "\n" + seq)。用于离线完整性审计:hashSelf 是无密钥 SHA256,
    /// 非 PSK 方改 payload 后可重算 hashSelf 使其自洽 —— 唯 sig(HMAC-PSK)能识破,故审计须验 sig。
    public static bool VerifySig(byte[] psk, string? hashSelf, long seq, string? sig)
        => !string.IsNullOrEmpty(sig) && hashSelf is not null && Crypto.FixedTimeEquals(Sig(psk, hashSelf, seq), sig);
}

/// 握手与图片通道的鉴权签名。见 api-contract §1.1 / §2.1。
public static class Auth
{
    /// WebSocket 握手头 X-Horus-Auth = HMAC(PSK, examId|seatId|agentId)。
    public static string Handshake(byte[] psk, string examId, string seatId, string agentId)
        => Crypto.HmacHex(psk, $"{examId}|{seatId}|{agentId}");

    /// 图片上传头 X-Horus-Sig = HMAC(PSK, canonicalHeaders + "\n" + sha256(body))。
    /// canonicalHeaders 采用固定顺序的 "key:value" 换行拼接(见 ImageCanonicalHeaders)。
    public static string ImageSig(byte[] psk, string canonicalHeaders, byte[] body)
    {
        string bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return Crypto.HmacHex(psk, canonicalHeaders + "\n" + bodyHash);
    }

    /// 击键旁路签名 X-Horus-KSig = HMAC(KSK, "keystroke\n" + sha256(body))。
    /// 由**判题后端**(可安全持 KSK,浏览器不持)对整条提交体签名;绑定 seatId/内容,防同网学员机伪造/栽赃。
    /// "keystroke\n" 域分隔前缀:防跨通道签名重用(图片/事件签名不能拿来当击键签名)。
    public static string KeystrokeSig(byte[] ksk, byte[] body)
    {
        string bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return Crypto.HmacHex(ksk, "keystroke\n" + bodyHash);
    }

    /// 图片上传的规范化头串(两端必须一致)。顺序: exam, seat, agent, seq, trigger, phash, ts, imageId。
    /// imageId = 客户端预生成 id(无则传 "");纳入签名防止 X-Horus-Image-Id 被篡改污染证据关联。
    public static string ImageCanonicalHeaders(
        string examId, string seatId, string agentId, long seq, string trigger, string phash, string ts, string imageId = "")
        => string.Join("\n", new[]
        {
            "exam:" + examId,
            "seat:" + seatId,
            "agent:" + agentId,
            "seq:" + seq,
            "trigger:" + trigger,
            "phash:" + phash,
            "ts:" + ts,
            "imageId:" + imageId,
        });
}

/// 事件信封:{ v, type:"event", event:{...}, seq, sig }。见 api-contract §1.2。
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

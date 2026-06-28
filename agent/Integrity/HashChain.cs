using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honus.Agent.Model;

namespace Honus.Agent.Integrity;

/// 事件哈希链 + HMAC 签名。
///   hashSelf = SHA256(hashPrev + "\n" + canonicalCore)
///   sig      = HMAC-SHA256(PSK, hashSelf + "\n" + seq)
/// canonicalCore 字段顺序见 api-contract-m1.md §0.1,两端必须逐字节一致。
/// 非线程安全:调用方需串行化 Seal(保证链顺序与 seq 一致)。
public sealed class HashChain
{
    private readonly byte[] _psk;
    private string _prev;

    public HashChain(byte[] psk, string genesis = "GENESIS")
    {
        _psk = psk;
        _prev = genesis;
    }

    public (string hashPrev, string hashSelf, string sig) Seal(AgentEvent core, long seq)
    {
        string prev = _prev;
        string canon = JsonSerializer.Serialize(new
        {
            core.ExamId, core.SeatId, core.AgentId, core.MachineId,
            core.Ts, core.Type, core.Payload, core.Risk, core.EvidenceImageId, seq,
        }, Json.Wire);

        string self = Sha256Hex(prev + "\n" + canon);
        string sig = HmacHex(_psk, self + "\n" + seq);
        _prev = self;
        return (prev, self, sig);
    }

    private static string Sha256Hex(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string HmacHex(byte[] key, string s)
    {
        using var h = new HMACSHA256(key);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }
}

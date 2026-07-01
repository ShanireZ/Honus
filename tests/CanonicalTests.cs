using Horus.Contracts;
using Xunit;

namespace Horus.Server.Tests;

/// 锁定 canonical / 哈希 / 签名的**逐字节**格式。Agent 与 Server 共用 Horus.Contracts 的这套实现,
/// 此测试即两端一致性的黄金基准——任何字段顺序 / 命名 / 枚举编码漂移都会被抓到。
public class CanonicalTests
{
    private static AgentEvent Sample() => new()
    {
        ExamId = "E1", SeatId = "A07", AgentId = "ag-A07", MachineId = "PC-A07",
        Ts = 1750000000.5, Type = SignalType.BrowserUrl,
        Payload = new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false },
        Risk = 80, Seq = 1287,
    };

    [Fact]
    public void Canonical_字段顺序_camelCase_snake枚举_省略null()
    {
        string canon = EventCanonical.Core(Sample(), 1287);
        const string expected =
            "{\"examId\":\"E1\",\"seatId\":\"A07\",\"agentId\":\"ag-A07\",\"machineId\":\"PC-A07\"," +
            "\"ts\":1750000000.5,\"type\":\"browser_url\"," +
            "\"payload\":{\"process\":\"chrome\",\"url\":\"https://chat.openai.com/\",\"whitelisted\":false}," +
            "\"risk\":80,\"seq\":1287}";   // evidenceImageId 为 null 被省略
        Assert.Equal(expected, canon);
    }

    [Fact]
    public void 哈希与签名_确定且可复验()
    {
        AgentEvent e = Sample();
        string self1 = EventCanonical.HashSelf("GENESIS", e, e.Seq);
        string self2 = EventCanonical.HashSelf("GENESIS", e, e.Seq);
        Assert.Equal(self1, self2);                 // 确定性
        Assert.Equal(64, self1.Length);             // SHA256 hex
        Assert.Matches("^[0-9a-f]{64}$", self1);

        string sig = EventCanonical.Sig(TestApp.Psk, self1, e.Seq);
        Assert.Equal(64, sig.Length);
        // 服务器式验签:仅凭 hashSelf + seq 复算,不依赖 canonical
        Assert.True(Crypto.FixedTimeEquals(sig, EventCanonical.Sig(TestApp.Psk, self1, e.Seq)));
    }

    [Fact]
    public void 握手签名_稳定()
    {
        string a = Auth.Handshake(TestApp.Psk, "E1", "A07", "ag-A07");
        string b = Auth.Handshake(TestApp.Psk, "E1", "A07", "ag-A07");
        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }
}

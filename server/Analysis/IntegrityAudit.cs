using Horus.Contracts;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Analysis;

/// 哈希链完整性审计(M3·取证强化)。对某场考试的 live 事件做**离线复验**,回答两个独立问题:
///
///   ① **锚点自洽(hashOk)**:每条事件的 `hash_self` 是否确实 = SHA256(`hash_prev` + "\n" + canonicalCore(落库字段))?
///      —— 从**落库的 payload/字段**逐字节复算(见 EventCanonical.CoreRaw)。不符 = 落库后 payload/字段被改动
///      (DB 层篡改 / 存储损坏),锚点不再承诺其内容。
///   ② **链连续(chainOk)**:按 (agent_id, seq) 升序,每条事件的 `hash_prev` 是否 = 前一条事件的 `hash_self`?
///      首条应 = "GENESIS"。断裂 = 事件被**删除 / 插入 / 重排**(seq 空洞属正常——事件与图片共用序号空间,
///      链只连**事件子序列**;此处只比较相邻事件,不要求 seq 连续)。
///
/// **诚实边界(architecture §10.1)**:持 PSK 的学员机可构造**自洽的**伪造链(hashOk+chainOk 全过),
/// 故本审计**不**能证明"内容为真",只能证明"落库后未被无 PSK 方改动 / 未被静默删增"。内容真伪靠截图 / 视觉 / 人工裁决。
/// 归档清理非关键事件后整链会断(§13.2),故连续性审计只对 **未归档(live)** 考试有意义。
/// **迁移前旧数据**:events.machine_id 是 M3 才加的列(旧行 NULL),而 canonicalCore 含 machineId → 旧事件无从复算 hashSelf,
/// 归入 `unverifiable` 单列(既不算 hashOk 也不当篡改),**绝不对合法历史数据误报"篡改"**。
public static class IntegrityAudit
{
    public sealed record Issue(long Id, long Seq, string Detail);

    public sealed record AgentChain(
        string AgentId, string SeatId, int Total, int HashOk, int ChainOk, int Unverifiable,
        IReadOnlyList<Issue> HashMismatches, IReadOnlyList<Issue> ContinuityBreaks)
    {
        // Ok = 未发现篡改证据。Unverifiable(迁移前缺 machine_id)不算失败,但也**不是**"已验证干净",单列诚实标注。
        public bool Ok => HashMismatches.Count == 0 && ContinuityBreaks.Count == 0;
    }

    public sealed record Report(
        string ExamId, int TotalEvents, int TotalHashOk, int TotalChainOk, int TotalUnverifiable,
        IReadOnlyList<AgentChain> Agents)
    {
        public bool Ok => Agents.All(a => a.Ok);
    }

    private sealed record Row(
        long Id, string SeatId, string AgentId, string? MachineId, long Seq, double Ts,
        string Type, string Payload, int Risk, string? EvidenceImageId, string? HashPrev, string? HashSelf);

    /// 在**只读连接**上执行(调用方走 db.Read)。按 agent 分组、seq 升序复验。
    public static Report Run(SqliteConnection conn, string examId)
    {
        // 一次性拉取该考试全部事件,按 (agent_id, seq) 升序 —— 复现 Agent 封链的先后。
        var byAgent = new Dictionary<string, List<Row>>();
        var seatOf = new Dictionary<string, string>();
        using (SqliteCommand c = conn.Cmd(
            @"SELECT id, seat_id, agent_id, machine_id, seq, ts, type, payload, risk, evidence_image_id, hash_prev, hash_self
              FROM events WHERE exam_id=@e ORDER BY agent_id, seq", ("@e", examId)))
        using (SqliteDataReader r = c.ExecuteReader())
        {
            while (r.Read())
            {
                var row = new Row(
                    r.GetInt64(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetDouble(5),
                    r.GetString(6), r.IsDBNull(7) ? "{}" : r.GetString(7), r.GetInt32(8),
                    r.IsDBNull(9) ? null : r.GetString(9),
                    r.IsDBNull(10) ? null : r.GetString(10),
                    r.IsDBNull(11) ? null : r.GetString(11));
                if (!byAgent.TryGetValue(row.AgentId, out List<Row>? list)) byAgent[row.AgentId] = list = new();
                list.Add(row);
                seatOf[row.AgentId] = row.SeatId;   // 取该 agent 见到的 seat(通常固定)
            }
        }

        var agents = new List<AgentChain>();
        int totalEvents = 0, totalHashOk = 0, totalChainOk = 0, totalUnverifiable = 0;

        foreach ((string agentId, List<Row> rows) in byAgent)
        {
            var hashMiss = new List<Issue>();
            var chainMiss = new List<Issue>();
            int unverifiable = 0, hashOkCount = 0;
            string? prevSelf = null;   // null → 尚无前驱(首条应链到 GENESIS)

            foreach (Row e in rows)
            {
                // ① 锚点自洽:从落库字段(含 machine_id)复算 hashSelf,与落库 hash_self 常量时间比对。
                //    **迁移前旧事件缺 machine_id**(M3 才加列,旧行为 NULL):canonicalCore 含 machineId,无从复算,
                //    诚实标注为「不可验(pre-M3)」——既不算 hashOk 也**不当作篡改**,避免对合法历史数据系统性假阳性。
                if (e.MachineId is null)
                {
                    unverifiable++;
                }
                else
                {
                    bool hashOk = EventCanonical.VerifyHashSelf(
                        e.HashPrev ?? "GENESIS", examId, e.SeatId, agentId, e.MachineId, e.Ts,
                        e.Type, e.Payload, e.Risk, e.EvidenceImageId, e.Seq, e.HashSelf);
                    if (hashOk) hashOkCount++;
                    else hashMiss.Add(new Issue(e.Id, e.Seq, "hash_self 与落库 payload/字段不符(疑落库后改动)"));
                }

                // ② 链连续:hash_prev 应 = 前一条的 hash_self;首条应 = GENESIS。不依赖 machineId,旧事件也能验。
                string expectedPrev = prevSelf ?? "GENESIS";
                string gotPrev = e.HashPrev ?? "";
                if (Crypto.FixedTimeEquals(gotPrev, expectedPrev)) totalChainOk++;
                else chainMiss.Add(new Issue(e.Id, e.Seq,
                    $"hash_prev 与前驱 hash_self 不符(疑删除/插入/重排):期望={Short(expectedPrev)} 实得={Short(gotPrev)}"));

                prevSelf = e.HashSelf;
            }

            totalEvents += rows.Count;
            totalHashOk += hashOkCount;
            totalUnverifiable += unverifiable;
            agents.Add(new AgentChain(
                agentId, seatOf[agentId], rows.Count, hashOkCount, rows.Count - chainMiss.Count, unverifiable,
                hashMiss, chainMiss));
        }

        return new Report(examId, totalEvents, totalHashOk, totalChainOk, totalUnverifiable, agents);
    }

    private static string Short(string h) => h.Length <= 12 ? h : h[..12] + "…";
}

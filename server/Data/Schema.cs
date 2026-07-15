using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Data;

/// 从内嵌资源读取权威 DDL(schema.sql)并应用。
/// M1 剥离 sqlite-vec 的 vec0 虚表(需 vec0 扩展,属 M3),其余表照建。
public static class Schema
{
    /// 按资源名后缀读取内嵌 DDL(如 "schema.sql" / "schema-archive.sql")。
    public static string LoadNamed(string endsWith)
    {
        Assembly asm = typeof(Schema).Assembly;
        string res = asm.GetManifestResourceNames().First(n => n.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase));
        using Stream s = asm.GetManifestResourceStream(res)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static string LoadDdl() => LoadNamed("schema.sql");

    public static void Apply(SqliteConnection conn)
    {
        var kept = SplitStatements(LoadDdl())
            .Where(st => !st.Contains("USING vec0", StringComparison.OrdinalIgnoreCase))  // M1 跳过 CLIP 向量虚表
            .ToList();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = string.Join(";\n", kept) + ";";
        cmd.ExecuteNonQuery();

        Migrate(conn);
    }

    /// 应用归档库 DDL(M3 归档作业首次运行时对 archive 文件调用,idempotent)。
    public static void ApplyArchive(SqliteConnection conn)
    {
        var stmts = SplitStatements(LoadNamed("schema-archive.sql")).ToList();
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = string.Join(";\n", stmts) + ";";
            cmd.ExecuteNonQuery();
        }
        // 既有 archive 库(CREATE IF NOT EXISTS 不补列)补列:
        //   machine_id —— 归档事件锚点日后独立复算 hash_self 需 machineId;
        //   server_risk —— 留存"为何被归档为关键"的取证依据(旁注,不入 canonical)。
        AddColumnIfMissing(conn, "archive_events", "machine_id", "TEXT");
        AddColumnIfMissing(conn, "archive_events", "server_risk", "INTEGER");
    }

    /// 幂等列迁移:CREATE TABLE IF NOT EXISTS 不会给**已存在**的表补列,故对既有 dev DB 显式 ADD COLUMN。
    /// 新建 DB 已含新列(DDL 里有),此处对其为 no-op。
    private static void Migrate(SqliteConnection conn)
    {
        AddColumnIfMissing(conn, "events", "server_risk", "INTEGER");   // M2:服务器侧风险复判
        AddColumnIfMissing(conn, "events", "machine_id", "TEXT");       // M3:落 machineId 以支持哈希链离线复验
        AddColumnIfMissing(conn, "oidc_sessions", "user_type", "TEXT"); // M4·RBAC:会话绑定的角色('elder'/'disciple')
        // 第三轮 F1/F2:把"视觉分析闩锁"从 uploaded_to_ocr(现仅表"真出网")拆到独立列。
        // 旧库 uploaded_to_ocr=1 的图视为已终结(analysis_state 缺省 0 会被重扫,故迁移时把旧 =1 回填为已终结)。
        AddColumnIfMissing(conn, "images", "analysis_state", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "images", "analysis_attempts", "INTEGER NOT NULL DEFAULT 0");
        BackfillAnalysisState(conn);
        AddColumnIfMissing(conn, "suspicious_queue", "source", "TEXT NOT NULL DEFAULT 'suspicion'"); // M5:区分可裁决作弊线索(默认)与只读健康告警
        MigrateHeartbeatPk(conn);                                       // 心跳 PK 补 exam/seat 维(旧库重建·心跳短暂可弃)
    }

    /// 旧 PK=(agent_id, ts) → 新 PK=(exam_id, seat_id, agent_id, ts)。心跳是 ~90s 窗口的在线指示、且归档时清理,
    /// 丢弃旧行至多损失几分钟"谁在线"(每 30s 自愈),故迁移直接 DROP+重建(CREATE IF NOT EXISTS 只对新库建了新 PK)。
    private static void MigrateHeartbeatPk(SqliteConnection conn)
    {
        // 检测:旧表里 exam_id 不在 PK(pk=0)→ 需迁移;新表 exam_id 在 PK(pk>0)→ 跳过。
        bool needMigrate;
        using (SqliteCommand q = conn.CreateCommand())
        {
            q.CommandText = "SELECT pk FROM pragma_table_info('agent_heartbeats') WHERE name='exam_id'";
            object? pk = q.ExecuteScalar();
            needMigrate = pk is not null && Convert.ToInt64(pk) == 0;
        }
        if (!needMigrate) return;

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            @"DROP TABLE agent_heartbeats;
              CREATE TABLE agent_heartbeats (
                agent_id TEXT NOT NULL, exam_id TEXT NOT NULL, seat_id TEXT NOT NULL,
                ts REAL NOT NULL, status TEXT NOT NULL,
                PRIMARY KEY (exam_id, seat_id, agent_id, ts));
              CREATE INDEX IF NOT EXISTS ix_hb_exam_ts ON agent_heartbeats(exam_id, ts);";
        cmd.ExecuteNonQuery();
    }

    /// 迁移旧库:旧语义下 uploaded_to_ocr=1 表示"已认领/已分析",新语义把闩锁移到 analysis_state。
    /// 故把旧的 uploaded_to_ocr=1 且 analysis_state=0 的行标为已终结,避免升级后补偿重扫把全部历史图重新送分析。幂等(重跑 no-op)。
    private static void BackfillAnalysisState(SqliteConnection conn)
    {
        using SqliteCommand c = conn.CreateCommand();
        c.CommandText = "UPDATE images SET analysis_state=1 WHERE analysis_state=0 AND uploaded_to_ocr=1";
        c.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string decl)
    {
        bool exists;
        using (SqliteCommand q = conn.CreateCommand())
        {
            q.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name=$c";
            q.Parameters.AddWithValue("$c", column);
            exists = q.ExecuteScalar() is not null;
        }
        if (exists) return;
        using SqliteCommand alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {decl}";
        alter.ExecuteNonQuery();
    }

    /// 先剥离 '--' 行注释,再按 ';' 切分语句。
    /// **必须先去注释**:否则注释里的分号(如 "与契约 §1.4 一致;seq")会劈裂 DDL 语句。
    /// 该 DDL 无包含 '--' 或 ';' 的字符串字面量,故按行去注释 + 简单切分是安全的。
    private static IEnumerable<string> SplitStatements(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        foreach (string line in sql.Split('\n'))
        {
            int c = line.IndexOf("--", StringComparison.Ordinal);
            sb.Append(c >= 0 ? line[..c] : line).Append('\n');
        }
        return sb.ToString().Split(';').Select(s => s.Trim()).Where(s => s.Length > 0);
    }
}

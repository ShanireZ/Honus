using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Honus.Server.Data;

/// 从内嵌资源读取权威 DDL(schema.sql)并应用。
/// M1 剥离 sqlite-vec 的 vec0 虚表(需 vec0 扩展,属 M3),其余表照建。
public static class Schema
{
    public static string LoadDdl()
    {
        Assembly asm = typeof(Schema).Assembly;
        string res = asm.GetManifestResourceNames().First(n => n.EndsWith("schema.sql", StringComparison.OrdinalIgnoreCase));
        using Stream s = asm.GetManifestResourceStream(res)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static void Apply(SqliteConnection conn)
    {
        var kept = SplitStatements(LoadDdl())
            .Where(st => !st.Contains("USING vec0", StringComparison.OrdinalIgnoreCase))  // M1 跳过 CLIP 向量虚表
            .ToList();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = string.Join(";\n", kept) + ";";
        cmd.ExecuteNonQuery();
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

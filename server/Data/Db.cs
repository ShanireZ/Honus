using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Data;

/// SQLite 访问层:**单写连接(写锁串行) + 只读连接池(WAL 并发读)**。
///
/// 写:所有落库经 <see cref="Write"/> 在写锁内串行(单写者,杜绝 "database is locked" 写-写冲突)。
/// 读:纯只读查询经 <see cref="Read"/> 从**只读连接池**租借一条连接——WAL 下读不阻塞写、写不阻塞读,且
///     **读与读之间也并发**(池大小内):看板 6 个 GET + 完整性审计(全考试事件 SHA256)+ 归档 copy 读**互不串行**,
///     避免一个慢只读操作阻塞所有交互看板轮询(闭合 architecture §10.2「看板走独立只读连接」的读-读串行残留)。
///
/// **:memory: 例外**:内存库每连接独立(且不支持 WAL),无法开第二条连接看到同一份数据,故内存模式下
///     无只读池,Read 回退写连接 + 写锁(语义与旧版单连接一致,测试不受影响)。图片原图存文件系统,不入 DB(见 Storage)。
public sealed class Db : IDisposable
{
    private readonly SqliteConnection _write;
    private readonly object _writeGate = new();
    private readonly SemaphoreSlim? _readSlots;               // 只读并发闸(= 池大小);:memory: 下 null
    private readonly ConcurrentBag<SqliteConnection>? _readPool;
    private readonly List<SqliteConnection> _allReads = new();   // 供 Dispose 释放

    public Db(string dataSource, int readPoolSize = 4)
    {
        bool isMemory = dataSource == ":memory:";

        // Pooling=false:连接随 Db 生命周期常驻的单例/池,连接池对其无益;
        // 且池化会在 Dispose 后仍扣着文件句柄(-wal/-shm 无法释放),妨碍归档 VACUUM / 清理。
        _write = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Pooling = false,
        }.ToString());
        _write.Open();
        Exec(_write, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Schema.Apply(_write);

        if (!isMemory)
        {
            // 只读连接池:Mode=ReadOnly 从物理上杜绝经此误写;WAL 让每条读已提交快照而不阻塞写者。
            int n = Math.Clamp(readPoolSize, 1, 16);
            _readSlots = new SemaphoreSlim(n, n);
            _readPool = new ConcurrentBag<SqliteConnection>();
            for (int i = 0; i < n; i++)
            {
                var rc = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = dataSource,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
                rc.Open();
                Exec(rc, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
                _readPool.Add(rc);
                _allReads.Add(rc);
            }
        }
    }

    /// 写(或读改写混合的原子序列):单写连接 + 写锁,单写者串行。
    public T Write<T>(Func<SqliteConnection, T> body)
    {
        lock (_writeGate) return body(_write);
    }

    public void Write(Action<SqliteConnection> body)
    {
        lock (_writeGate) body(_write);
    }

    /// 纯只读查询:从只读连接池租借一条(WAL 并发读,不占写锁,读-读并发)。:memory: 回退写连接 + 写锁。
    /// 注意:仅用于**无副作用**的 SELECT;任何写/读改写序列必须走 <see cref="Write"/> 以保原子性。
    public T Read<T>(Func<SqliteConnection, T> body)
    {
        if (_readPool is null) { lock (_writeGate) return body(_write); }   // :memory: 回退
        _readSlots!.Wait();
        SqliteConnection? c = null;
        try
        {
            _readPool.TryTake(out c);   // 闸已保证有空闲连接可取
            return body(c!);
        }
        finally
        {
            if (c is not null) _readPool.Add(c);
            _readSlots.Release();
        }
    }

    /// 兼容旧调用点:Locked == Write(既有语义 = 写锁串行)。新代码只读请显式用 Read。
    public T Locked<T>(Func<SqliteConnection, T> body) => Write(body);
    public void Locked(Action<SqliteConnection> body) => Write(body);

    private static void Exec(SqliteConnection conn, string sql)
    {
        using SqliteCommand c = conn.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (SqliteConnection rc in _allReads) rc.Dispose();
        _readSlots?.Dispose();
        _write.Dispose();
    }
}

/// SqliteCommand 参数绑定小工具(null → DBNull)。
public static class DbExtensions
{
    public static SqliteCommand Cmd(this SqliteConnection conn, string sql, params (string name, object? val)[] ps)
    {
        SqliteCommand c = conn.CreateCommand();
        c.CommandText = sql;
        foreach ((string name, object? val) in ps)
            c.Parameters.AddWithValue(name, val ?? DBNull.Value);
        return c;
    }

    /// 该考试是否正在归档 / 已归档(`archiving`/`archived`)——归档窗口内 ingest 应短路,
    /// 否则"读快照→DELETE WHERE exam_id"之间到达的新数据会被无锚点删掉(闭合 architecture §13.2 late-ingest 竞态)。
    /// 未建考试(status=null)/active/ended 均放行(不改既有 ingest 行为)。
    public static bool IsExamSealed(this SqliteConnection conn, string examId)
    {
        using SqliteCommand c = conn.Cmd("SELECT status FROM exams WHERE exam_id=@e", ("@e", examId));
        return c.ExecuteScalar() as string is "archiving" or "archived";
    }
}

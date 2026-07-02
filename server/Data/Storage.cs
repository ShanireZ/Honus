using System.Text;

namespace Horus.Server.Data;

/// 截图原图存文件系统:images/&lt;exam&gt;/&lt;seat&gt;/&lt;imageId&gt;.webp。DB 只存相对路径指针。
/// **原图永不出网**(architecture §5)。
public sealed class Storage
{
    private readonly string _root;

    public Storage(string dataDir)
    {
        _root = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(Path.Combine(_root, "images"));
    }

    /// 数据根(绝对路径)。归档作业据此定位 archive 库文件与冷存目录。
    public string Root => _root;

    /// 相对路径(存 DB)。exam/seat 做路径安全净化,防目录穿越。
    public static string RelPath(string examId, string seatId, string imageId)
        => $"images/{Safe(examId)}/{Safe(seatId)}/{imageId}.webp";

    public async Task<string> SaveWebpAsync(string examId, string seatId, string imageId, byte[] bytes)
    {
        string rel = RelPath(examId, seatId, imageId);
        string full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes);
        return rel;
    }

    /// 由相对路径还原绝对路径以读取原图。返回 null 表示越界(拒绝服务)。
    public string? Resolve(string relPath)
    {
        string full = Path.GetFullPath(Path.Combine(_root, relPath));
        // 边界须带路径分隔符,否则 "C:\data" 会误匹配 "C:\data-evil\..."
        string rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    // ---- M3 归档:证据图迁冷存 / 清理非关键图 ----

    /// 归档冷存相对路径:archive/&lt;exam&gt;/&lt;seat&gt;/&lt;imageId&gt;.webp。
    public static string ArchiveRelPath(string examId, string seatId, string imageId)
        => $"archive/{Safe(examId)}/{Safe(seatId)}/{imageId}.webp";

    /// 证据图迁入冷存,返回新相对路径。**幂等**:源在则移动;源已不在但目标已存在(重跑)→ 直接返回;
    /// 源与目标都在(重跑残留)→ 删源保目标。目录自动创建。
    public string MoveToArchive(string liveRel, string examId, string seatId, string imageId)
    {
        string archiveRel = ArchiveRelPath(examId, seatId, imageId);
        string dstFull = Path.Combine(_root, archiveRel);
        Directory.CreateDirectory(Path.GetDirectoryName(dstFull)!);
        string? srcFull = Resolve(liveRel);
        bool srcOk = srcFull is not null && File.Exists(srcFull);
        bool dstOk = File.Exists(dstFull);
        if (srcOk && !dstOk) File.Move(srcFull!, dstFull);
        else if (srcOk && dstOk) File.Delete(srcFull!);   // 目标已在(重跑),删残留源
        return archiveRel;
    }

    /// 删除 live 原图(清理非关键)。越界 / 不存在均静默忽略。
    public void DeleteLive(string liveRel)
    {
        string? f = Resolve(liveRel);
        if (f is not null && File.Exists(f)) File.Delete(f);
    }

    private static string Safe(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "_" : sb.ToString();
    }
}

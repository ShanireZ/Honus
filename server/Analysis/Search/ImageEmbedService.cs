using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Analysis.Search;

/// M3 按图搜图:后台把**证据图**(is_evidence=1)嵌入为向量(补扫·不占 ingest 热路径)。
/// 仅嵌证据/可疑图(owner 决策·省算力);原图永不出网(嵌入器内送派生图)。嵌入器未注册时整体 no-op。
public sealed class ImageEmbedService(
    Db db, Storage storage, ImageSearchStore store, ServerConfig cfg, IServiceProvider sp, ILogger<ImageEmbedService> log)
    : BackgroundService
{
    private readonly IImageEmbedder? embedder = sp.GetService<IImageEmbedder>();   // 嵌入器关时未注册 → null → no-op

    public bool Enabled => embedder is { Enabled: true };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Enabled || cfg.EmbedBackstopMinutes <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(cfg.EmbedBackstopMinutes));
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); } catch (Exception ex) { log.LogWarning(ex, "嵌入补扫异常"); }
            try { if (!await timer.WaitForNextTickAsync(ct)) break; } catch (OperationCanceledException) { break; }
        }
    }

    /// 嵌入一批尚无 embedding 的证据图;返回本次嵌入数。可手动触发(测试)。
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!Enabled) return 0;
        List<(string id, string path)> todo = db.Read(conn =>
        {
            var l = new List<(string, string)>();
            using SqliteCommand c = conn.Cmd(
                @"SELECT i.image_id, i.file_path FROM images i JOIN exams e ON i.exam_id=e.exam_id
                  WHERE i.is_evidence=1 AND e.status NOT IN ('archiving','archived')
                    AND NOT EXISTS (SELECT 1 FROM image_embeddings ie WHERE ie.image_id=i.image_id)
                  ORDER BY i.recv_ts LIMIT 200");
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) l.Add((r.GetString(0), r.GetString(1)));
            return l;
        });

        int n = 0;
        foreach ((string id, string path) in todo)
        {
            if (ct.IsCancellationRequested) break;
            float[]? vec = await EmbedFileAsync(id, path, ct);
            if (vec is not null) n++;
        }
        if (n > 0) log.LogInformation("按图搜图:嵌入 {N} 张证据图", n);
        return n;
    }

    /// 即时嵌入单图(搜索时查询图未嵌入则补嵌)。返回向量或 null。
    public async Task<float[]?> EmbedImageAsync(string imageId, CancellationToken ct)
    {
        if (!Enabled) return null;
        string? path = db.Read<string?>(conn =>
        {
            using SqliteCommand c = conn.Cmd("SELECT file_path FROM images WHERE image_id=@id", ("@id", imageId));
            return c.ExecuteScalar() as string;
        });
        return path is null ? null : await EmbedFileAsync(imageId, path, ct);
    }

    private async Task<float[]?> EmbedFileAsync(string imageId, string relPath, CancellationToken ct)
    {
        string? full = storage.Resolve(relPath);
        if (full is null || !File.Exists(full)) return null;
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(full, ct); } catch { return null; }
        float[]? vec = await embedder!.EmbedAsync(bytes, ct);
        if (vec is null || vec.Length == 0) return null;
        store.Save(imageId, vec, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        return vec;
    }
}

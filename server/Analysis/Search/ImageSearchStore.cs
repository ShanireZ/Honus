using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Analysis.Search;

/// M3 按图搜图:embedding 存取(普通 BLOB 表)+ 暴力余弦检索(规模小·无需 sqlite-vec)。
public sealed class ImageSearchStore(Db db)
{
    public void Save(string imageId, float[] vec, double now)
    {
        byte[] blob = VecMath.ToBytes(vec);
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"INSERT INTO image_embeddings (image_id,dim,embedding,embedded_at) VALUES (@id,@d,@e,@t)
                  ON CONFLICT(image_id) DO UPDATE SET dim=@d, embedding=@e, embedded_at=@t",
                ("@id", imageId), ("@d", vec.Length), ("@e", blob), ("@t", now));
            c.ExecuteNonQuery();
        });
    }

    public bool Has(string imageId) => db.Read(conn =>
    {
        using SqliteCommand c = conn.Cmd("SELECT 1 FROM image_embeddings WHERE image_id=@id", ("@id", imageId));
        return c.ExecuteScalar() is not null;
    });

    public float[]? Get(string imageId) => db.Read<float[]?>(conn =>
    {
        using SqliteCommand c = conn.Cmd("SELECT embedding FROM image_embeddings WHERE image_id=@id", ("@id", imageId));
        using SqliteDataReader r = c.ExecuteReader();
        return r.Read() && !r.IsDBNull(0) ? VecMath.FromBytes((byte[])r.GetValue(0)) : null;
    });

    /// 该考试所有已嵌入图(与 images 关联过滤 exam)。
    public List<(string id, float[] vec)> GetExam(string examId) => db.Read(conn =>
    {
        var list = new List<(string, float[])>();
        using SqliteCommand c = conn.Cmd(
            @"SELECT ie.image_id, ie.embedding FROM image_embeddings ie
              JOIN images i ON ie.image_id=i.image_id WHERE i.exam_id=@e", ("@e", examId));
        using SqliteDataReader r = c.ExecuteReader();
        while (r.Read()) if (!r.IsDBNull(1)) list.Add((r.GetString(0), VecMath.FromBytes((byte[])r.GetValue(1))));
        return list;
    });

    /// 暴力余弦 top-N(排除自身)。返回 (imageId, score) 降序。
    /// cosineFloor:低于此分的帧视为无关被过滤(默认 0 不过滤);n 会被钳到 [1,100](B1/B2)。
    public static List<(string id, double score)> TopN(float[] query, List<(string id, float[] vec)> corpus, int n, string? excludeId, double cosineFloor = 0.0)
        => corpus.Where(c => c.id != excludeId)
                 .Select(c => (c.id, score: VecMath.Cosine(query, c.vec)))
                 .Where(x => x.score >= cosineFloor)
                 .OrderByDescending(x => x.score)
                 .Take(n <= 0 ? 20 : Math.Clamp(n, 1, 100))
                 .ToList();
}

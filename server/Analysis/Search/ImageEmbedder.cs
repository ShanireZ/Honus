using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Horus.Server.Analysis.Vision;
using Horus.Server.Config;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Analysis.Search;

/// M3 CLIP 按图搜图:图像 → 向量嵌入器(provider-agnostic·同视觉的 Mock/OpenAI 兼容模式)。
/// 用途:按已知作弊图捞全场相似帧。**规模小 → C# 暴力余弦 KNN,不依赖 sqlite-vec 原生扩展**。
public interface IImageEmbedder
{
    bool Enabled { get; }
    int Dim { get; }
    /// 返回单位化(或原始)向量;失败返回 null(由补扫重试)。
    Task<float[]?> EmbedAsync(byte[] imageBytes, CancellationToken ct);
}

/// 确定性 Mock:同图 → 同向量(cosine 1.0 自匹配),不同图 → 不同向量。测试/联调用,不出网。
public sealed class MockImageEmbedder(int dim = 512) : IImageEmbedder
{
    public bool Enabled => true;
    public int Dim { get; } = dim < 1 ? 1 : dim;

    public Task<float[]?> EmbedAsync(byte[] imageBytes, CancellationToken ct)
    {
        byte[] h = SHA256.HashData(imageBytes);
        ulong s = BitConverter.ToUInt64(h, 0) | 1UL;   // 非零种子
        var v = new float[Dim];
        for (int i = 0; i < Dim; i++)
        {
            s ^= s << 13; s ^= s >> 7; s ^= s << 17;   // xorshift64,种子=图哈希 → 每图确定性伪随机
            v[i] = ((long)(s % 20000) - 10000) / 10000f;
        }
        VecMath.Normalize(v);
        return Task.FromResult<float[]?>(v);
    }
}

/// OpenAI 兼容 `/v1/embeddings`:送**派生图**(降采样+剥元数据·原图不出网,复用 VisionImagePrep)的 base64 data URI。
/// **默认复用视觉 baseUrl/key**(KEY一致·境内 provider)。⚠️ 前提:端点须有**图像/多模态 embedding 模型**;
/// 纯文本 embedding 端点不适用(需换本地 ONNX 适配·同一接口可切)。
public sealed class OpenAiImageEmbedder(HttpClient http, string endpoint, string model, string apiKey, int dim, ServerConfig cfg, ILogger<OpenAiImageEmbedder> log)
    : IImageEmbedder
{
    public bool Enabled => true;
    public int Dim { get; } = dim;

    public async Task<float[]?> EmbedAsync(byte[] imageBytes, CancellationToken ct)
    {
        byte[] derived = VisionImagePrep.Prepare(imageBytes, cfg, mustStrip: false) ?? imageBytes;   // 原图永不出网
        string dataUri = "data:image/webp;base64," + Convert.ToBase64String(derived);
        string reqJson = JsonSerializer.Serialize(new { model, input = new[] { dataUri } });
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            { Content = new StringContent(reqJson, Encoding.UTF8, "application/json") };
            if (!string.IsNullOrEmpty(apiKey)) req.Headers.Add("Authorization", "Bearer " + apiKey);
            using HttpResponseMessage resp = await http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) { log.LogWarning("embeddings 端点非 200:{S}", (int)resp.StatusCode); return null; }
            return ParseEmbedding(body);
        }
        catch (Exception ex) { log.LogWarning(ex, "图像嵌入失败"); return null; }
    }

    /// 解析 OpenAI embeddings 响应:{ data:[{ embedding:[...] }] }。
    internal static float[]? ParseEmbedding(string json)
    {
        try
        {
            using JsonDocument d = JsonDocument.Parse(json);
            if (!d.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return null;
            if (!data[0].TryGetProperty("embedding", out JsonElement emb) || emb.ValueKind != JsonValueKind.Array) return null;
            var v = new float[emb.GetArrayLength()];
            int i = 0;
            foreach (JsonElement e in emb.EnumerateArray()) v[i++] = e.TryGetSingle(out float f) ? f : 0f;
            return v.Length > 0 ? v : null;
        }
        catch { return null; }
    }
}

/// 向量小工具:float32 ↔ BLOB 字节(小端)、单位化、余弦相似度。
public static class VecMath
{
    public static byte[] ToBytes(float[] v)
    {
        var b = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, b, 0, b.Length);
        return b;
    }

    public static float[] FromBytes(byte[] b)
    {
        var v = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, v, 0, v.Length * 4);
        return v;
    }

    public static void Normalize(float[] v)
    {
        double n = 0;
        for (int i = 0; i < v.Length; i++) n += v[i] * (double)v[i];
        n = Math.Sqrt(n);
        if (n > 1e-12) for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / n);
    }

    /// 余弦相似度(不假设已单位化)。维度不一致返回 -1(不匹配)。
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return -1;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * (double)b[i]; na += a[i] * (double)a[i]; nb += b[i] * (double)b[i]; }
        double denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom > 1e-12 ? dot / denom : -1;
    }
}

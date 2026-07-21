using System.IO;
using Microsoft.AspNetCore.Http;

namespace Horus.Server.Ingest;

/// 图片 / 击键等 HTTP 通道共用的 body 读取 + 显式大小上限。
///
/// 设计:在分配 MemoryStream 之前先按 <c>Content-Length</c> 拒绝超限(防御:未鉴权者也能触发的缓冲放大攻击),
/// 再按实测长度兜底(分块无 Content-Length 时)。两路 ingest 共用同一份,避免安全逻辑日后分叉
/// (质量#5 / BUG#1 同类分叉的体现)。
///
/// 返回三元组:成功时 <c>(Body, null, null)</c>;超限返回 <c>(null, 413, "too_large")</c>;
/// <paramref name="allowEmpty"/> 为 <c>false</c> 且 body 为空时返回 <c>(null, 400, "empty_body")</c>。
internal static class IngestBody
{
    public static async Task<(byte[]? Body, int? StatusCode, string? Error)> ReadWithLimitAsync(
        HttpRequest req, int maxBytes, bool allowEmpty = false)
    {
        // 1) 声明了超大 Content-Length 的先拒(不缓冲)——前置到最前,未鉴权者也触发不了大缓冲。
        if (req.ContentLength is long declared && declared > maxBytes)
            return (null, 413, "too_large");

        // 2) 缓冲整条 body(既用于验签也用于解析);流读一次即耗尽,故先整段读入。
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms);

        // 3) 无 Content-Length(分块)时的兜底上限(Kestrel 全局上限之内更早收敛)。
        if (ms.Length > maxBytes)
            return (null, 413, "too_large");

        // 4) 空 body(声明 allowEmpty=false 时):不落库坏图。
        if (ms.Length == 0 && !allowEmpty)
            return (null, 400, "empty_body");

        return (ms.ToArray(), null, null);
    }
}

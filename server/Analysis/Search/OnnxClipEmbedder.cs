using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Horus.Server.Analysis.Search;

/// M3 按图搜图·CLIP 预处理:图像字节 → CHW float 张量(resize+center-crop 到 224² + CLIP 归一化)。
/// **与嵌入器解耦 → 可单测**(纯像素运算,不碰 ONNX 运行时)。归一化常量取 CLIP ViT-B/32 官方值。
public static class ClipPreprocess
{
    public const int Size = 224;
    private static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };   // RGB
    private static readonly float[] Std = { 0.26862954f, 0.26130258f, 0.27577711f };

    /// 返回 float[3*224*224](通道优先 CHW)。decode 失败抛(由调用方兜)。
    public static float[] ToTensor(byte[] imageBytes)
    {
        using Image<Rgb24> img = Image.Load<Rgb24>(imageBytes);
        img.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(Size, Size),
            Mode = ResizeMode.Crop,                     // 填满再中心裁剪 ≈ CLIP 的 Resize+CenterCrop
            Position = AnchorPositionMode.Center,
        }));
        var t = new float[3 * Size * Size];
        int plane = Size * Size;
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < Size; y++)
            {
                Span<Rgb24> row = acc.GetRowSpan(y);
                for (int x = 0; x < Size; x++)
                {
                    Rgb24 p = row[x];
                    int idx = y * Size + x;
                    t[idx] = (p.R / 255f - Mean[0]) / Std[0];               // R 通道
                    t[plane + idx] = (p.G / 255f - Mean[1]) / Std[1];       // G
                    t[2 * plane + idx] = (p.B / 255f - Mean[2]) / Std[2];   // B
                }
            }
        });
        return t;
    }
}

/// M3 按图搜图·本地 ONNX CLIP 图像编码器(CPU 推理·无需 GPU·**零出网**)。MiMo 无 embeddings 端点故走本地。
/// 模型 = CLIP ViT-B/32 图像编码器导出的 ONNX(512 维·部署提供)。输入/输出张量名可配(留空取模型首个)。
public sealed class OnnxClipEmbedder : IImageEmbedder, IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly ILogger<OnnxClipEmbedder> _log;

    public bool Enabled => true;
    public int Dim { get; }

    public OnnxClipEmbedder(string modelPath, string? inputName, string? outputName, int dim, ILogger<OnnxClipEmbedder> log)
    {
        _session = new InferenceSession(modelPath);
        _inputName = string.IsNullOrEmpty(inputName) ? _session.InputMetadata.Keys.First() : inputName!;
        _outputName = string.IsNullOrEmpty(outputName) ? _session.OutputMetadata.Keys.First() : outputName!;
        Dim = dim;
        _log = log;
        _log.LogInformation("ONNX CLIP 嵌入器就绪 model={Path} in={In} out={Out}", modelPath, _inputName, _outputName);
    }

    public Task<float[]?> EmbedAsync(byte[] imageBytes, CancellationToken ct)
    {
        try
        {
            float[] chw = ClipPreprocess.ToTensor(imageBytes);
            var input = new DenseTensor<float>(chw, new[] { 1, 3, ClipPreprocess.Size, ClipPreprocess.Size });
            var feeds = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, input) };
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(feeds, new[] { _outputName });
            float[] vec = results.First().AsEnumerable<float>().ToArray();
            if (vec.Length == 0) return Task.FromResult<float[]?>(null);
            VecMath.Normalize(vec);   // 单位化(检索用余弦,规范化无害)
            return Task.FromResult<float[]?>(vec);
        }
        catch (Exception ex) { _log.LogWarning(ex, "ONNX CLIP 推理失败 image={N}B", imageBytes.Length); return Task.FromResult<float[]?>(null); }
    }

    public void Dispose() => _session.Dispose();
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Horus.Server.Analysis.Vision;

/// OpenAI 兼容 /chat/completions 视觉 adapter。**DeepSeek-V4 / 小米 MiMo-V2.5(vLLM)/ Qwen-VL / GLM-4V 皆 OpenAI 兼容**
/// → 换供应商 = 换 baseUrl + model + key 三个 config,代码零改。图以 data:image/webp;base64 内联;要求模型只回 JSON。
/// 任何失败返回 null(fail-open:分析失败不阻断采集,交人工/后续复看)。
public sealed class OpenAiCompatibleVisionAnalyzer : IVisionAnalyzer
{
    private const string SystemPrompt =
        "你是考试监考图像审查助手。判断这张考试机屏幕截图是否显示作弊迹象:AI 对话界面(ChatGPT/DeepSeek/豆包/Kimi/文心 等)、" +
        "搜索引擎结果页、IDE 里的 AI 代码补全(灰色幽灵文本或整段凭空出现的代码)、远程协助/远控工具。只输出 JSON,不要任何解释。";

    private const string UserPrompt =
        "分析这张截图,只返回如下 JSON(不要代码块围栏):" +
        "{\"suspicious\":true/false,\"category\":\"web_ai|search|ide_plugin|remote_tool|other|none\"," +
        "\"confidence\":0-100,\"hits\":[\"标签\"],\"evidence\":\"一句话中文证据\",\"text\":\"截图里的关键可见文字(可空)\"}";

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _model;
    private readonly string _apiKey;

    public OpenAiCompatibleVisionAnalyzer(HttpClient http, string baseUrl, string model, string apiKey)
    {
        _http = http;
        _url = baseUrl.TrimEnd('/') + "/chat/completions";
        _model = model;
        _apiKey = apiKey;
    }

    public string Engine => "openai:" + _model;

    public async Task<VisionVerdict?> AnalyzeAsync(byte[] imageBytes, VisionContext ctx, CancellationToken ct)
    {
        try
        {
            string dataUri = "data:image/webp;base64," + Convert.ToBase64String(imageBytes);
            var body = new
            {
                model = _model,
                temperature = 0,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = new object[]
                    {
                        new { type = "text", text = UserPrompt },
                        new { type = "image_url", image_url = new { url = dataUri } },
                    } },
                },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, _url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            string json = await resp.Content.ReadAsStringAsync(ct);

            using JsonDocument doc = JsonDocument.Parse(json);
            string? content = doc.RootElement
                .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return string.IsNullOrWhiteSpace(content) ? null : Parse(content!);
        }
        catch { return null; }   // fail-open
    }

    /// 解析模型返回的 JSON。容忍 ```json 围栏 / 前后噪声:截取首个 '{' 到末个 '}'。
    public static VisionVerdict? Parse(string content)
    {
        int a = content.IndexOf('{'), b = content.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try
        {
            using JsonDocument d = JsonDocument.Parse(content[a..(b + 1)]);
            JsonElement r = d.RootElement;
            return new VisionVerdict
            {
                Suspicious = r.TryGetProperty("suspicious", out JsonElement s) && s.ValueKind == JsonValueKind.True,
                Category = Str(r, "category") ?? "none",
                Confidence = r.TryGetProperty("confidence", out JsonElement c) && c.TryGetInt32(out int ci) ? Math.Clamp(ci, 0, 100) : 0,
                Hits = StrArr(r, "hits"),
                Evidence = Str(r, "evidence") ?? "",
                Text = Str(r, "text"),
            };
        }
        catch { return null; }
    }

    private static string? Str(JsonElement o, string k)
        => o.TryGetProperty(k, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string[] StrArr(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out JsonElement e) || e.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (JsonElement it in e.EnumerateArray())
            if (it.ValueKind == JsonValueKind.String) list.Add(it.GetString()!);
        return list.ToArray();
    }
}

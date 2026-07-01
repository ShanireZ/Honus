using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horus.Server.Config;

/// 服务器配置(从 server.config.json 加载,camelCase)。record 便于用 with 施加环境变量覆盖。
public sealed record ServerConfig
{
    /// Kestrel 绑定地址(可多个,逗号分隔),如 "http://0.0.0.0:5199"。
    public string Urls { get; init; } = "http://0.0.0.0:5199";

    /// 数据根目录:SQLite 文件与截图原图都在此下。
    public string DataDir { get; init; } = "./data";

    /// SQLite 文件路径;":memory:" 走内存(测试用)。相对路径相对 DataDir。
    public string DbPath { get; init; } = "horus.db";

    /// 预共享 HMAC 密钥(base64)。与 Agent 同一把。留空则**关闭验签**(仅本地联调,生产必须配)。
    public string? PskBase64 { get; init; }

    /// 管理/看板令牌。所有 /api/* 请求需带 X-Horus-Admin 头(图片字节端点可用 ?t= 查询)。
    /// 留空则关闭管理鉴权(仅本地联调)。防止学员机调 /api/exams/{id}/config 关掉全场检测。
    public string? AdminToken { get; init; }

    /// 击键旁路密钥(base64)。判题后端持它对 /ingest/keystroke 提交体签名(X-Horus-KSig)。
    /// 留空则**关闭击键鉴权**(仅本地联调)。防同网学员机伪造/栽赃他人击键样本。与采集 PSK / 管理令牌相互独立。
    public string? KeystrokeSecretBase64 { get; init; }

    /// 允许在非 loopback 绑定下缺 PSK / 管理令牌启动(裸奔)。默认 false = fail-closed。仅联调开。
    public bool AllowInsecure { get; init; }

    // ---- 视觉分析(L2:视觉 LLM 取代 OCR + L3 Logo,合并单一视觉级)----
    /// 视觉分析器:留空/"off" = 关(默认) | "mock"(确定性·测试联调) | "openai"(OpenAI 兼容端点)。
    public string? VisionProvider { get; init; }
    /// OpenAI 兼容视觉端点基址(DeepSeek-V4 / MiMo-V2.5 / Qwen-VL / GLM-4V 通用)。provider="openai" 时用。
    public string? VisionBaseUrl { get; init; }
    /// 视觉模型名(如 deepseek-v4-pro / MiMo-V2.5)。
    public string? VisionModel { get; init; }
    /// 视觉端点 API key **明文**(仅联调;生产请用 visionApiKeyEnc 加密存储)。env HORUS_VISION_KEY 覆盖。
    public string? VisionApiKey { get; init; }
    /// 视觉端点 API key **DPAPI 密文**(base64·配置文件不存明文)。在部署机上跑 `protect-secret` 生成。见 SecretProtect。
    public string? VisionApiKeyEnc { get; init; }
    /// 视觉判定入可疑队列的置信度阈值(默认 60)。
    public int VisionConfidenceThreshold { get; init; } = 60;
    /// 是否也分析随机基线图(默认 false = 只分析触发型,§5 最小化上传/成本)。
    public bool VisionAnalyzeBaseline { get; init; }

    /// 事件风险分 ≥ 此值 → 入可疑队列。默认 50(见 architecture §16)。
    public int RiskThreshold { get; init; } = 50;

    /// 服务器侧 pHash 近重复判定:同座位相同 phash 视为重复,不另存原图。M1 用精确相等。
    public bool DedupImagesByPhash { get; init; } = true;

    /// 心跳在线判定窗口(秒):最近一次心跳在此窗口内则座位在线。
    public int OnlineWindowSeconds { get; init; } = 90;

    /// "最近风险"统计窗口(秒):座位热力取此窗口内事件的最大 risk。
    public int RecentRiskWindowSeconds { get; init; } = 300;

    [JsonIgnore]
    public byte[]? Psk => string.IsNullOrWhiteSpace(PskBase64) ? null : Convert.FromBase64String(PskBase64);

    [JsonIgnore]
    public byte[]? Ksk => string.IsNullOrWhiteSpace(KeystrokeSecretBase64) ? null : Convert.FromBase64String(KeystrokeSecretBase64);

    [JsonIgnore]
    public bool AuthEnabled => Psk is not null;

    [JsonIgnore]
    public bool KeystrokeAuthEnabled => Ksk is not null;

    [JsonIgnore]
    public bool AdminAuthEnabled => !string.IsNullOrEmpty(AdminToken);

    [JsonIgnore]
    public bool VisionEnabled => !string.IsNullOrWhiteSpace(VisionProvider)
                                 && !string.Equals(VisionProvider, "off", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ServerConfig Load(string path)
    {
        if (!File.Exists(path)) return new ServerConfig();
        return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), Opt) ?? new ServerConfig();
    }
}

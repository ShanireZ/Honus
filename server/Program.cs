using Horus.Contracts;
using Horus.Server.Api;
using Horus.Server.Config;
using Horus.Server.Data;
using Horus.Server.Ingest;

// ---- 配置加载(JSON) + 环境变量覆盖(便于测试/部署) ----
string cfgPath = Environment.GetEnvironmentVariable("HORUS_CONFIG")
                 ?? (args.Length > 0 ? args[0] : "server.config.json");
ServerConfig cfg = ServerConfig.Load(cfgPath);
cfg = cfg with
{
    DataDir = Environment.GetEnvironmentVariable("HORUS_DATADIR") ?? cfg.DataDir,
    DbPath = Environment.GetEnvironmentVariable("HORUS_DBPATH") ?? cfg.DbPath,
    PskBase64 = Environment.GetEnvironmentVariable("HORUS_PSK_B64") ?? cfg.PskBase64,
    AdminToken = Environment.GetEnvironmentVariable("HORUS_ADMIN_TOKEN") ?? cfg.AdminToken,
    Urls = Environment.GetEnvironmentVariable("HORUS_URLS") ?? cfg.Urls,
};

// ---- 解析数据目录与 DB 数据源 ----
string dataDir = Path.GetFullPath(cfg.DataDir);
Directory.CreateDirectory(dataDir);
string dataSource = cfg.DbPath == ":memory:"
    ? ":memory:"
    : Path.IsPathRooted(cfg.DbPath) ? cfg.DbPath : Path.Combine(dataDir, cfg.DbPath);

// Fail-closed:非 loopback 绑定却缺 PSK / 管理令牌 = 采集或管理面裸奔,拒绝启动(allowInsecure 仅联调可绕)。
string[] urls = cfg.Urls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
bool lanExposed = urls.Any(u =>
{
    try { string h = new Uri(u).Host; return h is not ("localhost" or "127.0.0.1" or "::1" or "[::1]"); }
    catch { return true; }
});
if (lanExposed && !cfg.AllowInsecure && (!cfg.AuthEnabled || !cfg.AdminAuthEnabled))
    throw new InvalidOperationException(
        "拒绝启动:绑定了非本机地址却未配置 PSK 或 AdminToken(采集/管理面将裸奔)。请配置两者,或仅联调时设 allowInsecure=true。");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(urls);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 2 * 1024 * 1024);   // 图片体上限 2MB(1080p webp~150KB),防放大 DoS

builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(new Db(dataSource));
builder.Services.AddSingleton(new Storage(dataDir));
builder.Services.AddSingleton<AgentHub>();          // 在线 Agent 注册表(config_update 下推)
builder.Services.AddSingleton<EventIngest>();
builder.Services.AddSingleton<ImageIngest>();
builder.Services.AddSingleton<KeystrokeIngest>();

WebApplication app = builder.Build();

app.UseWebSockets();

// ---- 管理/看板鉴权:所有 /api/* 需带 X-Horus-Admin 头(图片字节端点可用 ?t= 查询,因 <img> 无法设头) ----
// 关闭学员机"用 config 下发关掉全场检测 / 拉全班证据图 / 抹自己可疑裁决"等路径。未配令牌则放行(仅联调)。
if (cfg.AdminAuthEnabled)
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            string got = ctx.Request.Headers["X-Horus-Admin"].ToString();
            if (string.IsNullOrEmpty(got)) got = ctx.Request.Query["t"].ToString();
            if (!Crypto.FixedTimeEquals(got, cfg.AdminToken!))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                return;
            }
        }
        await next();
    });

// ---- 采集端通道(Agent ↔ Server) ----
app.MapGet("/ingest/events", (HttpContext ctx, EventIngest h) => h.HandleAsync(ctx));      // WebSocket
app.MapPost("/ingest/images", (HttpContext ctx, ImageIngest h) => h.HandleAsync(ctx));      // HTTP 图片
app.MapPost("/ingest/keystroke", (HttpContext ctx, KeystrokeIngest h) => h.HandleAsync(ctx)); // HTTP 击键旁路

// ---- 看板 / 管理 API ----
app.MapApi();

// ---- 静态看板(wwwroot 单页) ----
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("Horus 监考服务器启动 db={Db} dataDir={Dir} 采集鉴权={Auth} 管理鉴权={Admin} 阈值={Th}",
    dataSource, dataDir, cfg.AuthEnabled ? "开" : "关(仅联调)", cfg.AdminAuthEnabled ? "开" : "关(仅联调)", cfg.RiskThreshold);

app.Run();

// WebApplicationFactory<Program> 测试入口需要可见的 Program 类
public partial class Program { }

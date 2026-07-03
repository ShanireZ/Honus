using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Horus.Server.Ingest;
using Xunit;

namespace Horus.Server.Tests;

/// M4 部署项:考前预检 /api/preflight + /api/exams 白名单标记(§10.1 缺口告警)。
public class PreflightTests
{
    private static async Task<JsonElement> AdminGet(HttpClient http, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        HttpResponseMessage resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static async Task CreateExam(HttpClient http, string examId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/exams")
        { Content = JsonContent.Create(new { examId, name = "T", seats = new[] { new { seatId = "A01" } } }) };
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        (await http.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private static async Task PushWhitelist(HttpClient http, string examId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/exams/{examId}/config")
        { Content = JsonContent.Create(new { whitelistHosts = new[] { "judge.exam.cn" } }) };
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        (await http.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private static string? WhitelistCheckLevel(JsonElement preflight)
    {
        foreach (JsonElement c in preflight.GetProperty("checks").EnumerateArray())
            if (c.GetProperty("id").GetString() == "whitelist") return c.GetProperty("level").GetString();
        return null;
    }

    [Fact]
    public async Task 预检_基本可用_含checks与activeExams()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        JsonElement pf = await AdminGet(http, "/api/preflight");
        Assert.True(pf.GetProperty("ok").GetBoolean());                 // token 模式无 fail
        Assert.True(pf.GetProperty("checks").GetArrayLength() > 0);
        Assert.Equal(0, pf.GetProperty("activeExams").GetArrayLength()); // 尚无考试
    }

    [Fact]
    public async Task active考试无白名单_预检告警_下发后转ok()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        await CreateExam(http, "E1");

        JsonElement pf1 = await AdminGet(http, "/api/preflight");
        Assert.Equal("warn", WhitelistCheckLevel(pf1));                 // 无白名单 → warn
        JsonElement ae = pf1.GetProperty("activeExams")[0];
        Assert.Equal("E1", ae.GetProperty("examId").GetString());
        Assert.False(ae.GetProperty("hasWhitelist").GetBoolean());

        await PushWhitelist(http, "E1");
        JsonElement pf2 = await AdminGet(http, "/api/preflight");
        Assert.Equal("ok", WhitelistCheckLevel(pf2));                   // 下发后 → ok
    }

    [Fact]
    public async Task exams列表_带hasWhitelist标记()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        await CreateExam(http, "E9");

        JsonElement before = await AdminGet(http, "/api/exams");
        Assert.False(before[0].GetProperty("hasWhitelist").GetBoolean());

        await PushWhitelist(http, "E9");
        JsonElement after = await AdminGet(http, "/api/exams");
        Assert.True(after[0].GetProperty("hasWhitelist").GetBoolean());
    }
}

/// M2 基线抽样策略:确定性 1/N 抽样(跨重启一致)。
public class BaselineSamplingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 抽样率不大于1_全命中(int rate)
    {
        for (int i = 0; i < 50; i++)
            Assert.True(ImageIngest.BaselineSampleHit("img_" + i.ToString("x"), rate));
    }

    [Fact]
    public void 确定性_同id同结果()
    {
        for (int i = 0; i < 100; i++)
        {
            string id = "img_" + Guid.NewGuid().ToString("N");
            Assert.Equal(ImageIngest.BaselineSampleHit(id, 7), ImageIngest.BaselineSampleHit(id, 7));
        }
    }

    [Fact]
    public void 分布_约1_N命中()
    {
        const int rate = 5, total = 5000;
        int hit = 0;
        for (int i = 0; i < total; i++)
            if (ImageIngest.BaselineSampleHit("img_" + i.ToString("x8"), rate)) hit++;
        double frac = (double)hit / total;
        // 期望 ~0.2;放宽到 [0.15, 0.25] 容忍 FNV 在有限样本上的偏差。
        Assert.InRange(frac, 0.15, 0.25);
    }
}

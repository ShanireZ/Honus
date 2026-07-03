using System.Net;
using Horus.Server.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Horus.Server.Tests;

/// token 交换瞬时失败重试(OidcHttp)。真机曾遇服务端 POST /oauth/token 偶发 TLS「unexpected EOF」——
/// 对 TLS/网络异常与 502/503/504 重试;稳定的 4xx/成功不重试。
public class OidcHttpRetryTests
{
    // 按脚本产生响应/异常的桩 handler:每次调用记数,依 _steps 决定抛异常还是返回某状态码。
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<object> _steps;   // Exception 或 HttpStatusCode
        public int Calls { get; private set; }

        public StubHandler(params object[] steps) => _steps = new Queue<object>(steps);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            object step = _steps.Count > 0 ? _steps.Dequeue() : HttpStatusCode.OK;
            if (step is Exception ex) return Task.FromException<HttpResponseMessage>(ex);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)step)
            {
                Content = new StringContent("{\"id_token\":\"x\"}"),
            });
        }
    }

    private static readonly Dictionary<string, string> Fields = new() { ["grant_type"] = "authorization_code" };

    private static async Task<(bool ok, int status, string body, int calls)> Run(params object[] steps)
    {
        var handler = new StubHandler(steps);
        using var http = new HttpClient(handler);
        (bool ok, int status, string body) = await OidcHttp.PostFormWithRetryAsync(
            http, "https://token.test/oauth/token", Fields, NullLogger.Instance, CancellationToken.None);
        return (ok, status, body, handler.Calls);
    }

    [Fact]
    public async Task 前两次TLS异常_第三次成功_共调用3次()
    {
        var r = await Run(
            new HttpRequestException("SSL handshake EOF"),
            new IOException("unexpected EOF"),
            HttpStatusCode.OK);
        Assert.True(r.ok);
        Assert.Equal(200, r.status);
        Assert.Equal(3, r.calls);
    }

    [Fact]
    public async Task 网关502瞬时_重试后成功()
    {
        var r = await Run(HttpStatusCode.BadGateway, HttpStatusCode.OK);
        Assert.True(r.ok);
        Assert.Equal(2, r.calls);
    }

    [Fact]
    public async Task 稳定4xx_不重试_返回非ok()
    {
        var r = await Run(HttpStatusCode.BadRequest);   // 400 = 真错误,非瞬时
        Assert.False(r.ok);
        Assert.Equal(400, r.status);
        Assert.Equal(1, r.calls);   // 只调用一次,不浪费重试
    }

    [Fact]
    public async Task 一次成功_不重试()
    {
        var r = await Run(HttpStatusCode.OK);
        Assert.True(r.ok);
        Assert.Equal(1, r.calls);
    }

    [Fact]
    public async Task 全程瞬时失败_重试耗尽后抛出()
    {
        var handler = new StubHandler(
            new HttpRequestException("EOF"),
            new HttpRequestException("EOF"),
            new HttpRequestException("EOF"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => OidcHttp.PostFormWithRetryAsync(
            http, "https://token.test/oauth/token", Fields, NullLogger.Instance, CancellationToken.None));
        Assert.Equal(3, handler.Calls);   // 用满 maxAttempts=3
    }
}

using System.Net;
using System.Net.Http.Json;

namespace CodeSentinel.Tests;

/// <summary>
/// 项目基座测试：健康检查与统一错误响应形状。
/// </summary>
public class FoundationTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    /// <summary>
    /// 健康检查接口应返回 200。
    /// </summary>
    [Fact]
    public async Task Health_returns_ok()
    {
        var res = await factory.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    /// <summary>
    /// 未知路由应返回 404 且响应体为统一错误形状。
    /// </summary>
    [Fact]
    public async Task Unknown_route_returns_error_shape()
    {
        var res = await factory.CreateClient().GetAsync("/api/nope");
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("NOT_FOUND", body!.Code);
        Assert.False(string.IsNullOrWhiteSpace(body.TraceId));
    }
}

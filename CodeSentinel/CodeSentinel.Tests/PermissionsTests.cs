using System.Net;
using System.Net.Http.Json;
using System.Text;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 权限矩阵验收测试：跨仓库越权一律 404，匿名一律 401，待改密用户允许访问改密白名单接口。
/// </summary>
public class PermissionsTests
{
    private const string TestPassword = "Passw0rd!";

    /// <summary>
    /// 只属于仓库 A 的成员访问仓库 B 的任意作用域端点都应返回 404 NOT_FOUND，而非 403。
    /// </summary>
    /// <param name="method">HTTP 方法</param>
    /// <param name="pathTemplate">带 {repoB} / {reviewB} 占位符的路径</param>
    /// <param name="jsonBody">请求体，GET 时为 null</param>
    [Theory]
    [MemberData(nameof(RepoBScopedEndpoints))]
    public async Task Member_of_another_repo_gets_404(
        HttpMethod method,
        string pathTemplate,
        string? jsonBody)
    {
        using var factory = new TestAppFactory();
        var member = factory.SeedUser(UniqueName("member-a"), TestPassword);
        var submitterB = factory.SeedUser(UniqueName("submitter-b"), TestPassword);
        var repoB = factory.SeedRepo("https://example.com/repo-b.git", UniqueName("repo-b"));
        SeedMember(factory, repoB.Id, submitterB.Id);
        var reviewB = SeedReview(factory, repoB.Id, submitterB.Id);

        var client = await factory.LoginAsync(member.Username, TestPassword);
        var url = pathTemplate
            .Replace("{repoB}", repoB.Id.ToString(), StringComparison.Ordinal)
            .Replace("{reviewB}", reviewB.ToString(), StringComparison.Ordinal);

        var response = await SendAsync(client, method, url, jsonBody);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("NOT_FOUND", body.Code);
    }

    /// <summary>
    /// 匿名访问受保护接口返回 401，但健康检查、登录、CSRF 与初始化管理员接口不受保护。
    /// </summary>
    [Fact]
    public async Task Anonymous_gets_401_except_health_login_csrf_and_setup()
    {
        using var factory = new TestAppFactory();
        var client = factory.CreateClient();

        // 免登录接口：健康检查、CSRF 取令牌、登录（返回 401 无效凭据而非被拦在认证层）、初始化管理员（已初始化返回 409）
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/api/health")).StatusCode);

        var csrf = await client.GetAsync("/api/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);

        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { username = "nobody", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(
            "INVALID_CREDENTIALS",
            (await login.Content.ReadFromJsonAsync<ErrorBody>())!.Code);

        var setup = await client.PostAsJsonAsync(
            "/api/setup/admin", new { token = "x", username = "a", password = "Passw0rd!", displayName = "a" });
        Assert.Equal(HttpStatusCode.Conflict, setup.StatusCode);

        // 受保护接口的代表样本：跨功能一律 401 UNAUTHENTICATED
        foreach (var route in ProtectedRoutes())
        {
            var response = await client.GetAsync(route);
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.NotNull(body);
            Assert.Equal("UNAUTHENTICATED", body.Code);
        }
    }

    /// <summary>
    /// 待改密用户仍可访问改密白名单：读取自身信息与 CSRF 令牌，但访问其它接口返回 403。
    /// </summary>
    [Fact]
    public async Task Must_change_password_user_can_access_allowlist()
    {
        using var factory = new TestAppFactory();
        var admin = await factory.LoginAsAdminAsync();
        var create = await admin.PostJsonWithCsrfAsync(
            "/api/users",
            new
            {
                username = "must-change-allowlist",
                password = "Initial123!",
                displayName = "待改密",
                isAdmin = false
            });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var user = await factory.LoginAsync("must-change-allowlist", "Initial123!");

        var me = await user.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var csrf = await user.GetAsync("/api/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
        Assert.NotNull(await csrf.Content.ReadFromJsonAsync<CsrfBody>());

        var blocked = await user.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal(
            "PASSWORD_CHANGE_REQUIRED",
            (await blocked.Content.ReadFromJsonAsync<ErrorBody>())!.Code);
    }

    /// <summary>
    /// 评审详情权限里的 isMerger 只取仓库成员上的合并人标记，管理员不会自动成为合并人。
    /// </summary>
    [Fact]
    public async Task Detail_permissions_report_is_merger_only_for_repo_mergers()
    {
        using var factory = new TestAppFactory();
        var submitter = factory.SeedUser(UniqueName("submitter"), TestPassword);
        var merger = factory.SeedUser(UniqueName("merger"), TestPassword);
        var member = factory.SeedUser(UniqueName("member"), TestPassword);
        var repo = factory.SeedRepo("https://example.com/repo-merger.git", UniqueName("repo-merger"));
        SeedMember(factory, repo.Id, submitter.Id);
        SeedMember(factory, repo.Id, merger.Id, isMerger: true);
        SeedMember(factory, repo.Id, member.Id);
        var reviewId = SeedReview(factory, repo.Id, submitter.Id);

        var mergerClient = await factory.LoginAsync(merger.Username, TestPassword);
        var mergerDetail = await mergerClient.GetFromJsonAsync<ReviewDetailBody>($"/api/reviews/{reviewId}");
        Assert.NotNull(mergerDetail);
        Assert.True(mergerDetail.Permissions.IsMerger);

        var memberClient = await factory.LoginAsync(member.Username, TestPassword);
        var memberDetail = await memberClient.GetFromJsonAsync<ReviewDetailBody>($"/api/reviews/{reviewId}");
        Assert.NotNull(memberDetail);
        Assert.False(memberDetail.Permissions.IsMerger);

        // 管理员可访问任意评审单，但未被标记为合并人时 isMerger 仍为 false
        var admin = await factory.LoginAsAdminAsync();
        var adminDetail = await admin.GetFromJsonAsync<ReviewDetailBody>($"/api/reviews/{reviewId}");
        Assert.NotNull(adminDetail);
        Assert.False(adminDetail.Permissions.IsMerger);
    }

    /// <summary>
    /// 仓库 B 作用域端点表：方法、带占位符的路径与可选请求体。
    /// </summary>
    /// <returns>理论数据行</returns>
    public static IEnumerable<object?[]> RepoBScopedEndpoints()
    {
        yield return [HttpMethod.Get, "/api/repos/{repoB}", null];
        yield return [HttpMethod.Get, "/api/repos/{repoB}/members", null];
        yield return [HttpMethod.Get, "/api/repos/{repoB}/branches", null];
        yield return [HttpMethod.Get, "/api/reviews/{reviewB}", null];
        yield return [HttpMethod.Get, "/api/reviews/{reviewB}/files", null];
        yield return [HttpMethod.Get, "/api/reviews/{reviewB}/diff?path=a.txt", null];
        yield return [HttpMethod.Get, "/api/reviews/{reviewB}/comments", null];
        yield return
        [
            HttpMethod.Post,
            "/api/reviews/{reviewB}/comments",
            "{\"path\":\"a.txt\",\"side\":\"NEW\",\"line\":1,\"body\":\"x\"}"
        ];
        yield return [HttpMethod.Post, "/api/reviews/{reviewB}/decisions", "{\"state\":\"APPROVED\"}"];
        yield return [HttpMethod.Post, "/api/reviews/{reviewB}/close", "{\"reason\":\"x\"}"];
        yield return
        [
            HttpMethod.Put,
            "/api/reviews/{reviewB}/files/viewed",
            "{\"path\":\"a.txt\",\"viewed\":true}"
        ];
        yield return [HttpMethod.Post, "/api/reviews/{reviewB}/mark-merged", "{}"];
    }

    /// <summary>
    /// 受保护路由代表样本，用于匿名 401 断言。
    /// </summary>
    /// <returns>路由列表</returns>
    private static IEnumerable<string> ProtectedRoutes()
    {
        yield return "/api/auth/me";
        yield return "/api/users";
        yield return "/api/repos";
        yield return "/api/reviews";
        yield return "/api/notifications";
        yield return "/api/repos/00000000-0000-0000-0000-000000000000/members";
        yield return "/api/reviews/00000000-0000-0000-0000-000000000000/comments";
    }

    /// <summary>
    /// 携带 CSRF 令牌发送请求，GET 时无需请求体与令牌。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="method">HTTP 方法</param>
    /// <param name="url">请求地址</param>
    /// <param name="jsonBody">JSON 请求体，GET 时为 null</param>
    /// <returns>HTTP 响应</returns>
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        string? jsonBody)
    {
        using var request = new HttpRequestMessage(method, url);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            request.Headers.Add(
                TestApiClient.CsrfHeaderName,
                await client.GetCsrfTokenAsync());
        }

        return await client.SendAsync(request);
    }

    /// <summary>
    /// 直接写入仓库成员关系。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="userId">用户主键</param>
    /// <param name="isMerger">是否为合并人</param>
    private static void SeedMember(
        TestAppFactory factory,
        Guid repoId,
        Guid userId,
        bool isMerger = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.RepoMembers.Add(new RepoMember
        {
            Id = Guid.NewGuid(),
            RepoId = repoId,
            UserId = userId,
            IsMerger = isMerger
        });
        db.SaveChanges();
    }

    /// <summary>
    /// 直接写入一条评审单。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="submitterId">提交人主键</param>
    /// <returns>评审单主键</returns>
    private static Guid SeedReview(TestAppFactory factory, Guid repoId, Guid submitterId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var review = new Review
        {
            Id = Guid.NewGuid(),
            RepoId = repoId,
            BaseBranch = "main",
            HeadBranch = "feature",
            SubmitterId = submitterId,
            Title = "越权验收",
            Status = ReviewStatus.Open,
            BaseSha = "base-sha",
            HeadSha = "head-sha"
        };
        db.Reviews.Add(review);
        db.SaveChanges();
        return review.Id;
    }

    /// <summary>
    /// 生成不重复的用户名或仓库名。
    /// </summary>
    /// <param name="prefix">名称前缀</param>
    /// <returns>唯一名称</returns>
    private static string UniqueName(string prefix)
    {
        return $"{prefix}-{Guid.NewGuid():N}";
    }
}

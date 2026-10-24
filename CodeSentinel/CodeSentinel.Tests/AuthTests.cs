using System.Net;
using System.Net.Http.Json;
using CodeSentinel.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 登录、会话、CSRF 与审计相关测试。
/// </summary>
public class AuthTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    /// <summary>
    /// 种子管理员登录后应能读取自己的身份信息。
    /// </summary>
    [Fact]
    public async Task Login_with_seeded_admin_returns_me()
    {
        var client = await factory.LoginAsAdminAsync();

        var me = await client.GetFromJsonAsync<MeBody>("/api/auth/me");

        Assert.NotNull(me);
        Assert.Equal(TestAppFactory.AdminUsername, me.Username);
        Assert.Equal(TestAppFactory.AdminDisplayName, me.DisplayName);
        Assert.True(me.IsAdmin);
        Assert.False(me.MustChangePassword);
    }

    /// <summary>
    /// 密码错误应返回 401 与统一错误形状。
    /// </summary>
    [Fact]
    public async Task Login_with_wrong_password_returns_401_error_shape()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username = TestAppFactory.AdminUsername, password = "Wrong123!" });
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("INVALID_CREDENTIALS", body.Code);
        Assert.False(string.IsNullOrWhiteSpace(body.TraceId));
    }

    /// <summary>
    /// 已登录的非 GET 请求缺少 CSRF 令牌时应返回 400。
    /// </summary>
    [Fact]
    public async Task Mutating_request_without_csrf_returns_400()
    {
        var client = await factory.LoginAsAdminAsync();

        var response = await client.PostAsJsonAsync(
            "/api/users",
            new { username = "no-csrf-user", password = "Passw0rd!", displayName = "缺少令牌" });
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("CSRF_INVALID", body.Code);
    }

    /// <summary>
    /// 停用用户即使密码正确也不能登录。
    /// </summary>
    [Fact]
    public async Task Deactivated_user_cannot_login()
    {
        factory.SeedUser("inactive-user", "Passw0rd!", isActive: false);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username = "inactive-user", password = "Passw0rd!" });
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("INVALID_CREDENTIALS", body.Code);
    }

    /// <summary>
    /// 新建用户首次登录后必须先改密，改密前访问其它接口返回 403 PASSWORD_CHANGE_REQUIRED。
    /// </summary>
    [Fact]
    public async Task New_user_must_change_password_before_other_calls()
    {
        var admin = await factory.LoginAsAdminAsync();
        var create = await admin.PostJsonWithCsrfAsync(
            "/api/users",
            new
            {
                username = "must-change-user",
                password = "Initial123!",
                displayName = "待改密用户",
                isAdmin = false
            });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var user = await factory.LoginAsync("must-change-user", "Initial123!");

        var me = await user.GetFromJsonAsync<MeBody>("/api/auth/me");
        Assert.NotNull(me);
        Assert.True(me.MustChangePassword);

        var blocked = await user.GetAsync("/api/users");
        var blockedBody = await blocked.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.NotNull(blockedBody);
        Assert.Equal("PASSWORD_CHANGE_REQUIRED", blockedBody.Code);

        var change = await user.PostJsonWithCsrfAsync(
            "/api/auth/change-password",
            new { currentPassword = "Initial123!", newPassword = "Changed123!" });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        var updatedMe = await user.GetFromJsonAsync<MeBody>("/api/auth/me");
        Assert.NotNull(updatedMe);
        Assert.False(updatedMe.MustChangePassword);

        var afterChange = await user.PostJsonWithCsrfAsync(
            "/api/users",
            new { username = "forbidden-user", password = "Passw0rd!", displayName = "无权用户" });
        Assert.Equal(HttpStatusCode.Forbidden, afterChange.StatusCode);
    }

    /// <summary>
    /// 非管理员不能创建用户。
    /// </summary>
    [Fact]
    public async Task Non_admin_cannot_create_user()
    {
        factory.SeedUser("plain-user", "Passw0rd!");
        var client = await factory.LoginAsync("plain-user", "Passw0rd!");

        var response = await client.PostJsonWithCsrfAsync(
            "/api/users",
            new { username = "created-by-plain-user", password = "Passw0rd!", displayName = "越权创建" });
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("FORBIDDEN", body.Code);
    }

    /// <summary>
    /// 登录成功应写入 LOGIN 审计日志并记录操作人。
    /// </summary>
    [Fact]
    public async Task Login_writes_audit_row()
    {
        _ = await factory.LoginAsAdminAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 同一测试类内多次登录都会写日志，这里只断言存在且操作人正确
        var logs = await db.AuditLogs
            .Where(m => m.Action == "LOGIN" && m.ActorId == factory.AdminUserId)
            .ToListAsync();

        Assert.NotEmpty(logs);
        Assert.All(logs, log =>
        {
            Assert.Equal("USER", log.TargetType);
            Assert.Equal(factory.AdminUserId, log.TargetId);
        });
    }

    /// <summary>
    /// 登录后被停用的用户，其下一次请求应立刻返回 401，无需重新登录即失效。
    /// </summary>
    [Fact]
    public async Task Deactivated_user_is_rejected_on_next_request()
    {
        var user = factory.SeedUser("session-deactivate", "Passw0rd!");
        var client = await factory.LoginAsync(user.Username, "Passw0rd!");

        var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var admin = await factory.LoginAsAdminAsync();
        var patch = await admin.PatchJsonWithCsrfAsync(
            $"/api/users/{user.Id}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var blocked = await client.GetAsync("/api/auth/me");
        var body = await blocked.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("UNAUTHENTICATED", body.Code);
    }

    /// <summary>
    /// 管理员被降级后，其已签发的 Cookie 应立即失去管理员权限。
    /// </summary>
    [Fact]
    public async Task Demoted_admin_loses_access_immediately()
    {
        var demotee = factory.SeedUser("demotee-admin", "Passw0rd!", isAdmin: true);
        var client = await factory.LoginAsync(demotee.Username, "Passw0rd!");

        var allowed = await client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        var admin = await factory.LoginAsAdminAsync();
        var patch = await admin.PatchJsonWithCsrfAsync(
            $"/api/users/{demotee.Id}", new { isAdmin = false });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var denied = await client.GetAsync("/api/users");
        var body = await denied.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("FORBIDDEN", body.Code);
    }
}

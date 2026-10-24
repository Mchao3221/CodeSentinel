using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 错误形状健壮性测试：无效请求体、损坏 payload 的降级、未处理异常的 500 兜底与分页防溢出。
/// </summary>
public class ErrorShapeTests
{
    private const string TestPassword = "Passw0rd!";

    /// <summary>
    /// 标记已读接口收到非法 JSON 时应返回 400 INVALID_BODY，而不是 500。
    /// </summary>
    [Fact]
    public async Task Invalid_json_in_read_returns_400_invalid_body()
    {
        using var factory = new TestAppFactory();
        var user = factory.SeedUser("invalid-json-reader", TestPassword);
        var client = await factory.LoginAsync(user.Username, TestPassword);

        var response = await PostRawAsync(client, "/api/notifications/read", "{ not valid json");

        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("INVALID_BODY", body.Code);
    }

    /// <summary>
    /// 单条损坏的 payload_json 不应让整个通知列表 500，应回退为空对象。
    /// </summary>
    [Fact]
    public async Task Corrupt_payload_falls_back_to_empty_object()
    {
        using var factory = new TestAppFactory();
        var user = factory.SeedUser("corrupt-payload-owner", TestPassword);
        var good = SeedNotification(factory, user.Id, new { title = "正常" });
        SeedCorruptNotification(factory, user.Id);

        var client = await factory.LoginAsync(user.Username, TestPassword);
        var body = await client.GetFromJsonAsync<NotificationListBody>("/api/notifications");

        Assert.NotNull(body);
        Assert.Equal(2, body.Total);
        var goodItem = body.Items.Single(m => m.Id == good);
        Assert.Equal("正常", goodItem.Payload.GetProperty("title").GetString());
        var corruptItem = body.Items.Single(m => m.Id != good);
        Assert.Equal(JsonValueKind.Object, corruptItem.Payload.ValueKind);
        Assert.Empty(corruptItem.Payload.EnumerateObject());
    }

    /// <summary>
    /// 未处理异常应返回 500 INTERNAL_ERROR 的统一错误形状，而不是泄露堆栈。
    /// </summary>
    [Fact]
    public async Task Unexpected_exception_returns_500_internal_error()
    {
        using var factory = new TestAppFactory();
        var repo = factory.SeedRepo("https://example.com/repo.git", "error-shape-repo");
        var review = SeedReview(factory, repo.Id, factory.AdminUserId);

        // 直接删除评审单关联的仓库，制造详情组装时的未处理异常
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Repos.SingleAsync(m => m.Id == repo.Id);
            db.Repos.Remove(row);
            await db.SaveChangesAsync();
        }

        var admin = await factory.LoginAsAdminAsync();
        var response = await admin.GetAsync($"/api/reviews/{review}");
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("INTERNAL_ERROR", body.Code);
        Assert.False(string.IsNullOrWhiteSpace(body.TraceId));
    }

    /// <summary>
    /// 巨大的页码不应导致 (page-1)*pageSize 整数溢出，各列表接口都应正常返回 200。
    /// </summary>
    [Fact]
    public async Task Huge_page_does_not_overflow()
    {
        using var factory = new TestAppFactory();
        var client = await factory.LoginAsAdminAsync();

        foreach (var url in new[]
        {
            "/api/repos?page=2147483647",
            "/api/reviews?page=2147483647",
            "/api/users?page=2147483647",
            "/api/notifications?page=2147483647"
        })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
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
            Title = "错误形状",
            Status = ReviewStatus.Open,
            BaseSha = "base-sha",
            HeadSha = "head-sha"
        };
        db.Reviews.Add(review);
        db.SaveChanges();
        return review.Id;
    }

    /// <summary>
    /// 直接写入一条正常通知。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="userId">接收人主键</param>
    /// <param name="payload">payload 对象</param>
    /// <returns>通知主键</returns>
    private static Guid SeedNotification(TestAppFactory factory, Guid userId, object payload)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = NotificationType.ReviewSubmitted,
            PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        db.Notifications.Add(notification);
        db.SaveChanges();
        return notification.Id;
    }

    /// <summary>
    /// 直接写入一条 payload_json 损坏的通知。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="userId">接收人主键</param>
    private static void SeedCorruptNotification(TestAppFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = NotificationType.ReviewSubmitted,
            PayloadJson = "{ 损坏的 payload"
        });
        db.SaveChanges();
    }

    /// <summary>
    /// 携带 CSRF 令牌发送原始文本请求。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="url">请求地址</param>
    /// <param name="body">原始请求体</param>
    /// <returns>HTTP 响应</returns>
    private static async Task<HttpResponseMessage> PostRawAsync(
        HttpClient client,
        string url,
        string body)
    {
        var token = await client.GetCsrfTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(TestApiClient.CsrfHeaderName, token);
        return await client.SendAsync(request);
    }
}

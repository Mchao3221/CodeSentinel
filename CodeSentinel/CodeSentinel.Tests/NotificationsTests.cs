using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using CodeSentinel.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 通知查询、已读与五分钟去重集成测试：在真实 SQLite 与真实 HTTP 上验证。
/// </summary>
public class NotificationsTests
{
    private const string TestPassword = "Passw0rd!";
    private const string BaseBranch = "main";
    private const string HeadBranch = "feature";

    /// <summary>
    /// 列表只返回当前用户的通知，按创建时间倒序，payload 以 JSON 对象返回。
    /// </summary>
    [Fact]
    public async Task Lists_only_my_notifications()
    {
        using var factory = new TestAppFactory();
        var owner = factory.SeedUser(UniqueName("owner"), TestPassword);
        var other = factory.SeedUser(UniqueName("other"), TestPassword);
        var reviewId = Guid.NewGuid();
        var olderId = SeedNotification(
            factory,
            owner.Id,
            NotificationType.ReviewSubmitted,
            reviewId,
            new { reviewId, title = "旧通知" },
            DateTime.UtcNow.AddMinutes(-2));
        var newerId = SeedNotification(
            factory,
            owner.Id,
            NotificationType.CommentReply,
            reviewId,
            new { reviewId, title = "新通知" },
            DateTime.UtcNow.AddMinutes(-1));
        SeedNotification(factory, other.Id, NotificationType.ReviewSubmitted, reviewId, new { reviewId });

        var client = await factory.LoginAsync(owner.Username, TestPassword);
        var body = await ReadNotificationsAsync(client, string.Empty);

        Assert.Equal(2, body.Total);
        Assert.Equal(1, body.Page);
        Assert.Equal(20, body.PageSize);
        Assert.Equal(new[] { newerId, olderId }, body.Items.Select(m => m.Id).ToArray());
        Assert.All(body.Items, m => Assert.Equal(JsonValueKind.Object, m.Payload.ValueKind));
        Assert.Equal("COMMENT_REPLY", body.Items[0].Type);
        Assert.Equal(reviewId, body.Items[0].ReviewId);
        Assert.Equal("新通知", body.Items[0].Payload.GetProperty("title").GetString());
        Assert.Null(body.Items[0].ReadAt);

        var secondPage = await ReadNotificationsAsync(client, "?page=2&pageSize=1");
        Assert.Equal(2, secondPage.Total);
        Assert.Equal(2, secondPage.Page);
        Assert.Equal(1, secondPage.PageSize);
        Assert.Equal(olderId, Assert.Single(secondPage.Items).Id);

        await MarkReadAsync(factory, newerId);
        var unread = await ReadNotificationsAsync(client, "?unreadOnly=true");
        Assert.Equal(1, unread.Total);
        Assert.Equal(olderId, Assert.Single(unread.Items).Id);
    }

    /// <summary>
    /// 未读数只统计当前用户的未读记录。
    /// </summary>
    [Fact]
    public async Task Unread_count_matches_unread_rows()
    {
        using var factory = new TestAppFactory();
        var owner = factory.SeedUser(UniqueName("owner"), TestPassword);
        var other = factory.SeedUser(UniqueName("other"), TestPassword);
        SeedNotification(factory, owner.Id, NotificationType.ReviewSubmitted, null, new { title = "一" });
        SeedNotification(factory, owner.Id, NotificationType.Approved, null, new { title = "二" });
        SeedNotification(
            factory,
            owner.Id,
            NotificationType.Closed,
            null,
            new { title = "三" },
            readAt: DateTime.UtcNow);
        SeedNotification(factory, other.Id, NotificationType.ReviewSubmitted, null, new { title = "别人的" });

        var client = await factory.LoginAsync(owner.Username, TestPassword);
        var body = await client.GetFromJsonAsync<UnreadCountBody>("/api/notifications/unread-count");

        Assert.NotNull(body);
        Assert.Equal(2, body.Count);
    }

    /// <summary>
    /// 按 id 标记与全部标记都只影响当前用户，重复调用保持幂等。
    /// </summary>
    [Fact]
    public async Task Mark_selected_and_all_as_read_are_idempotent()
    {
        using var factory = new TestAppFactory();
        var owner = factory.SeedUser(UniqueName("owner"), TestPassword);
        var other = factory.SeedUser(UniqueName("other"), TestPassword);
        var firstId = SeedNotification(factory, owner.Id, NotificationType.ReviewSubmitted, null, new { title = "一" });
        var secondId = SeedNotification(factory, owner.Id, NotificationType.Approved, null, new { title = "二" });
        var thirdId = SeedNotification(factory, owner.Id, NotificationType.Closed, null, new { title = "三" });
        var otherId = SeedNotification(factory, other.Id, NotificationType.ReviewSubmitted, null, new { title = "别人的" });

        var client = await factory.LoginAsync(owner.Username, TestPassword);
        var selected = new { ids = new[] { firstId, thirdId } };
        var first = await client.PostJsonWithCsrfAsync("/api/notifications/read", selected);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(2, (await first.Content.ReadFromJsonAsync<MarkReadBody>())!.Updated);

        var repeated = await client.PostJsonWithCsrfAsync("/api/notifications/read", selected);
        Assert.Equal(0, (await repeated.Content.ReadFromJsonAsync<MarkReadBody>())!.Updated);
        Assert.Equal(1, await ReadUnreadCountAsync(client));
        Assert.Equal(1, await CountUnreadAsync(factory, other.Id));
        Assert.Null(await ReadReadAtAsync(factory, otherId));

        var markAll = await client.PostJsonWithCsrfAsync("/api/notifications/read", new { all = true });
        Assert.Equal(1, (await markAll.Content.ReadFromJsonAsync<MarkReadBody>())!.Updated);
        var markAllAgain = await client.PostJsonWithCsrfAsync("/api/notifications/read", new { all = true });
        Assert.Equal(0, (await markAllAgain.Content.ReadFromJsonAsync<MarkReadBody>())!.Updated);
        Assert.Equal(0, await ReadUnreadCountAsync(client));
        Assert.NotNull(await ReadReadAtAsync(factory, secondId));
        Assert.Equal(1, await CountUnreadAsync(factory, other.Id));
    }

    /// <summary>
    /// 同一用户、类型、评审单在五分钟内重复写入时更新原记录，已读或过期记录则新增。
    /// </summary>
    [Fact]
    public async Task Same_event_within_five_minutes_updates_instead_of_inserting()
    {
        using var factory = new TestAppFactory();
        var user = factory.SeedUser(UniqueName("dedupe"), TestPassword);
        var reviewId = Guid.NewGuid();

        var first = await CreateNotificationAsync(
            factory, user.Id, NotificationType.ReviewSubmitted, reviewId, new { title = "v1" });
        var second = await CreateNotificationAsync(
            factory, user.Id, NotificationType.ReviewSubmitted, reviewId, new { title = "v2" });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Count, second.Count);
        Assert.Contains("v2", second.PayloadJson);
        Assert.True(second.CreatedAt > first.CreatedAt);

        // 不同类型与不同评审单属于不同事件
        var otherType = await CreateNotificationAsync(
            factory, user.Id, NotificationType.ChangesRequested, reviewId, new { title = "v3" });
        var otherReview = await CreateNotificationAsync(
            factory, user.Id, NotificationType.ReviewSubmitted, Guid.NewGuid(), new { title = "v4" });
        Assert.NotEqual(otherType.Id, first.Id);
        Assert.NotEqual(otherReview.Id, first.Id);
        Assert.Equal(first.Count + 2, otherReview.Count);

        // 已读记录不参与去重
        await MarkReadAsync(factory, second.Id);
        var afterRead = await CreateNotificationAsync(
            factory, user.Id, NotificationType.ReviewSubmitted, reviewId, new { title = "v5" });
        Assert.NotEqual(second.Id, afterRead.Id);

        // 超过五分钟的记录不参与去重
        await SetCreatedAtAsync(factory, afterRead.Id, DateTime.UtcNow.AddMinutes(-6));
        var expired = await CreateNotificationAsync(
            factory, user.Id, NotificationType.ReviewSubmitted, reviewId, new { title = "v6" });
        Assert.NotEqual(afterRead.Id, expired.Id);

        // review_id 为空的通知同样按用户加类型去重
        var nullOwner = factory.SeedUser(UniqueName("null-review"), TestPassword);
        var nullFirst = await CreateNotificationAsync(
            factory, nullOwner.Id, NotificationType.Approved, null, new { title = "n1" });
        var nullSecond = await CreateNotificationAsync(
            factory, nullOwner.Id, NotificationType.Approved, null, new { title = "n2" });
        Assert.Equal(nullFirst.Id, nullSecond.Id);
        Assert.Equal(1, nullSecond.Count);
    }

    /// <summary>
    /// 审核人撤回结论时通知提交人使用 DECISION_WITHDRAWN 并携带操作人信息。
    /// </summary>
    [Fact]
    public async Task Withdrawal_uses_decision_withdrawn_type()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedReviewContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context.Repo.Id, [context.ReviewerA.Id]);

        Assert.Equal(
            HttpStatusCode.OK,
            (await DecideAsync(reviewer, review.Id, "APPROVED", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await DecideAsync(reviewer, review.Id, "PENDING", null)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notification = await db.Notifications.AsNoTracking().SingleAsync(
            m => m.ReviewId == review.Id
                && m.UserId == context.Submitter.Id
                && m.Type == NotificationType.DecisionWithdrawn);

        using var payload = JsonDocument.Parse(notification.PayloadJson);
        var root = payload.RootElement;
        Assert.Equal(review.Id, root.GetProperty("reviewId").GetGuid());
        Assert.Equal(review.Title, root.GetProperty("title").GetString());
        Assert.Equal(context.Repo.Name, root.GetProperty("repoName").GetString());
        Assert.Equal(HeadBranch, root.GetProperty("headBranch").GetString());
        Assert.Equal(
            context.ReviewerA.DisplayName,
            root.GetProperty("actorDisplayName").GetString());
    }

    /// <summary>
    /// 撤回结论即使未改变聚合状态也要通知提交人：A 通过 + B 待审时 A 撤回，状态仍 OPEN 但收到一条 DECISION_WITHDRAWN。
    /// </summary>
    [Fact]
    public async Task Withdrawal_without_status_change_still_notifies_submitter()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedReviewContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewerA = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, [context.ReviewerA.Id, context.ReviewerB.Id]);

        // A 通过，B 仍待审，评审单保持 OPEN
        Assert.Equal(
            HttpStatusCode.OK,
            (await DecideAsync(reviewerA, review.Id, "APPROVED", null)).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.Reviews.AsNoTracking().SingleAsync(m => m.Id == review.Id);
            Assert.Equal(ReviewStatus.Open, stored.Status);
        }

        // A 撤回，聚合状态仍 OPEN，但提交人收到一条 DECISION_WITHDRAWN
        Assert.Equal(
            HttpStatusCode.OK,
            (await DecideAsync(reviewerA, review.Id, "PENDING", null)).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.Reviews.AsNoTracking().SingleAsync(m => m.Id == review.Id);
            Assert.Equal(ReviewStatus.Open, stored.Status);

            var withdrawn = await db.Notifications.AsNoTracking()
                .Where(m => m.ReviewId == review.Id
                    && m.UserId == context.Submitter.Id
                    && m.Type == NotificationType.DecisionWithdrawn)
                .ToListAsync();
            Assert.Single(withdrawn);
        }
    }

    /// <summary>
    /// 新增审核人时，新加入的人收到 REVIEW_SUBMITTED 通知。
    /// </summary>
    [Fact]
    public async Task Added_reviewer_receives_review_submitted()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedReviewContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context.Repo.Id, [context.ReviewerA.Id]);

        var added = await submitter.PostJsonWithCsrfAsync(
            $"/api/reviews/{review.Id}/reviewers",
            new { userIds = new[] { context.ReviewerB.Id } });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notification = await db.Notifications.AsNoTracking().SingleAsync(
            m => m.ReviewId == review.Id
                && m.UserId == context.ReviewerB.Id
                && m.Type == NotificationType.ReviewSubmitted);

        using var payload = JsonDocument.Parse(notification.PayloadJson);
        var root = payload.RootElement;
        Assert.Equal(review.Id, root.GetProperty("reviewId").GetGuid());
        Assert.Equal(review.Title, root.GetProperty("title").GetString());
        Assert.Equal(context.Repo.Name, root.GetProperty("repoName").GetString());
    }

    /// <summary>
    /// 移除审核人的通知包含被移除人信息，提交人自己移除时不通知自己。
    /// </summary>
    [Fact]
    public async Task Reviewer_removal_payload_skips_self_notification()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedReviewContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter,
            context.Repo.Id,
            [context.ReviewerA.Id, context.ReviewerB.Id, context.ReviewerC.Id]);

        var removedBySubmitter = await submitter.DeleteWithCsrfAsync(
            $"/api/reviews/{review.Id}/reviewers/{context.ReviewerB.Id}");
        Assert.Equal(HttpStatusCode.OK, removedBySubmitter.StatusCode);
        Assert.Equal(0, await CountReviewerRemovedAsync(factory, review.Id, context.Submitter.Id));

        var admin = await factory.LoginAsAdminAsync();
        var removedByAdmin = await admin.DeleteWithCsrfAsync(
            $"/api/reviews/{review.Id}/reviewers/{context.ReviewerC.Id}");
        Assert.Equal(HttpStatusCode.OK, removedByAdmin.StatusCode);

        var notification = await ReadReviewerRemovedAsync(
            factory, review.Id, context.Submitter.Id);
        using var payload = JsonDocument.Parse(notification.PayloadJson);
        var root = payload.RootElement;
        Assert.Equal(review.Id, root.GetProperty("reviewId").GetGuid());
        Assert.Equal(context.ReviewerC.Id, root.GetProperty("removedUserId").GetGuid());
        Assert.Equal(
            context.ReviewerC.DisplayName,
            root.GetProperty("removedDisplayName").GetString());
        Assert.Equal("REMOVED_BY_ADMIN", root.GetProperty("reason").GetString());
    }

    /// <summary>
    /// 标记已读接口拒绝空请求体与既无 ids 也无 all 的请求体。
    /// </summary>
    [Fact]
    public async Task Read_rejects_empty_body()
    {
        using var factory = new TestAppFactory();
        var user = factory.SeedUser(UniqueName("reader"), TestPassword);
        var client = await factory.LoginAsync(user.Username, TestPassword);

        var empty = await PostRawAsync(client, "/api/notifications/read", string.Empty);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(
            "INVALID_BODY",
            (await empty.Content.ReadFromJsonAsync<ErrorBody>())!.Code);

        var noFields = await client.PostJsonWithCsrfAsync("/api/notifications/read", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noFields.StatusCode);
        Assert.Equal(
            "INVALID_BODY",
            (await noFields.Content.ReadFromJsonAsync<ErrorBody>())!.Code);
    }

    /// <summary>
    /// 准备仓库、成员关系、基线提交与 feature 分支。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="source">真实 git 测试仓库</param>
    /// <returns>通知测试上下文</returns>
    private static async Task<NotificationContext> SeedReviewContextAsync(
        TestAppFactory factory,
        TestGitRepo source)
    {
        var submitter = factory.SeedUser(UniqueName("submitter"), TestPassword);
        var reviewerA = factory.SeedUser(UniqueName("reviewer-a"), TestPassword);
        var reviewerB = factory.SeedUser(UniqueName("reviewer-b"), TestPassword);
        var reviewerC = factory.SeedUser(UniqueName("reviewer-c"), TestPassword);
        var repo = factory.SeedRepo(source.Url, UniqueName("notify"));
        SeedMember(factory, repo.Id, submitter.Id);
        SeedMember(factory, repo.Id, reviewerA.Id);
        SeedMember(factory, repo.Id, reviewerB.Id);
        SeedMember(factory, repo.Id, reviewerC.Id);
        await source.CreateBranchAsync(HeadBranch, 2);
        await FetchMirrorAsync(factory, repo);
        return new NotificationContext(repo, submitter, reviewerA, reviewerB, reviewerC);
    }

    /// <summary>
    /// 直接写入仓库成员关系。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="userId">用户主键</param>
    private static void SeedMember(TestAppFactory factory, Guid repoId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.RepoMembers.Add(new RepoMember
        {
            Id = Guid.NewGuid(),
            RepoId = repoId,
            UserId = userId,
            IsMerger = false
        });
        db.SaveChanges();
    }

    /// <summary>
    /// 用真实 git 把远端分支拉进本地镜像。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repo">仓库</param>
    /// <returns></returns>
    private static async Task FetchMirrorAsync(TestAppFactory factory, Repo repo)
    {
        var mirror = factory.Services.GetRequiredService<MirrorService>();
        await mirror.FetchAsync(repo.Id, repo.GitUrl, null, CancellationToken.None);
    }

    /// <summary>
    /// 通过 HTTP 创建评审单并返回详情。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="reviewerIds">审核人主键</param>
    /// <returns>评审单详情</returns>
    private static async Task<ReviewDetailBody> CreateReviewAsync(
        HttpClient client,
        Guid repoId,
        IReadOnlyList<Guid> reviewerIds)
    {
        var response = await client.PostJsonWithCsrfAsync(
            "/api/reviews",
            new
            {
                repoId,
                headBranch = HeadBranch,
                baseBranch = BaseBranch,
                title = "通知测试",
                description = (string?)null,
                reviewerIds
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// 提交审核结论。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="state">结论状态</param>
    /// <param name="note">结论附言</param>
    /// <returns>HTTP 响应</returns>
    private static Task<HttpResponseMessage> DecideAsync(
        HttpClient client,
        Guid reviewId,
        string state,
        string? note)
    {
        return client.PostJsonWithCsrfAsync(
            $"/api/reviews/{reviewId}/decisions",
            new { state, note });
    }

    /// <summary>
    /// 直接写入一条通知，并可覆盖创建时间与已读时间。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="userId">接收人主键</param>
    /// <param name="type">通知类型</param>
    /// <param name="reviewId">关联评审单，可空</param>
    /// <param name="payload">payload 对象</param>
    /// <param name="createdAt">创建时间，可空</param>
    /// <param name="readAt">阅读时间，可空</param>
    /// <returns>通知主键</returns>
    private static Guid SeedNotification(
        TestAppFactory factory,
        Guid userId,
        NotificationType type,
        Guid? reviewId,
        object payload,
        DateTime? createdAt = null,
        DateTime? readAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            ReviewId = reviewId,
            PayloadJson = JsonSerializer.Serialize(
                payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ReadAt = readAt
        };

        db.Notifications.Add(notification);
        db.SaveChanges();

        if (createdAt is not null)
        {
            db.Notifications
                .Where(m => m.Id == notification.Id)
                .ExecuteUpdate(m => m.SetProperty(n => n.CreatedAt, createdAt.Value));
        }

        return notification.Id;
    }

    /// <summary>
    /// 通过通知服务写入一条通知，并返回该事件最新一行及用户的通知总数。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="userId">接收人主键</param>
    /// <param name="type">通知类型</param>
    /// <param name="reviewId">关联评审单，可空</param>
    /// <param name="payload">payload 对象</param>
    /// <returns>最新一行快照</returns>
    private static async Task<NotificationSnapshot> CreateNotificationAsync(
        TestAppFactory factory,
        Guid userId,
        NotificationType type,
        Guid? reviewId,
        object payload)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationService>();
        await service.CreateAsync(userId, type, reviewId, payload, CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Notifications.AsNoTracking()
            .Where(m => m.UserId == userId && m.Type == type && m.ReviewId == reviewId)
            .OrderByDescending(m => m.CreatedAt)
            .FirstAsync();
        var count = await db.Notifications.AsNoTracking()
            .CountAsync(m => m.UserId == userId);
        return new NotificationSnapshot(row.Id, row.PayloadJson, row.CreatedAt, count);
    }

    /// <summary>
    /// 把指定通知标记为已读。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="notificationId">通知主键</param>
    /// <returns></returns>
    private static async Task MarkReadAsync(TestAppFactory factory, Guid notificationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        await db.Notifications
            .Where(m => m.Id == notificationId)
            .ExecuteUpdateAsync(m => m.SetProperty(n => n.ReadAt, now), CancellationToken.None);
    }

    /// <summary>
    /// 覆盖指定通知的创建时间。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="notificationId">通知主键</param>
    /// <param name="createdAt">新的创建时间</param>
    /// <returns></returns>
    private static async Task SetCreatedAtAsync(
        TestAppFactory factory,
        Guid notificationId,
        DateTime createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Notifications
            .Where(m => m.Id == notificationId)
            .ExecuteUpdateAsync(
                m => m.SetProperty(n => n.CreatedAt, createdAt),
                CancellationToken.None);
    }

    /// <summary>
    /// 读取当前用户的未读数。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <returns>未读数</returns>
    private static async Task<int> ReadUnreadCountAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<UnreadCountBody>(
            "/api/notifications/unread-count");
        Assert.NotNull(body);
        return body.Count;
    }

    /// <summary>
    /// 统计指定用户的未读通知数。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="userId">用户主键</param>
    /// <returns>未读数</returns>
    private static async Task<int> CountUnreadAsync(TestAppFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Notifications.AsNoTracking()
            .CountAsync(m => m.UserId == userId && m.ReadAt == null);
    }

    /// <summary>
    /// 读取指定通知的已读时间。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="notificationId">通知主键</param>
    /// <returns>已读时间，未读返回 null</returns>
    private static async Task<DateTime?> ReadReadAtAsync(
        TestAppFactory factory,
        Guid notificationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Notifications.AsNoTracking()
            .Where(m => m.Id == notificationId)
            .Select(m => m.ReadAt)
            .SingleAsync();
    }

    /// <summary>
    /// 统计评审单下发给某用户的审核人移除通知数。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">接收人主键</param>
    /// <returns>通知数量</returns>
    private static async Task<int> CountReviewerRemovedAsync(
        TestAppFactory factory,
        Guid reviewId,
        Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Notifications.AsNoTracking()
            .CountAsync(m => m.ReviewId == reviewId
                && m.UserId == userId
                && m.Type == NotificationType.ReviewerRemoved);
    }

    /// <summary>
    /// 读取评审单下发给某用户的唯一一条审核人移除通知。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">接收人主键</param>
    /// <returns>通知行</returns>
    private static async Task<Notification> ReadReviewerRemovedAsync(
        TestAppFactory factory,
        Guid reviewId,
        Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Notifications.AsNoTracking().SingleAsync(
            m => m.ReviewId == reviewId
                && m.UserId == userId
                && m.Type == NotificationType.ReviewerRemoved);
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

    /// <summary>
    /// 读取通知列表。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="query">查询字符串</param>
    /// <returns>通知列表响应</returns>
    private static async Task<NotificationListBody> ReadNotificationsAsync(
        HttpClient client,
        string query)
    {
        var body = await client.GetFromJsonAsync<NotificationListBody>(
            $"/api/notifications{query}");
        Assert.NotNull(body);
        return body;
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

    /// <summary>
    /// 通知测试上下文：数据库仓库与相关用户。
    /// </summary>
    /// <param name="Repo">数据库仓库</param>
    /// <param name="Submitter">提交人</param>
    /// <param name="ReviewerA">第一审核人</param>
    /// <param name="ReviewerB">第二审核人</param>
    /// <param name="ReviewerC">第三审核人</param>
    private sealed record NotificationContext(
        Repo Repo,
        User Submitter,
        User ReviewerA,
        User ReviewerB,
        User ReviewerC);

    /// <summary>
    /// 通知去重测试的行快照。
    /// </summary>
    /// <param name="Id">通知主键</param>
    /// <param name="PayloadJson">payload JSON</param>
    /// <param name="CreatedAt">创建时间</param>
    /// <param name="Count">接收人当前的通知总数</param>
    private sealed record NotificationSnapshot(
        Guid Id,
        string PayloadJson,
        DateTime CreatedAt,
        int Count);
}

/// <summary>
/// 通知列表响应 DTO。
/// </summary>
/// <param name="Items">当前页数据</param>
/// <param name="Total">总条数</param>
/// <param name="Page">当前页码</param>
/// <param name="PageSize">每页条数</param>
public sealed record NotificationListBody(
    IReadOnlyList<NotificationBody> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// 通知项 DTO。
/// </summary>
/// <param name="Id">通知主键</param>
/// <param name="Type">通知类型</param>
/// <param name="ReviewId">关联评审单</param>
/// <param name="Payload">payload 对象</param>
/// <param name="ReadAt">阅读时间</param>
/// <param name="CreatedAt">创建时间</param>
public sealed record NotificationBody(
    Guid Id,
    string Type,
    Guid? ReviewId,
    JsonElement Payload,
    DateTime? ReadAt,
    DateTime CreatedAt);

/// <summary>
/// 未读数响应 DTO。
/// </summary>
/// <param name="Count">未读数</param>
public sealed record UnreadCountBody(int Count);

/// <summary>
/// 标记已读响应 DTO。
/// </summary>
/// <param name="Updated">本次实际更新行数</param>
public sealed record MarkReadBody(int Updated);

using System.Net;
using System.Net.Http.Json;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 管理员用户管理相关测试。
/// </summary>
public class UsersTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    /// <summary>
    /// 管理员可以创建用户，且列表接口能返回该用户。
    /// </summary>
    [Fact]
    public async Task Admin_can_create_user_and_list_users()
    {
        var admin = await factory.LoginAsAdminAsync();

        var create = await admin.PostJsonWithCsrfAsync(
            "/api/users",
            new
            {
                username = "listed-user",
                password = "Passw0rd!",
                displayName = "列表用户",
                email = "listed@example.com",
                isAdmin = false
            });
        var created = await create.Content.ReadFromJsonAsync<UserBody>();

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.NotNull(created);
        Assert.Equal("listed-user", created.Username);
        Assert.True(created.MustChangePassword);

        var list = await admin.GetFromJsonAsync<UserListBody>("/api/users?page=1&pageSize=100");

        Assert.NotNull(list);
        Assert.Equal(1, list.Page);
        Assert.Equal(100, list.PageSize);
        Assert.Contains(list.Items, m => m.Id == created.Id && m.Username == "listed-user");

        // 默认 pageSize 为 20，超过上限时收敛到 100
        var defaultList = await admin.GetFromJsonAsync<UserListBody>("/api/users");
        var clampedList = await admin.GetFromJsonAsync<UserListBody>("/api/users?pageSize=500");
        Assert.Equal(20, defaultList!.PageSize);
        Assert.Equal(100, clampedList!.PageSize);

        // 最新创建的用户排在最前
        Assert.Equal(created.Id, list.Items[0].Id);
    }

    /// <summary>
    /// 用户名重复（大小写不敏感）应返回 409。
    /// </summary>
    [Fact]
    public async Task Duplicate_username_is_rejected_case_insensitively()
    {
        var admin = await factory.LoginAsAdminAsync();
        var first = await admin.PostJsonWithCsrfAsync(
            "/api/users",
            new { username = "Alice", password = "Passw0rd!", displayName = "爱丽丝" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var duplicate = await admin.PostJsonWithCsrfAsync(
            "/api/users",
            new { username = "alice", password = "Passw0rd!", displayName = "小写爱丽丝" });
        var body = await duplicate.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("USERNAME_EXISTS", body.Code);
    }

    /// <summary>
    /// 停用审核人应把未结束评审中的状态改为 REMOVED，并通知提交人补人。
    /// </summary>
    [Fact]
    public async Task Deactivating_user_marks_reviewers_removed_and_notifies_submitter()
    {
        var reviewer = factory.SeedUser("reviewer-to-remove", "Passw0rd!");
        var submitter = factory.SeedUser("submitter-one", "Passw0rd!");
        var review = SeedReview(submitter, (reviewer, ReviewerState.Pending));

        var admin = await factory.LoginAsAdminAsync();
        var patch = await admin.PatchJsonWithCsrfAsync(
            $"/api/users/{reviewer.Id}",
            new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var assignment = await db.ReviewReviewers.SingleAsync(
            m => m.ReviewId == review.Id && m.UserId == reviewer.Id);
        var notification = await db.Notifications.SingleAsync(
            m => m.ReviewId == review.Id && m.UserId == submitter.Id);

        Assert.Equal(ReviewerState.Removed, assignment.State);
        Assert.Equal(NotificationType.ReviewerRemoved, notification.Type);
    }

    /// <summary>
    /// 停用唯一一名“要求修改”的审核人后，评审单回到 OPEN。
    /// </summary>
    [Fact]
    public async Task Deactivating_only_changes_requested_reviewer_returns_review_to_open()
    {
        var submitter = factory.SeedUser("submitter-cr", "Passw0rd!");
        var reviewer = factory.SeedUser("reviewer-cr", "Passw0rd!");
        var review = SeedReview(submitter, (reviewer, ReviewerState.ChangesRequested));

        var admin = await factory.LoginAsAdminAsync();
        var patch = await admin.PatchJsonWithCsrfAsync(
            $"/api/users/{reviewer.Id}",
            new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Reviews.AsNoTracking().SingleAsync(m => m.Id == review.Id);
        Assert.Equal(ReviewStatus.Open, stored.Status);
    }

    /// <summary>
    /// 停用“要求修改”的审核人且仍有审核人已通过时，评审单重算为 APPROVED。
    /// </summary>
    [Fact]
    public async Task Deactivating_changes_requested_reviewer_recomputes_to_approved()
    {
        var submitter = factory.SeedUser("submitter-cr2", "Passw0rd!");
        var changes = factory.SeedUser("reviewer-cr2", "Passw0rd!");
        var approver = factory.SeedUser("reviewer-ok2", "Passw0rd!");
        var review = SeedReview(
            submitter,
            (changes, ReviewerState.ChangesRequested),
            (approver, ReviewerState.Approved));

        var admin = await factory.LoginAsAdminAsync();
        var patch = await admin.PatchJsonWithCsrfAsync(
            $"/api/users/{changes.Id}",
            new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Reviews.AsNoTracking().SingleAsync(m => m.Id == review.Id);
        Assert.Equal(ReviewStatus.Approved, stored.Status);
    }

    /// <summary>
    /// 停用两名已通过审核人中的一名时，评审单仍为 APPROVED。
    /// </summary>
    [Fact]
    public async Task Deactivating_one_approved_reviewer_keeps_review_approved()
    {
        var submitter = factory.SeedUser("submitter-ok", "Passw0rd!");
        var first = factory.SeedUser("reviewer-ok-a", "Passw0rd!");
        var second = factory.SeedUser("reviewer-ok-b", "Passw0rd!");
        var review = SeedReview(
            submitter,
            (first, ReviewerState.Approved),
            (second, ReviewerState.Approved));

        var admin = await factory.LoginAsAdminAsync();
        var patch = await admin.PatchJsonWithCsrfAsync(
            $"/api/users/{first.Id}",
            new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Reviews.AsNoTracking().SingleAsync(m => m.Id == review.Id);
        var removed = await db.ReviewReviewers.AsNoTracking()
            .SingleAsync(m => m.ReviewId == review.Id && m.UserId == first.Id);

        Assert.Equal(ReviewStatus.Approved, stored.Status);
        Assert.Equal(ReviewerState.Removed, removed.State);
    }

    /// <summary>
    /// 停用仍待评审的审核人不触发重算，不能借移除待审人自动通过。
    /// </summary>
    [Fact]
    public async Task Deactivating_pending_reviewer_does_not_auto_approve()
    {
        var submitter = factory.SeedUser("submitter-pending", "Passw0rd!");
        var approver = factory.SeedUser("reviewer-approved", "Passw0rd!");
        var pending = factory.SeedUser("reviewer-pending", "Passw0rd!");
        var review = SeedReview(
            submitter,
            (approver, ReviewerState.Approved),
            (pending, ReviewerState.Pending));

        var admin = await factory.LoginAsAdminAsync();
        var patch = await admin.PatchJsonWithCsrfAsync(
            $"/api/users/{pending.Id}",
            new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Reviews.AsNoTracking().SingleAsync(m => m.Id == review.Id);
        Assert.Equal(ReviewStatus.Open, stored.Status);
    }

    /// <summary>
    /// 不能停用最后一个启用中的管理员。
    /// </summary>
    [Fact]
    public async Task Cannot_deactivate_last_active_admin()
    {
        var admin = await factory.LoginAsAdminAsync();

        var response = await admin.PatchJsonWithCsrfAsync(
            $"/api/users/{factory.AdminUserId}",
            new { isActive = false });
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("LAST_ADMIN", body.Code);
    }

    /// <summary>
    /// 管理员重置密码应返回一次性新密码，并强制用户下次登录后改密。
    /// </summary>
    [Fact]
    public async Task Reset_password_returns_one_time_password_and_forces_change()
    {
        var target = factory.SeedUser("reset-target", "OldPass123!");
        var admin = await factory.LoginAsAdminAsync();

        var reset = await admin.PostJsonWithCsrfAsync(
            $"/api/users/{target.Id}/reset-password",
            new { });
        var body = await reset.Content.ReadFromJsonAsync<ResetPasswordBody>();

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.NotNull(body);
        Assert.True(body.Password.Length >= 8);

        var user = await factory.LoginAsync("reset-target", body.Password);
        var me = await user.GetFromJsonAsync<MeBody>("/api/auth/me");

        Assert.NotNull(me);
        Assert.True(me.MustChangePassword);
    }

    /// <summary>
    /// 准备一个进行中的评审单，挂上指定结论状态的审核人，并按状态机对齐评审单状态。
    /// </summary>
    /// <param name="submitter">提交人</param>
    /// <param name="reviewers">审核人及其结论状态</param>
    /// <returns>创建的评审单</returns>
    private Review SeedReview(User submitter, params (User Reviewer, ReviewerState State)[] reviewers)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var repo = new Repo
        {
            Id = Guid.NewGuid(),
            Name = $"repo-{Guid.NewGuid():N}",
            GitUrl = "https://example.com/repo.git",
            DefaultBaseBranch = "main",
            IsActive = true
        };
        var review = new Review
        {
            Id = Guid.NewGuid(),
            RepoId = repo.Id,
            BaseBranch = "main",
            HeadBranch = $"feature/{Guid.NewGuid():N}",
            SubmitterId = submitter.Id,
            Title = "停用审核人验证",
            Status = ReviewStateMachine.Recompute(reviewers.Select(m => m.State)),
            BaseSha = "base-sha",
            HeadSha = "head-sha"
        };

        db.Repos.Add(repo);
        db.Reviews.Add(review);
        foreach (var (reviewer, state) in reviewers)
        {
            db.ReviewReviewers.Add(new ReviewReviewer
            {
                Id = Guid.NewGuid(),
                ReviewId = review.Id,
                UserId = reviewer.Id,
                State = state
            });
        }

        db.SaveChanges();
        return review;
    }
}

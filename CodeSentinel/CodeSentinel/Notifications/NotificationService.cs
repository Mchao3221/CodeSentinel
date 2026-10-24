using System.Text.Json;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CodeSentinel.Notifications;

/// <summary>
/// 站内通知写入服务：按用户落库，payload 使用应用的 JSON 序列化配置。
/// </summary>
public sealed class NotificationService
{
    private const int DedupeWindowMinutes = 5;

    private readonly AppDbContext _db;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// 按数据库上下文与应用 JSON 配置创建通知服务。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="jsonOptions">应用的 HTTP JSON 配置</param>
    public NotificationService(AppDbContext db, IOptions<JsonOptions> jsonOptions)
    {
        _db = db;
        _jsonOptions = jsonOptions.Value.SerializerOptions;
    }

    /// <summary>
    /// 为单个用户写入一条通知并立即保存；五分钟内同一用户、类型与评审单的未读通知改为更新原记录。
    /// </summary>
    /// <param name="userId">接收人主键</param>
    /// <param name="type">通知类型</param>
    /// <param name="reviewId">关联评审单主键，可为空</param>
    /// <param name="payload">渲染所需的最小字段</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public Task CreateAsync(
        Guid userId,
        NotificationType type,
        Guid? reviewId,
        object payload,
        CancellationToken ct)
    {
        return CreateForUsersAsync(reviewId, [userId], type, payload, ct);
    }

    /// <summary>
    /// 为一批用户写入同一事件的通知：一次读取五分钟内可合并的未读行，存在则更新，否则新增。
    /// </summary>
    /// <param name="reviewId">关联评审单主键，可为空</param>
    /// <param name="userIds">接收人主键列表，重复值只保留一个</param>
    /// <param name="type">通知类型</param>
    /// <param name="payload">渲染所需的最小字段</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task CreateForUsersAsync(
        Guid? reviewId,
        IReadOnlyCollection<Guid> userIds,
        NotificationType type,
        object payload,
        CancellationToken ct)
    {
        var recipients = userIds.Distinct().ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var existing = await LoadMergeableAsync(recipients, type, reviewId, now, ct);
        ApplyPayload(
            recipients,
            existing,
            type,
            reviewId,
            JsonSerializer.Serialize(payload, _jsonOptions),
            now);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 一次读取这批接收人在五分钟内未读、可合并的通知行，按创建时间倒序。
    /// </summary>
    /// <param name="recipients">接收人主键列表</param>
    /// <param name="type">通知类型</param>
    /// <param name="reviewId">关联评审单主键，可为空</param>
    /// <param name="now">当前时间</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>可合并的通知行</returns>
    private async Task<List<Notification>> LoadMergeableAsync(
        IReadOnlyCollection<Guid> recipients,
        NotificationType type,
        Guid? reviewId,
        DateTime now,
        CancellationToken ct)
    {
        var windowStart = now.AddMinutes(-DedupeWindowMinutes);
        return await _db.Notifications
            .Where(m => recipients.Contains(m.UserId)
                && m.Type == type
                && m.ReviewId == reviewId
                && m.ReadAt == null
                && m.CreatedAt >= windowStart)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 按接收人合并已存在的行，其余接收人新增一条通知。
    /// </summary>
    /// <param name="recipients">接收人主键列表</param>
    /// <param name="existing">可合并的通知行，按创建时间倒序</param>
    /// <param name="type">通知类型</param>
    /// <param name="reviewId">关联评审单主键，可为空</param>
    /// <param name="payloadJson">序列化后的 payload</param>
    /// <param name="now">当前时间</param>
    private void ApplyPayload(
        IReadOnlyCollection<Guid> recipients,
        List<Notification> existing,
        NotificationType type,
        Guid? reviewId,
        string payloadJson,
        DateTime now)
    {
        var mergeableByUser = new Dictionary<Guid, Notification>();
        foreach (var row in existing)
        {
            // 同一接收人有多条可合并记录时只保留最新一条
            mergeableByUser.TryAdd(row.UserId, row);
        }

        foreach (var userId in recipients)
        {
            if (mergeableByUser.TryGetValue(userId, out var row))
            {
                // 合并同一事件：刷新内容并把创建时间推到当前，使其重新排到列表顶部
                row.PayloadJson = payloadJson;
                row.CreatedAt = now;
                continue;
            }

            _db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Type = type,
                ReviewId = reviewId,
                PayloadJson = payloadJson
            });
        }
    }

    /// <summary>
    /// 给评审单上所有未移除的审核人各发一条通知，评审单不存在时忽略。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="type">通知类型</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task NotifyReviewersAsync(Guid reviewId, NotificationType type, CancellationToken ct)
    {
        var reviewerIds = await _db.ReviewReviewers.AsNoTracking()
            .Where(m => m.ReviewId == reviewId && m.State != ReviewerState.Removed)
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(ct);

        await NotifyUsersAsync(reviewId, reviewerIds, type, ct);
    }

    /// <summary>
    /// 给去重后的接收人各发一条通知，评审单不存在时忽略。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userIds">接收人主键</param>
    /// <param name="type">通知类型</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task NotifyUsersAsync(
        Guid reviewId,
        IReadOnlyCollection<Guid> userIds,
        NotificationType type,
        CancellationToken ct)
    {
        var context = await LoadReviewContextAsync(reviewId, ct);
        if (context is null)
        {
            return;
        }

        var payload = new
        {
            reviewId,
            title = context.Title,
            repoName = context.RepoName,
            submitterName = context.SubmitterName,
            headBranch = context.HeadBranch,
            baseBranch = context.BaseBranch
        };

        await CreateForUsersAsync(reviewId, userIds, type, payload, ct);
    }

    /// <summary>
    /// 通知提交人某位审核人撤回了结论，payload 携带操作人显示名。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="submitterId">提交人主键</param>
    /// <param name="actorId">撤回结论的审核人主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task NotifyDecisionWithdrawnAsync(
        Guid reviewId,
        Guid submitterId,
        Guid actorId,
        CancellationToken ct)
    {
        var context = await LoadReviewContextAsync(reviewId, ct);
        if (context is null)
        {
            return;
        }

        var payload = new
        {
            reviewId,
            title = context.Title,
            repoName = context.RepoName,
            headBranch = context.HeadBranch,
            actorDisplayName = await LoadDisplayNameAsync(actorId, ct)
        };

        await CreateAsync(submitterId, NotificationType.DecisionWithdrawn, reviewId, payload, ct);
    }

    /// <summary>
    /// 通知提交人某位审核人被移出，payload 携带被移除人信息与移除原因。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="recipientId">接收人主键</param>
    /// <param name="removedUserId">被移除的审核人主键</param>
    /// <param name="reason">移除原因，例如 REMOVED_BY_ADMIN</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task NotifyReviewerRemovedAsync(
        Guid reviewId,
        Guid recipientId,
        Guid removedUserId,
        string reason,
        CancellationToken ct)
    {
        var context = await LoadReviewContextAsync(reviewId, ct);
        if (context is null)
        {
            return;
        }

        var payload = new
        {
            reviewId,
            title = context.Title,
            repoName = context.RepoName,
            submitterName = context.SubmitterName,
            headBranch = context.HeadBranch,
            baseBranch = context.BaseBranch,
            removedUserId,
            removedDisplayName = await LoadDisplayNameAsync(removedUserId, ct),
            reason
        };

        await CreateAsync(recipientId, NotificationType.ReviewerRemoved, reviewId, payload, ct);
    }

    /// <summary>
    /// 读取用户显示名，用户不存在时返回空字符串。
    /// </summary>
    /// <param name="userId">用户主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>显示名</returns>
    private async Task<string> LoadDisplayNameAsync(Guid userId, CancellationToken ct)
    {
        return await _db.Users.AsNoTracking()
            .Where(m => m.Id == userId)
            .Select(m => m.DisplayName)
            .FirstOrDefaultAsync(ct) ?? string.Empty;
    }

    /// <summary>
    /// 读取通知 payload 所需的评审单、仓库与提交人信息。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>上下文，评审单不存在时为空</returns>
    private async Task<ReviewNotificationContext?> LoadReviewContextAsync(
        Guid reviewId,
        CancellationToken ct)
    {
        return await (
            from review in _db.Reviews.AsNoTracking()
            join repo in _db.Repos.AsNoTracking() on review.RepoId equals repo.Id
            join submitter in _db.Users.AsNoTracking() on review.SubmitterId equals submitter.Id
            where review.Id == reviewId
            select new ReviewNotificationContext(
                review.Title,
                repo.Name,
                submitter.DisplayName,
                review.HeadBranch,
                review.BaseBranch))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// 通知 payload 的评审单上下文。
    /// </summary>
    /// <param name="Title">标题</param>
    /// <param name="RepoName">仓库名称</param>
    /// <param name="SubmitterName">提交人显示名</param>
    /// <param name="HeadBranch">提交分支</param>
    /// <param name="BaseBranch">目标分支</param>
    private sealed record ReviewNotificationContext(
        string Title,
        string RepoName,
        string SubmitterName,
        string HeadBranch,
        string BaseBranch);
}

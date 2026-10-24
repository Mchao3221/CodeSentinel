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
    /// 为单个用户写入一条通知并立即保存。
    /// </summary>
    /// <param name="userId">接收人主键</param>
    /// <param name="type">通知类型</param>
    /// <param name="reviewId">关联评审单主键</param>
    /// <param name="payload">渲染所需的最小字段</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task CreateAsync(
        Guid userId,
        NotificationType type,
        Guid reviewId,
        object payload,
        CancellationToken ct)
    {
        _db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            ReviewId = reviewId,
            PayloadJson = JsonSerializer.Serialize(payload, _jsonOptions)
        });

        await _db.SaveChangesAsync(ct);
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
        var context = await LoadReviewContextAsync(reviewId, ct);
        if (context is null)
        {
            return;
        }

        var reviewerIds = await _db.ReviewReviewers.AsNoTracking()
            .Where(m => m.ReviewId == reviewId && m.State != ReviewerState.Removed)
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(ct);

        var payload = new
        {
            reviewId,
            title = context.Title,
            repoName = context.RepoName,
            submitterName = context.SubmitterName,
            headBranch = context.HeadBranch,
            baseBranch = context.BaseBranch
        };

        foreach (var reviewerId in reviewerIds)
        {
            await CreateAsync(reviewerId, type, reviewId, payload, ct);
        }
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

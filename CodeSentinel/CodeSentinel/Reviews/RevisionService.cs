using CodeSentinel.Audit;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using CodeSentinel.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodeSentinel.Reviews;

/// <summary>
/// 评审版本服务：发现分支新提交时追加版本、清空旧结论并通知审核人。
/// </summary>
/// <param name="db">数据库上下文</param>
/// <param name="mirrorService">镜像服务，用于读取仓库分支 head</param>
/// <param name="auditService">审计服务</param>
/// <param name="notificationService">通知服务</param>
/// <param name="anchorService">评论重新锚定服务，Task 11 前为空实现</param>
/// <param name="logger">日志记录器</param>
public sealed class RevisionService(
    AppDbContext db,
    MirrorService mirrorService,
    AuditService auditService,
    NotificationService notificationService,
    AnchorService anchorService,
    ILogger<RevisionService> logger)
{
    /// <summary>
    /// 新版本创建审计动作。
    /// </summary>
    public const string RevisionCreatedAction = "REVISION_CREATED";

    private const string ReviewTargetType = "REVIEW";
    private const int MaxAddAttempts = 2;

    /// <summary>
    /// 为新 head 生成下一个版本；head 未变化或评审单不再活跃时返回 false。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="newHeadSha">新 head 提交</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否生成了新版本</returns>
    public async Task<bool> AddRevisionAsync(Guid reviewId, string newHeadSha, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAddAttempts; attempt++)
        {
            var result = await TryAddRevisionOnceAsync(reviewId, newHeadSha, ct);

            if (result.Outcome == AddRevisionOutcome.Created)
            {
                await TryReanchorAsync(
                    reviewId, result.FromRevisionId, result.ToRevisionId, ct);
                return true;
            }

            if (result.Outcome != AddRevisionOutcome.Conflict)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 同步仓库下全部活跃评审单：分支 head 变化时追加新版本，分支已删除时跳过。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task SyncActiveReviewsAsync(Guid repoId, CancellationToken ct)
    {
        var reviews = await db.Reviews.AsNoTracking()
            .Where(m => m.RepoId == repoId
                && (m.Status == ReviewStatus.Open
                    || m.Status == ReviewStatus.ChangesRequested
                    || m.Status == ReviewStatus.Approved))
            .Select(m => new { m.Id, m.HeadBranch, m.HeadSha })
            .ToListAsync(ct);

        // 没有活跃评审单时不必再执行 git 分支查询
        if (reviews.Count == 0)
        {
            return;
        }

        var branches = await mirrorService.ListBranchesAsync(repoId, ct);
        var headByBranch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var branch in branches)
        {
            // 同仓库分支名大小写不敏感，重复名字以后出现的为准
            headByBranch[branch.Name] = branch.HeadSha;
        }

        foreach (var review in reviews)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (!headByBranch.TryGetValue(review.HeadBranch, out var head)
                || string.Equals(head, review.HeadSha, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await AddRevisionAsync(review.Id, head, ct);
        }
    }

    /// <summary>
    /// 尝试一次版本追加：乐观更新评审单、插入版本、重置审核人、发通知，全部在一个事务内完成。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="newHeadSha">新 head 提交</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>本次尝试结果</returns>
    private async Task<AddRevisionResult> TryAddRevisionOnceAsync(
        Guid reviewId,
        string newHeadSha,
        CancellationToken ct)
    {
        var snapshot = await LoadSnapshotAsync(reviewId, ct);
        if (snapshot is null
            || !IsActive(snapshot.Status)
            || string.Equals(snapshot.HeadSha, newHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return AddRevisionResult.Skipped();
        }

        var newRevisionId = Guid.NewGuid();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            if (!await TryMarkReviewAsync(snapshot, newRevisionId, newHeadSha, ct))
            {
                await transaction.RollbackAsync(ct);
                return AddRevisionResult.Conflict();
            }

            await InsertRevisionAsync(snapshot, newRevisionId, newHeadSha, ct);
            await ResetReviewersAsync(reviewId, newRevisionId, ct);
            await NotifyNewCommitsAsync(snapshot, newHeadSha, ct);
            await transaction.CommitAsync(ct);
            return AddRevisionResult.Created(snapshot.CurrentRevisionId, newRevisionId);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await RollbackQuietlyAsync(transaction);
            DetachTrackedChanges();
            return AddRevisionResult.Conflict();
        }
        catch (Exception)
        {
            await RollbackQuietlyAsync(transaction);
            DetachTrackedChanges();
            throw;
        }
    }

    /// <summary>
    /// 读取评审单、仓库名与当前版本信息；数据不完整时返回空。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>版本追加所需的快照</returns>
    private async Task<RevisionSnapshot?> LoadSnapshotAsync(Guid reviewId, CancellationToken ct)
    {
        var row = await (
            from review in db.Reviews.AsNoTracking()
            join repo in db.Repos.AsNoTracking() on review.RepoId equals repo.Id
            where review.Id == reviewId
            select new
            {
                review.Id,
                review.Status,
                review.HeadSha,
                review.CurrentRevisionId,
                review.Title,
                review.HeadBranch,
                review.BaseBranch,
                RepoName = repo.Name
            })
            .FirstOrDefaultAsync(ct);

        if (row?.CurrentRevisionId is null)
        {
            return null;
        }

        var current = await db.ReviewRevisions.AsNoTracking()
            .Where(m => m.Id == row.CurrentRevisionId.Value)
            .Select(m => new { m.Seq, m.BaseSha })
            .FirstOrDefaultAsync(ct);

        if (current is null)
        {
            return null;
        }

        return new RevisionSnapshot(
            row.Id,
            row.Status,
            row.HeadSha,
            row.CurrentRevisionId.Value,
            current.Seq,
            current.BaseSha,
            row.Title,
            row.HeadBranch,
            row.BaseBranch,
            row.RepoName);
    }

    /// <summary>
    /// 乐观更新评审单：仅当当前版本未变化且仍活跃时，切换到新版本并清空通过时间。
    /// </summary>
    /// <param name="snapshot">追加前的评审单快照</param>
    /// <param name="newRevisionId">新版本主键</param>
    /// <param name="newHeadSha">新 head 提交</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否更新成功</returns>
    private async Task<bool> TryMarkReviewAsync(
        RevisionSnapshot snapshot,
        Guid newRevisionId,
        string newHeadSha,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var affected = await db.Reviews
            .Where(m => m.Id == snapshot.ReviewId
                && m.CurrentRevisionId == snapshot.CurrentRevisionId
                && (m.Status == ReviewStatus.Open
                    || m.Status == ReviewStatus.ChangesRequested
                    || m.Status == ReviewStatus.Approved))
            .ExecuteUpdateAsync(m => m
                .SetProperty(r => r.CurrentRevisionId, newRevisionId)
                .SetProperty(r => r.HeadSha, newHeadSha)
                .SetProperty(r => r.Status, ReviewStatus.Open)
                .SetProperty(r => r.ApprovedAt, (DateTime?)null)
                .SetProperty(r => r.MergeHint, false)
                .SetProperty(r => r.MergeHintCount, 0)
                .SetProperty(r => r.UpdatedAt, now), ct);

        return affected > 0;
    }

    /// <summary>
    /// 插入新版本并写入审计，由调用方保存。
    /// </summary>
    /// <param name="snapshot">追加前的评审单快照</param>
    /// <param name="newRevisionId">新版本主键</param>
    /// <param name="newHeadSha">新 head 提交</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task InsertRevisionAsync(
        RevisionSnapshot snapshot,
        Guid newRevisionId,
        string newHeadSha,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        db.ReviewRevisions.Add(new ReviewRevision
        {
            Id = newRevisionId,
            ReviewId = snapshot.ReviewId,
            Seq = snapshot.Sequence + 1,
            BaseSha = snapshot.BaseSha,
            HeadSha = newHeadSha,
            CreatedAt = now,
            UpdatedAt = now
        });

        auditService.Add(null, RevisionCreatedAction, ReviewTargetType, snapshot.ReviewId, new
        {
            seq = snapshot.Sequence + 1,
            baseSha = snapshot.BaseSha,
            headSha = newHeadSha
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 把所有未移除的审核人重置为待审核，并指向新版本。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="revisionId">新版本主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task ResetReviewersAsync(
        Guid reviewId,
        Guid revisionId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.ReviewReviewers
            .Where(m => m.ReviewId == reviewId && m.State != ReviewerState.Removed)
            .ExecuteUpdateAsync(m => m
                .SetProperty(r => r.State, ReviewerState.Pending)
                .SetProperty(r => r.StateRevisionId, revisionId)
                .SetProperty(r => r.Note, (string?)null)
                .SetProperty(r => r.UpdatedAt, now), ct);
    }

    /// <summary>
    /// 给每个在册审核人发一条 NEW_COMMITS 通知，payload 带上新 head SHA。
    /// </summary>
    /// <param name="snapshot">追加前的评审单快照</param>
    /// <param name="newHeadSha">新 head 提交</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task NotifyNewCommitsAsync(
        RevisionSnapshot snapshot,
        string newHeadSha,
        CancellationToken ct)
    {
        var reviewerIds = await db.ReviewReviewers.AsNoTracking()
            .Where(m => m.ReviewId == snapshot.ReviewId && m.State != ReviewerState.Removed)
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(ct);
        var payload = new
        {
            reviewId = snapshot.ReviewId,
            title = snapshot.Title,
            repoName = snapshot.RepoName,
            headBranch = snapshot.HeadBranch,
            baseBranch = snapshot.BaseBranch,
            headSha = newHeadSha
        };

        foreach (var userId in reviewerIds)
        {
            await notificationService.CreateAsync(
                userId, NotificationType.NewCommits, snapshot.ReviewId, payload, ct);
        }
    }

    /// <summary>
    /// 事务提交后调用重新锚定；失败只记录日志，不影响已生成的新版本。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="fromRevisionId">旧版本主键</param>
    /// <param name="toRevisionId">新版本主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task TryReanchorAsync(
        Guid reviewId,
        Guid fromRevisionId,
        Guid toRevisionId,
        CancellationToken ct)
    {
        try
        {
            var report = await anchorService.ReanchorAsync(
                reviewId, fromRevisionId, toRevisionId, ct);
            logger.LogInformation(
                "评审单 {ReviewId} 从版本 {FromRevisionId} 重新锚定到 {ToRevisionId}：保留 {Kept}，移动 {Moved}，失效 {Outdated}",
                reviewId,
                fromRevisionId,
                toRevisionId,
                report.Kept,
                report.Moved,
                report.Outdated);
        }
        catch (Exception ex)
        {
            // 取消语义必须向上冒泡，不能被“不影响新版本”的兜底吞掉
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("评论重新锚定已取消", ex, ct);
            }

            logger.LogWarning(ex, "评审单 {ReviewId} 重新锚定失败，新版本已保留", reviewId);
        }
    }

    /// <summary>
    /// 静默回滚事务，清理失败路径上的连接状态。
    /// </summary>
    /// <param name="transaction">当前事务</param>
    /// <returns></returns>
    private async Task RollbackQuietlyAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "回滚评审版本事务失败");
        }
    }

    /// <summary>
    /// 移除失败尝试留下的跟踪实体，避免同一作用域内的后续保存重复写入。
    /// </summary>
    private void DetachTrackedChanges()
    {
        foreach (var entry in db.ChangeTracker.Entries()
            .Where(m => m.State is EntityState.Added
                or EntityState.Modified
                or EntityState.Deleted)
            .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// 判断评审单状态是否仍可追加版本。
    /// </summary>
    /// <param name="status">评审单状态</param>
    /// <returns>是否活跃</returns>
    private static bool IsActive(ReviewStatus status)
    {
        return status is ReviewStatus.Open
            or ReviewStatus.ChangesRequested
            or ReviewStatus.Approved;
    }

    /// <summary>
    /// 判断异常是否为 SQLite 唯一约束冲突。
    /// </summary>
    /// <param name="exception">EF 更新异常</param>
    /// <returns>是否为唯一约束冲突</returns>
    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        return exception.InnerException is SqliteException { SqliteErrorCode: 19 };
    }

    /// <summary>
    /// 追加版本所需的评审单与当前版本快照。
    /// </summary>
    /// <param name="ReviewId">评审单主键</param>
    /// <param name="Status">评审单状态</param>
    /// <param name="HeadSha">当前 head 提交</param>
    /// <param name="CurrentRevisionId">当前版本主键</param>
    /// <param name="Sequence">当前版本号</param>
    /// <param name="BaseSha">当前版本基线提交</param>
    /// <param name="Title">评审单标题</param>
    /// <param name="HeadBranch">提交分支</param>
    /// <param name="BaseBranch">目标分支</param>
    /// <param name="RepoName">仓库名称</param>
    private sealed record RevisionSnapshot(
        Guid ReviewId,
        ReviewStatus Status,
        string HeadSha,
        Guid CurrentRevisionId,
        int Sequence,
        string BaseSha,
        string Title,
        string HeadBranch,
        string BaseBranch,
        string RepoName);

    /// <summary>
    /// 一次追加尝试的结果。
    /// </summary>
    /// <param name="Outcome">结果类型</param>
    /// <param name="FromRevisionId">旧版本主键，仅成功时有效</param>
    /// <param name="ToRevisionId">新版本主键，仅成功时有效</param>
    private sealed record AddRevisionResult(
        AddRevisionOutcome Outcome,
        Guid FromRevisionId,
        Guid ToRevisionId)
    {
        /// <summary>
        /// 未发生变化，无需追加。
        /// </summary>
        /// <returns>跳过结果</returns>
        public static AddRevisionResult Skipped()
        {
            return new AddRevisionResult(AddRevisionOutcome.Skipped, Guid.Empty, Guid.Empty);
        }

        /// <summary>
        /// 并发冲突，需要重新读取后再试。
        /// </summary>
        /// <returns>冲突结果</returns>
        public static AddRevisionResult Conflict()
        {
            return new AddRevisionResult(AddRevisionOutcome.Conflict, Guid.Empty, Guid.Empty);
        }

        /// <summary>
        /// 成功创建新版本。
        /// </summary>
        /// <param name="fromRevisionId">旧版本主键</param>
        /// <param name="toRevisionId">新版本主键</param>
        /// <returns>成功结果</returns>
        public static AddRevisionResult Created(Guid fromRevisionId, Guid toRevisionId)
        {
            return new AddRevisionResult(
                AddRevisionOutcome.Created, fromRevisionId, toRevisionId);
        }
    }

    /// <summary>
    /// 追加尝试的结果类型。
    /// </summary>
    private enum AddRevisionOutcome
    {
        /// <summary>
        /// 已创建新版本。
        /// </summary>
        Created,

        /// <summary>
        /// head 未变化或评审单不再活跃。
        /// </summary>
        Skipped,

        /// <summary>
        /// 并发冲突。
        /// </summary>
        Conflict
    }
}

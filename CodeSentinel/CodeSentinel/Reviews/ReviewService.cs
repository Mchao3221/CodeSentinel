using CodeSentinel.Audit;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using CodeSentinel.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Reviews;

/// <summary>
/// 评审单服务：创建评审单、锁定版本 SHA 与组装详情。
/// </summary>
public sealed class ReviewService(
    AppDbContext db,
    MirrorService mirrorService,
    AuditService auditService,
    NotificationService notificationService)
{
    /// <summary>
    /// 创建评审单审计动作。
    /// </summary>
    public const string ReviewCreatedAction = "REVIEW_CREATED";

    private const string ReviewTargetType = "REVIEW";
    private const int MaxTitleLength = 200;
    private const string RemoteTrackingPrefix = "refs/remotes/origin/";
    private const string HeadRefPrefix = "refs/heads/";
    private const string ShortRemotePrefix = "origin/";
    private const string AnyRefPrefix = "refs/";

    /// <summary>
    /// 创建评审单：校验仓库成员、分支与审核人，写入评审单、版本 1、审核人、审计，并通知审核人。
    /// </summary>
    /// <param name="submitterId">提交人主键</param>
    /// <param name="request">创建请求</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>创建的评审单</returns>
    public async Task<Review> CreateAsync(
        Guid submitterId,
        CreateReviewRequest request,
        CancellationToken ct)
    {
        var repo = await LoadRepoForSubmitterAsync(request.RepoId, submitterId, ct);
        var baseBranch = NormalizeBranch(request.BaseBranch);
        var headBranch = NormalizeBranch(request.HeadBranch);
        var (baseSha, headSha) = await LockShasAsync(repo.Id, baseBranch, headBranch, ct);
        var reviewerIds = await NormalizeReviewersAsync(repo.Id, submitterId, request.ReviewerIds, ct);
        var title = NormalizeTitle(request.Title);
        var description = string.IsNullOrWhiteSpace(request.Description)
            ? null
            : request.Description.Trim();

        var review = BuildReview(
            repo.Id, submitterId, baseBranch, headBranch, baseSha, headSha,
            title, description, reviewerIds);

        auditService.Add(submitterId, ReviewCreatedAction, ReviewTargetType, review.Id, new
        {
            repoId = repo.Id,
            headBranch,
            baseBranch,
            baseSha,
            headSha,
            reviewerIds
        });

        await SaveCreatedAsync(review, ct);
        await notificationService.NotifyReviewersAsync(
            review.Id, NotificationType.ReviewSubmitted, ct);

        return review;
    }

    /// <summary>
    /// 读取评审单详情，并计算当前用户可执行的操作。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="isAdmin">当前用户是否管理员</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>评审单详情</returns>
    public async Task<ReviewDetailDto> GetDetailAsync(
        Guid reviewId,
        Guid userId,
        bool isAdmin,
        CancellationToken ct)
    {
        var review = await LoadAccessibleReviewAsync(reviewId, userId, isAdmin, ct);
        return await BuildDetailAsync(review, userId, isAdmin, ct);
    }

    /// <summary>
    /// 读取提交人所属仓库，非成员与不存在都返回 404。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="submitterId">提交人主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>仓库</returns>
    private async Task<Repo> LoadRepoForSubmitterAsync(
        Guid repoId,
        Guid submitterId,
        CancellationToken ct)
    {
        var repo = await db.Repos.AsNoTracking()
            .Where(m => m.Id == repoId
                && db.RepoMembers.Any(member => member.RepoId == m.Id && member.UserId == submitterId))
            .FirstOrDefaultAsync(ct);

        return repo ?? throw RepoNotFound();
    }

    /// <summary>
    /// 锁定两侧分支的 head SHA，并校验提交分支领先目标分支。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="baseBranch">目标分支</param>
    /// <param name="headBranch">提交分支</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>基线提交与评审提交</returns>
    private async Task<(string BaseSha, string HeadSha)> LockShasAsync(
        Guid repoId,
        string baseBranch,
        string headBranch,
        CancellationToken ct)
    {
        var baseSha = await mirrorService.GetHeadAsync(repoId, baseBranch, ct)
            ?? throw BranchNotFound(baseBranch);
        var headSha = await mirrorService.GetHeadAsync(repoId, headBranch, ct)
            ?? throw BranchNotFound(headBranch);

        if (!await mirrorService.IsAncestorAsync(repoId, baseSha, headSha, ct))
        {
            throw NoChanges();
        }

        var aheadBy = await mirrorService.CountAheadAsync(repoId, baseSha, headSha, ct);
        if (aheadBy <= 0)
        {
            throw NoChanges();
        }

        return (baseSha, headSha);
    }

    /// <summary>
    /// 校验审核人非空、不含提交人，且全部是启用中的仓库成员。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="submitterId">提交人主键</param>
    /// <param name="reviewerIds">请求中的审核人主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>去重后的审核人主键</returns>
    private async Task<IReadOnlyList<Guid>> NormalizeReviewersAsync(
        Guid repoId,
        Guid submitterId,
        IReadOnlyList<Guid>? reviewerIds,
        CancellationToken ct)
    {
        var ids = reviewerIds?
            .Where(m => m != Guid.Empty)
            .Distinct()
            .ToList() ?? [];

        if (ids.Count == 0)
        {
            throw InvalidReviewer("评审单至少需要一名审核人");
        }

        if (ids.Contains(submitterId))
        {
            throw InvalidReviewer("提交人不能作为评审人");
        }

        var memberCount = await db.RepoMembers.AsNoTracking()
            .Where(m => m.RepoId == repoId
                && ids.Contains(m.UserId)
                && db.Users.Any(user => user.Id == m.UserId && user.IsActive))
            .CountAsync(ct);

        if (memberCount != ids.Count)
        {
            throw InvalidReviewer("审核人必须是启用中的仓库成员");
        }

        return ids;
    }

    /// <summary>
    /// 构造评审单、版本 1 与待审核的审核人实体并加入工作单元。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="submitterId">提交人主键</param>
    /// <param name="baseBranch">目标分支</param>
    /// <param name="headBranch">提交分支</param>
    /// <param name="baseSha">基线提交</param>
    /// <param name="headSha">评审提交</param>
    /// <param name="title">标题</param>
    /// <param name="description">评审说明</param>
    /// <param name="reviewerIds">审核人主键</param>
    /// <returns>创建的评审单</returns>
    private Review BuildReview(
        Guid repoId,
        Guid submitterId,
        string baseBranch,
        string headBranch,
        string baseSha,
        string headSha,
        string title,
        string? description,
        IReadOnlyList<Guid> reviewerIds)
    {
        var reviewId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();

        var review = new Review
        {
            Id = reviewId,
            RepoId = repoId,
            BaseBranch = baseBranch,
            HeadBranch = headBranch,
            SubmitterId = submitterId,
            Title = title,
            Description = description,
            Status = ReviewStatus.Open,
            CurrentRevisionId = revisionId,
            BaseSha = baseSha,
            HeadSha = headSha
        };

        db.Reviews.Add(review);
        db.ReviewRevisions.Add(new ReviewRevision
        {
            Id = revisionId,
            ReviewId = reviewId,
            Seq = 1,
            BaseSha = baseSha,
            HeadSha = headSha
        });

        foreach (var reviewerId in reviewerIds)
        {
            db.ReviewReviewers.Add(new ReviewReviewer
            {
                Id = Guid.NewGuid(),
                ReviewId = reviewId,
                UserId = reviewerId,
                State = ReviewerState.Pending,
                StateRevisionId = revisionId
            });
        }

        return review;
    }

    /// <summary>
    /// 一次保存评审单、版本、审核人与审计；唯一索引冲突时翻译为带回已有主键的 409。
    /// </summary>
    /// <param name="review">待保存的评审单</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task SaveCreatedAsync(Review review, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            var existingId = await db.Reviews.AsNoTracking()
                .Where(m => m.RepoId == review.RepoId
                    && m.HeadBranch == review.HeadBranch
                    && (m.Status == ReviewStatus.Open
                        || m.Status == ReviewStatus.ChangesRequested
                        || m.Status == ReviewStatus.Approved))
                .Select(m => (Guid?)m.Id)
                .FirstOrDefaultAsync(ct);

            throw new ActiveReviewConflictException(
                existingId ?? Guid.Empty,
                "该分支已存在进行中的评审单");
        }
    }

    /// <summary>
    /// 读取评审单并校验当前用户是否可访问其仓库。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="isAdmin">当前用户是否管理员</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>评审单</returns>
    private async Task<Review> LoadAccessibleReviewAsync(
        Guid reviewId,
        Guid userId,
        bool isAdmin,
        CancellationToken ct)
    {
        var review = await db.Reviews.AsNoTracking().FirstOrDefaultAsync(m => m.Id == reviewId, ct)
            ?? throw ReviewNotFound();

        var allowed = isAdmin || await db.RepoMembers.AsNoTracking()
            .AnyAsync(m => m.RepoId == review.RepoId && m.UserId == userId, ct);

        return allowed ? review : throw ReviewNotFound();
    }

    /// <summary>
    /// 组装评审单详情，包括版本、审核人与当前用户权限。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="isAdmin">当前用户是否管理员</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>评审单详情</returns>
    private async Task<ReviewDetailDto> BuildDetailAsync(
        Review review,
        Guid userId,
        bool isAdmin,
        CancellationToken ct)
    {
        var repo = await db.Repos.AsNoTracking()
            .Where(m => m.Id == review.RepoId)
            .Select(m => new ReviewRepoRef(m.Id, m.Name))
            .FirstAsync(ct);
        var submitter = await db.Users.AsNoTracking()
            .Where(m => m.Id == review.SubmitterId)
            .Select(m => new ReviewUserRef(m.Id, m.DisplayName))
            .FirstAsync(ct);
        var isMerger = await IsMergerAsync(review.RepoId, userId, ct);
        var revisions = await LoadRevisionsAsync(review.Id, ct);
        var reviewerRows = await LoadReviewerRowsAsync(review.Id, ct);
        var currentRevision = revisions.FirstOrDefault(m => m.Id == review.CurrentRevisionId)?.Seq ?? 0;
        var permissions = BuildPermissions(review, userId, isAdmin, isMerger, reviewerRows);

        return new ReviewDetailDto(
            review.Id,
            repo,
            review.BaseBranch,
            review.HeadBranch,
            submitter,
            review.Title,
            review.Description,
            review.Status,
            review.BaseSha,
            review.HeadSha,
            currentRevision,
            revisions,
            reviewerRows.Select(m => new ReviewReviewerDto(m.UserId, m.DisplayName, m.State, m.Note)).ToList(),
            permissions,
            review.MergeHint,
            review.MergedAt,
            review.MergeCommitSha,
            review.CloseReason,
            review.CreatedAt,
            review.UpdatedAt);
    }

    /// <summary>
    /// 读取评审单的全部版本，按版本号升序。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>版本列表</returns>
    private async Task<IReadOnlyList<ReviewRevisionDto>> LoadRevisionsAsync(
        Guid reviewId,
        CancellationToken ct)
    {
        return await db.ReviewRevisions.AsNoTracking()
            .Where(m => m.ReviewId == reviewId)
            .OrderBy(m => m.Seq)
            .Select(m => new ReviewRevisionDto(m.Id, m.Seq, m.HeadSha, m.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// 读取评审单审核人及显示名，按加入顺序排列。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>审核人列表</returns>
    private async Task<IReadOnlyList<ReviewerRow>> LoadReviewerRowsAsync(
        Guid reviewId,
        CancellationToken ct)
    {
        return await (
            from reviewer in db.ReviewReviewers.AsNoTracking()
            join user in db.Users.AsNoTracking() on reviewer.UserId equals user.Id
            where reviewer.ReviewId == reviewId
            orderby reviewer.CreatedAt, reviewer.UserId
            select new ReviewerRow(
                reviewer.UserId,
                user.DisplayName,
                reviewer.State,
                reviewer.StateRevisionId,
                reviewer.Note))
            .ToListAsync(ct);
    }

    /// <summary>
    /// 计算当前用户对评审单的操作权限。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="isAdmin">当前用户是否管理员</param>
    /// <param name="isMerger">当前用户是否为仓库合并人</param>
    /// <param name="reviewers">审核人列表</param>
    /// <returns>操作权限</returns>
    private static ReviewPermissionsDto BuildPermissions(
        Review review,
        Guid userId,
        bool isAdmin,
        bool isMerger,
        IReadOnlyList<ReviewerRow> reviewers)
    {
        var assignment = reviewers.FirstOrDefault(
            m => m.UserId == userId && m.State != ReviewerState.Removed);
        var active = review.Status is ReviewStatus.Open
            or ReviewStatus.ChangesRequested
            or ReviewStatus.Approved;
        var canDecide = active
            && assignment is not null
            && assignment.StateRevisionId == review.CurrentRevisionId;

        return new ReviewPermissionsDto(
            canDecide,
            review.SubmitterId == userId || isAdmin,
            review.SubmitterId == userId || isMerger || isAdmin,
            review.Status == ReviewStatus.Approved && (isMerger || isAdmin));
    }

    /// <summary>
    /// 判断用户是否为仓库合并人。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="userId">用户主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否合并人</returns>
    private async Task<bool> IsMergerAsync(Guid repoId, Guid userId, CancellationToken ct)
    {
        return await db.RepoMembers.AsNoTracking()
            .AnyAsync(m => m.RepoId == repoId && m.UserId == userId && m.IsMerger, ct);
    }

    /// <summary>
    /// 校验并归一化分支名：远端跟踪引用、origin 前缀与 heads 引用统一成短名，其它引用直接拒绝。
    /// </summary>
    /// <param name="branch">原始分支名</param>
    /// <returns>短分支名</returns>
    private static string NormalizeBranch(string? branch)
    {
        var value = branch?.Trim() ?? string.Empty;

        // 逐层剥离已知前缀，避免 refs/remotes/origin/refs/heads/x 这类嵌套写法绕过归一化
        while (TryStripKnownPrefix(ref value))
        {
        }

        if (value.Length == 0 || value.StartsWith(AnyRefPrefix, StringComparison.Ordinal))
        {
            throw BranchNotFound(value);
        }

        return value;
    }

    /// <summary>
    /// 剥离一层已知的分支引用前缀。
    /// </summary>
    /// <param name="value">待归一化的分支名</param>
    /// <returns>是否发生了剥离</returns>
    private static bool TryStripKnownPrefix(ref string value)
    {
        if (value.StartsWith(RemoteTrackingPrefix, StringComparison.Ordinal))
        {
            value = value[RemoteTrackingPrefix.Length..];
            return true;
        }

        if (value.StartsWith(HeadRefPrefix, StringComparison.Ordinal))
        {
            value = value[HeadRefPrefix.Length..];
            return true;
        }

        if (value.StartsWith(ShortRemotePrefix, StringComparison.Ordinal))
        {
            value = value[ShortRemotePrefix.Length..];
            return true;
        }

        return false;
    }

    /// <summary>
    /// 校验并归一化标题。
    /// </summary>
    /// <param name="title">原始标题</param>
    /// <returns>去空白后的标题</returns>
    private static string NormalizeTitle(string? title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxTitleLength)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_TITLE",
                $"标题不能为空且不超过 {MaxTitleLength} 个字符");
        }

        return trimmed;
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
    /// 构造“仓库不存在”异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException RepoNotFound()
    {
        return new ApiException(StatusCodes.Status404NotFound, "NOT_FOUND", "仓库不存在");
    }

    /// <summary>
    /// 构造“评审单不存在”异常，越权时不暴露评审单是否存在。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException ReviewNotFound()
    {
        return new ApiException(StatusCodes.Status404NotFound, "NOT_FOUND", "评审单不存在");
    }

    /// <summary>
    /// 构造“分支不存在”异常。
    /// </summary>
    /// <param name="branch">分支名</param>
    /// <returns>业务异常</returns>
    private static ApiException BranchNotFound(string branch)
    {
        return new ApiException(
            StatusCodes.Status400BadRequest,
            "BRANCH_NOT_FOUND",
            string.IsNullOrEmpty(branch) ? "分支名不能为空" : $"镜像中不存在分支：{branch}");
    }

    /// <summary>
    /// 构造“没有可评审提交”异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException NoChanges()
    {
        return new ApiException(
            StatusCodes.Status400BadRequest,
            "NO_CHANGES",
            "提交分支没有领先目标分支的提交");
    }

    /// <summary>
    /// 构造“审核人不合法”异常。
    /// </summary>
    /// <param name="message">错误信息</param>
    /// <returns>业务异常</returns>
    private static ApiException InvalidReviewer(string message)
    {
        return new ApiException(StatusCodes.Status400BadRequest, "INVALID_REVIEWER", message);
    }

    /// <summary>
    /// 审核人明细行，供权限计算与详情组装使用。
    /// </summary>
    /// <param name="UserId">审核人主键</param>
    /// <param name="DisplayName">显示名</param>
    /// <param name="State">结论状态</param>
    /// <param name="StateRevisionId">结论对应版本</param>
    /// <param name="Note">结论附言</param>
    private sealed record ReviewerRow(
        Guid UserId,
        string DisplayName,
        ReviewerState State,
        Guid? StateRevisionId,
        string? Note);
}

/// <summary>
/// 活跃评审单唯一约束冲突，由端点转换成带回已有评审单主键的 409 响应。
/// </summary>
public sealed class ActiveReviewConflictException : Exception
{
    /// <summary>
    /// 已存在的活跃评审单主键。
    /// </summary>
    public Guid ReviewId { get; }

    /// <summary>
    /// 初始化活跃评审单冲突异常。
    /// </summary>
    /// <param name="reviewId">已存在的活跃评审单主键</param>
    /// <param name="message">错误信息</param>
    public ActiveReviewConflictException(Guid reviewId, string message) : base(message)
    {
        ReviewId = reviewId;
    }
}

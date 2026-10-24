using CodeSentinel.Audit;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using CodeSentinel.Notifications;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Reviews;

/// <summary>
/// 行级评论服务：创建、回复、编辑、软删除与解决状态流转。
/// </summary>
/// <param name="db">数据库上下文</param>
/// <param name="diffService">diff 引擎，用于校验行号与读取 blob id</param>
/// <param name="mirrorService">镜像服务，用于解析 OLD 侧的合并基</param>
/// <param name="auditService">审计服务</param>
/// <param name="notificationService">通知服务，用于回复通知</param>
public sealed class CommentService(
    AppDbContext db,
    DiffService diffService,
    MirrorService mirrorService,
    AuditService auditService,
    NotificationService notificationService)
{
    /// <summary>
    /// 创建评论审计动作。
    /// </summary>
    public const string CommentCreatedAction = "COMMENT_CREATED";

    /// <summary>
    /// 编辑评论审计动作。
    /// </summary>
    public const string CommentEditedAction = "COMMENT_EDITED";

    /// <summary>
    /// 删除评论审计动作。
    /// </summary>
    public const string CommentDeletedAction = "COMMENT_DELETED";

    /// <summary>
    /// 解决评论审计动作。
    /// </summary>
    public const string CommentResolvedAction = "COMMENT_RESOLVED";

    /// <summary>
    /// 取消解决审计动作。
    /// </summary>
    public const string CommentUnresolvedAction = "COMMENT_UNRESOLVED";

    private const string CommentTargetType = "COMMENT";
    private const int MaxBodyLength = 5000;
    private const int BodyExcerptLength = 200;

    /// <summary>
    /// 创建评论或回复：校验正文、行号与父评论，写入锚点原文与 blob id，并记录审计与回复通知。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="authorId">评论作者主键</param>
    /// <param name="request">创建评论请求</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>创建的评论</returns>
    public async Task<Comment> CreateAsync(
        Guid reviewId,
        Guid authorId,
        CreateCommentRequest request,
        CancellationToken ct)
    {
        var review = await ReviewAccess.LoadForMemberAsync(db, reviewId, authorId, ct);
        var body = NormalizeBody(request.Body);
        var side = ParseSide(request.Side);
        var path = request.Path?.Trim() ?? string.Empty;
        var parent = await LoadParentAsync(review.Id, request.ParentId, ct);
        var revision = await LoadCurrentRevisionAsync(review, ct);
        var file = await diffService.GetFileDiffAsync(
            review.RepoId, revision.BaseSha, revision.HeadSha, path, ct);
        EnsureCommentable(file);
        var line = FindLine(file, side, request.Line) ?? throw LineNotInDiff();
        var blobSha = await ResolveBlobShaAsync(review, revision, file, side, ct);

        var comment = new Comment
        {
            Id = Guid.NewGuid(),
            ReviewId = review.Id,
            RevisionId = revision.Id,
            AuthorId = authorId,
            FilePath = path,
            Side = ToSideValue(side),
            LineNumber = request.Line,
            AnchorText = line.Text,
            BlobSha = blobSha,
            Body = body,
            ParentId = parent?.Id,
            Status = CommentStatus.Open
        };

        var repliedToOther = await PersistCreatedAsync(
            comment, review.Id, authorId, side, request.Line, parent, ct);

        // 回复他人时通知被回复人
        if (repliedToOther && parent is not null)
        {
            await NotifyReplyAsync(comment, parent.AuthorId, ct);
        }

        return comment;
    }

    /// <summary>
    /// 读取评审单上的全部评论（含已删除与已失效），按创建时间升序；该接口有意不分页。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>评论列表</returns>
    public async Task<IReadOnlyList<CommentDto>> ListAsync(
        Guid reviewId,
        Guid userId,
        CancellationToken ct)
    {
        await ReviewAccess.LoadForMemberAsync(db, reviewId, userId, ct);

        // 单个评审单的评论量很小，按约定有意不做分页
        var rows = await (
            from comment in db.Comments.AsNoTracking()
            join author in db.Users.AsNoTracking() on comment.AuthorId equals author.Id
            where comment.ReviewId == reviewId
            orderby comment.CreatedAt, comment.Id
            select new { Comment = comment, AuthorName = author.DisplayName })
            .ToListAsync(ct);

        return rows.Select(m => ToDto(m.Comment, m.AuthorName)).ToList();
    }

    /// <summary>
    /// 修改评论正文：仅作者、仅 OPEN 或 RESOLVED 状态可编辑。
    /// </summary>
    /// <param name="commentId">评论主键</param>
    /// <param name="actorId">操作人主键</param>
    /// <param name="body">新正文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新后的评论</returns>
    public async Task<Comment> UpdateBodyAsync(
        Guid commentId,
        Guid actorId,
        string? body,
        CancellationToken ct)
    {
        var (comment, _) = await LoadCommentForMemberAsync(commentId, actorId, ct);
        if (comment.AuthorId != actorId)
        {
            throw Forbidden("只有作者可以编辑评论");
        }

        if (comment.Status is not (CommentStatus.Open or CommentStatus.Resolved))
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "COMMENT_NOT_EDITABLE",
                "该评论当前状态不允许编辑");
        }

        comment.Body = NormalizeBody(body);
        auditService.Add(actorId, CommentEditedAction, CommentTargetType, comment.Id, new
        {
            reviewId = comment.ReviewId
        });
        await db.SaveChangesAsync(ct);
        return comment;
    }

    /// <summary>
    /// 软删除评论：作者或管理员可删，回复不级联删除。
    /// </summary>
    /// <param name="commentId">评论主键</param>
    /// <param name="actorId">操作人主键</param>
    /// <param name="isAdmin">操作人是否管理员</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新后的评论</returns>
    public async Task<Comment> DeleteAsync(
        Guid commentId,
        Guid actorId,
        bool isAdmin,
        CancellationToken ct)
    {
        var (comment, _) = await LoadCommentForMemberAsync(commentId, actorId, ct);
        if (comment.AuthorId != actorId && !isAdmin)
        {
            throw Forbidden("只有作者或管理员可以删除评论");
        }

        comment.Status = CommentStatus.Deleted;
        auditService.Add(actorId, CommentDeletedAction, CommentTargetType, comment.Id, new
        {
            reviewId = comment.ReviewId
        });
        await db.SaveChangesAsync(ct);
        return comment;
    }

    /// <summary>
    /// 解决或取消解决评论：作者或仓库合并人可操作，只允许 OPEN 与 RESOLVED 互转。
    /// </summary>
    /// <param name="commentId">评论主键</param>
    /// <param name="actorId">操作人主键</param>
    /// <param name="resolve">true 表示解决，false 表示取消解决</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新后的评论</returns>
    public async Task<Comment> SetResolvedAsync(
        Guid commentId,
        Guid actorId,
        bool resolve,
        CancellationToken ct)
    {
        var (comment, review) = await LoadCommentForMemberAsync(commentId, actorId, ct);
        if (!await CanResolveAsync(comment, review, actorId, ct))
        {
            throw Forbidden("只有作者或仓库合并人可以解决评论");
        }

        var expected = resolve ? CommentStatus.Open : CommentStatus.Resolved;
        if (comment.Status != expected)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "COMMENT_NOT_RESOLVABLE",
                "该评论当前状态不允许此操作");
        }

        ApplyResolvedState(comment, actorId, resolve);
        var action = resolve ? CommentResolvedAction : CommentUnresolvedAction;
        auditService.Add(actorId, action, CommentTargetType, comment.Id, new
        {
            reviewId = comment.ReviewId
        });
        await db.SaveChangesAsync(ct);
        return comment;
    }

    /// <summary>
    /// 按评论与作者查询显示名，组装响应 DTO。
    /// </summary>
    /// <param name="comment">评论</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>评论响应</returns>
    public async Task<CommentDto> BuildDtoAsync(Comment comment, CancellationToken ct)
    {
        var displayName = await db.Users.AsNoTracking()
            .Where(m => m.Id == comment.AuthorId)
            .Select(m => m.DisplayName)
            .FirstOrDefaultAsync(ct);

        return ToDto(comment, displayName ?? string.Empty);
    }

    /// <summary>
    /// 读取评论并校验当前用户对所属仓库的访问权；非成员按评论不存在处理。
    /// </summary>
    /// <param name="commentId">评论主键</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>评论与所属评审单</returns>
    private async Task<(Comment Comment, Review Review)> LoadCommentForMemberAsync(
        Guid commentId,
        Guid userId,
        CancellationToken ct)
    {
        var comment = await db.Comments.FirstOrDefaultAsync(m => m.Id == commentId, ct)
            ?? throw CommentNotFound();
        var review = await ReviewAccess.LoadForMemberAsync(db, comment.ReviewId, userId, ct);
        return (comment, review);
    }

    /// <summary>
    /// 读取评审单当前版本。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>当前版本</returns>
    private async Task<ReviewRevision> LoadCurrentRevisionAsync(Review review, CancellationToken ct)
    {
        if (review.CurrentRevisionId is null)
        {
            throw ReviewAccess.NotFound();
        }

        return await db.ReviewRevisions.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == review.CurrentRevisionId.Value, ct)
            ?? throw ReviewAccess.NotFound();
    }

    /// <summary>
    /// 校验父评论存在、属于同一评审单且未删除。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="parentId">父评论主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>父评论，未提供时返回 null</returns>
    private async Task<Comment?> LoadParentAsync(
        Guid reviewId,
        Guid? parentId,
        CancellationToken ct)
    {
        if (parentId is null)
        {
            return null;
        }

        var parent = await db.Comments.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == parentId.Value, ct);

        if (parent is null || parent.ReviewId != reviewId || parent.Status == CommentStatus.Deleted)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_PARENT",
                "回复目标不存在、不属于当前评审单或已删除");
        }

        return parent;
    }

    /// <summary>
    /// 解析评论所在侧，只接受 OLD 与 NEW，忽略大小写。
    /// </summary>
    /// <param name="side">原始 side</param>
    /// <returns>评论所在侧</returns>
    private static CommentSide ParseSide(string? side)
    {
        return side?.Trim().ToUpperInvariant() switch
        {
            "OLD" => CommentSide.Old,
            "NEW" => CommentSide.New,
            _ => throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_SIDE",
                "side 只支持 OLD 或 NEW")
        };
    }

    /// <summary>
    /// 超大或二进制文件不允许行级评论。
    /// </summary>
    /// <param name="file">文件 diff</param>
    private static void EnsureCommentable(DiffFile file)
    {
        if (file.IsTooLarge || file.IsBinary)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "FILE_NOT_COMMENTABLE",
                "该文件过大或为二进制，不支持行级评论");
        }
    }

    /// <summary>
    /// 按侧与行号在 diff 中查找可评论的行：NEW 侧接受上下文与新增行，OLD 侧接受上下文与删除行。
    /// </summary>
    /// <param name="file">文件 diff</param>
    /// <param name="side">评论所在侧</param>
    /// <param name="line">行号</param>
    /// <returns>命中的 diff 行，未命中返回 null</returns>
    private static DiffLine? FindLine(DiffFile file, CommentSide side, int line)
    {
        var lines = file.Hunks.SelectMany(m => m.Lines);

        return side == CommentSide.New
            ? lines.FirstOrDefault(m => m.NewLine == line
                && m.Type is DiffLineType.Context or DiffLineType.Add)
            : lines.FirstOrDefault(m => m.OldLine == line
                && m.Type is DiffLineType.Context or DiffLineType.Delete);
    }

    /// <summary>
    /// 解析评论锚点对应的 blob id：NEW 取评审提交，OLD 取合并基，重命名时使用旧路径。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="revision">当前版本</param>
    /// <param name="file">文件 diff</param>
    /// <param name="side">评论所在侧</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>blob id</returns>
    private async Task<string> ResolveBlobShaAsync(
        Review review,
        ReviewRevision revision,
        DiffFile file,
        CommentSide side,
        CancellationToken ct)
    {
        if (side == CommentSide.New)
        {
            return await diffService.GetBlobShaAsync(
                review.RepoId, revision.HeadSha, file.Path, ct);
        }

        var commitSha = await mirrorService.GetMergeBaseAsync(
            review.RepoId, revision.BaseSha, revision.HeadSha, ct) ?? revision.BaseSha;
        return await diffService.GetBlobShaAsync(
            review.RepoId, commitSha, file.OldPath ?? file.Path, ct);
    }

    /// <summary>
    /// 判断当前用户能否解决评论：作者本人或仓库合并人。
    /// </summary>
    /// <param name="comment">评论</param>
    /// <param name="review">评审单</param>
    /// <param name="actorId">操作人主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否允许</returns>
    private async Task<bool> CanResolveAsync(
        Comment comment,
        Review review,
        Guid actorId,
        CancellationToken ct)
    {
        if (comment.AuthorId == actorId)
        {
            return true;
        }

        return await db.RepoMembers.AsNoTracking()
            .AnyAsync(m => m.RepoId == review.RepoId && m.UserId == actorId && m.IsMerger, ct);
    }

    /// <summary>
    /// 写入或清空解决人与解决时间。
    /// </summary>
    /// <param name="comment">评论</param>
    /// <param name="actorId">操作人主键</param>
    /// <param name="resolve">true 表示解决</param>
    private static void ApplyResolvedState(Comment comment, Guid actorId, bool resolve)
    {
        comment.Status = resolve ? CommentStatus.Resolved : CommentStatus.Open;
        comment.ResolvedBy = resolve ? actorId : null;
        comment.ResolvedAt = resolve ? DateTime.UtcNow : null;
    }

    /// <summary>
    /// 落库新评论与创建审计，并返回是否需要发送回复通知。
    /// </summary>
    /// <param name="comment">新评论</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="authorId">作者主键</param>
    /// <param name="side">评论所在侧</param>
    /// <param name="line">行号</param>
    /// <param name="parent">父评论，可空</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否需要通知被回复人</returns>
    private async Task<bool> PersistCreatedAsync(
        Comment comment,
        Guid reviewId,
        Guid authorId,
        CommentSide side,
        int line,
        Comment? parent,
        CancellationToken ct)
    {
        db.Comments.Add(comment);
        auditService.Add(authorId, CommentCreatedAction, CommentTargetType, comment.Id, new
        {
            reviewId,
            path = comment.FilePath,
            side = ToSideValue(side),
            line,
            parentId = parent?.Id
        });
        await db.SaveChangesAsync(ct);

        return parent is not null && parent.AuthorId != authorId;
    }

    /// <summary>
    /// 回复他人评论时通知被回复人，payload 只带渲染所需的最小字段。
    /// </summary>
    /// <param name="comment">新回复</param>
    /// <param name="recipientId">被回复人主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task NotifyReplyAsync(Comment comment, Guid recipientId, CancellationToken ct)
    {
        var authorDisplayName = await db.Users.AsNoTracking()
            .Where(m => m.Id == comment.AuthorId)
            .Select(m => m.DisplayName)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var payload = new
        {
            reviewId = comment.ReviewId,
            path = comment.FilePath,
            line = comment.LineNumber,
            bodyExcerpt = Excerpt(comment.Body),
            authorDisplayName
        };

        await notificationService.CreateAsync(
            recipientId, NotificationType.CommentReply, comment.ReviewId, payload, ct);
    }

    /// <summary>
    /// 归一化评论正文：去首尾空白后必须为 1 到 5000 个字符。
    /// </summary>
    /// <param name="body">原始正文</param>
    /// <returns>归一化后的正文</returns>
    private static string NormalizeBody(string? body)
    {
        var trimmed = body?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxBodyLength)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_BODY",
                $"评论内容不能为空且不超过 {MaxBodyLength} 个字符");
        }

        return trimmed;
    }

    /// <summary>
    /// 截取正文摘要，用于通知 payload。
    /// </summary>
    /// <param name="body">正文</param>
    /// <returns>不超过 200 个字符的摘要</returns>
    private static string Excerpt(string body)
    {
        return body.Length <= BodyExcerptLength ? body : body[..BodyExcerptLength];
    }

    /// <summary>
    /// 把评论实体映射为响应 DTO。
    /// </summary>
    /// <param name="comment">评论</param>
    /// <param name="authorDisplayName">作者显示名</param>
    /// <returns>评论响应</returns>
    private static CommentDto ToDto(Comment comment, string authorDisplayName)
    {
        return new CommentDto(
            comment.Id,
            comment.RevisionId,
            comment.AuthorId,
            authorDisplayName,
            comment.FilePath,
            comment.Side,
            comment.LineNumber,
            comment.Body,
            comment.ParentId,
            comment.Status,
            comment.ResolvedBy,
            comment.ResolvedAt,
            comment.CreatedAt,
            comment.UpdatedAt);
    }

    /// <summary>
    /// 把评论侧转换为落库字符串。
    /// </summary>
    /// <param name="side">评论所在侧</param>
    /// <returns>OLD 或 NEW</returns>
    private static string ToSideValue(CommentSide side)
    {
        return side == CommentSide.Old ? "OLD" : "NEW";
    }

    /// <summary>
    /// 构造“评论不存在”异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException CommentNotFound()
    {
        return new ApiException(StatusCodes.Status404NotFound, "NOT_FOUND", "评论不存在");
    }

    /// <summary>
    /// 构造“行不在 diff 中”异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException LineNotInDiff()
    {
        return new ApiException(
            StatusCodes.Status400BadRequest,
            "LINE_NOT_IN_DIFF",
            "该行不在当前版本的 diff 中");
    }

    /// <summary>
    /// 构造“无权操作”异常。
    /// </summary>
    /// <param name="message">错误信息</param>
    /// <returns>业务异常</returns>
    private static ApiException Forbidden(string message)
    {
        return new ApiException(StatusCodes.Status403Forbidden, "FORBIDDEN", message);
    }

    /// <summary>
    /// 评论所在侧。
    /// </summary>
    private enum CommentSide
    {
        /// <summary>
        /// 旧文件一侧。
        /// </summary>
        Old,

        /// <summary>
        /// 新文件一侧。
        /// </summary>
        New
    }
}

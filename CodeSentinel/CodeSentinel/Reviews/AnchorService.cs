using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Reviews;

/// <summary>
/// 重新锚定结果：保留、移动与失效的评论数量。
/// </summary>
/// <param name="Kept">保持不变的数量</param>
/// <param name="Moved">成功移动的数量</param>
/// <param name="Outdated">失效的数量</param>
public sealed record ReanchorReport(int Kept, int Moved, int Outdated);

/// <summary>
/// 评论重新锚定服务：新版本生成后，把未解决 NEW 侧评论映射到新 head。
/// 以评论记录的 blob id 判定其坐标所在的版本，避免把其它版本的坐标重复平移。
/// </summary>
/// <param name="db">数据库上下文</param>
/// <param name="diffService">diff 引擎，用于读取版本间 diff 与文件内容</param>
public sealed class AnchorService(AppDbContext db, DiffService diffService)
{
    private const string NewSide = "NEW";
    private const int SearchRadius = 5;
    private const int MaxScanLines = 2000;

    /// <summary>
    /// 把评审单上所有未解决 NEW 侧评论重新锚定到新版本。
    /// 评论位置在上一跳已就地更新到 fromRevision 的 head 坐标，因此逐跳累积映射即可跨多次版本迁移。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="fromRevisionId">旧版本主键</param>
    /// <param name="toRevisionId">新版本主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>重新锚定结果</returns>
    public async Task<ReanchorReport> ReanchorAsync(
        Guid reviewId,
        Guid fromRevisionId,
        Guid toRevisionId,
        CancellationToken ct)
    {
        var review = await db.Reviews.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == reviewId, ct);
        if (review is null)
        {
            return new ReanchorReport(0, 0, 0);
        }

        var heads = await LoadHeadsAsync(reviewId, fromRevisionId, toRevisionId, ct);
        if (heads is null)
        {
            return new ReanchorReport(0, 0, 0);
        }

        // 不按 revision_id == fromRevision 过滤：评论可能由更早版本逐跳迁移而来。
        // 但目标版本上刚创建的评论已经是新坐标，必须排除，避免被重复映射。
        var comments = await LoadOpenCommentsAsync(reviewId, toRevisionId, ct);
        if (comments.Count == 0)
        {
            return new ReanchorReport(0, 0, 0);
        }

        var changes = await diffService.GetChangesAsync(
            review.RepoId, heads.Value.FromHead, heads.Value.ToHead, ct);
        var session = new ReanchorSession(
            review.RepoId, heads.Value.FromHead, heads.Value.ToHead, changes);
        var snapshots = comments.ToDictionary(
            m => m.Id,
            m => new CommentSnapshot(m.Status, m.FilePath, m.LineNumber, m.BlobSha));

        try
        {
            var report = await ApplyAsync(session, comments, ct);
            await db.SaveChangesAsync(ct);
            return report;
        }
        catch (Exception)
        {
            // 失败时还原跟踪实体，避免同一 scope 的后续保存写入部分重定位结果
            RestoreSnapshots(comments, snapshots);
            throw;
        }
    }

    /// <summary>
    /// 依次处理评论并汇总保留、移动与失效数量。
    /// </summary>
    /// <param name="session">本次重新锚定的会话状态</param>
    /// <param name="comments">待处理评论</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>重新锚定结果</returns>
    private async Task<ReanchorReport> ApplyAsync(
        ReanchorSession session,
        IReadOnlyList<Comment> comments,
        CancellationToken ct)
    {
        var kept = 0;
        var moved = 0;
        var outdated = 0;

        foreach (var comment in comments)
        {
            var outcome = await ReanchorOneAsync(session, comment, ct);

            if (outcome == ReanchorOutcome.Kept)
            {
                kept++;
            }
            else if (outcome == ReanchorOutcome.Moved)
            {
                moved++;
            }
            else
            {
                outdated++;
            }
        }

        return new ReanchorReport(kept, moved, outdated);
    }

    /// <summary>
    /// 读取新旧版本的 head；任一版本不属于该评审单时返回 null。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="fromRevisionId">旧版本主键</param>
    /// <param name="toRevisionId">新版本主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>新旧 head，数据不完整时为 null</returns>
    private async Task<(string FromHead, string ToHead)?> LoadHeadsAsync(
        Guid reviewId,
        Guid fromRevisionId,
        Guid toRevisionId,
        CancellationToken ct)
    {
        var revisions = await db.ReviewRevisions.AsNoTracking()
            .Where(m => m.ReviewId == reviewId
                && (m.Id == fromRevisionId || m.Id == toRevisionId))
            .Select(m => new { m.Id, m.HeadSha })
            .ToListAsync(ct);

        var fromHead = revisions.FirstOrDefault(m => m.Id == fromRevisionId)?.HeadSha;
        var toHead = revisions.FirstOrDefault(m => m.Id == toRevisionId)?.HeadSha;

        return fromHead is null || toHead is null ? null : (fromHead, toHead);
    }

    /// <summary>
    /// 加载需要参与重定位的评论：全部未解决 NEW 侧评论，排除目标版本新建的评论。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="toRevisionId">新版本主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>参与重定位的评论</returns>
    private async Task<List<Comment>> LoadOpenCommentsAsync(
        Guid reviewId,
        Guid toRevisionId,
        CancellationToken ct)
    {
        return await db.Comments
            .Where(m => m.ReviewId == reviewId
                && m.Status == CommentStatus.Open
                && m.Side == NewSide
                && m.RevisionId != toRevisionId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 处理单条评论：按 blob id 判定坐标版本，再决定保留、映射或失效。
    /// </summary>
    /// <param name="session">本次重新锚定的会话状态</param>
    /// <param name="comment">待处理评论</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>单条评论的处理结果</returns>
    private async Task<ReanchorOutcome> ReanchorOneAsync(
        ReanchorSession session,
        Comment comment,
        CancellationToken ct)
    {
        var change = FindChange(session.Changes, comment.FilePath);
        if (change?.Status == FileChangeStatus.Deleted)
        {
            return Outdated(comment);
        }

        var newPath = change?.Path ?? comment.FilePath;
        var newBlobSha = await GetBlobShaAsync(session, session.ToHead, newPath, ct);
        if (newBlobSha is null)
        {
            return Outdated(comment);
        }

        // 评论已在新 head 坐标：只同步重命名后的路径，行号保持不动
        if (string.Equals(comment.BlobSha, newBlobSha, StringComparison.Ordinal))
        {
            ApplyPosition(comment, newPath, comment.LineNumber, newBlobSha);
            return ReanchorOutcome.Kept;
        }

        var fromBlobSha = await GetBlobShaAsync(session, session.FromHead, comment.FilePath, ct);
        if (!string.Equals(comment.BlobSha, fromBlobSha, StringComparison.Ordinal))
        {
            // 坐标来自更早版本且未跟随迁移，不能盲目按本跳差值平移
            return Outdated(comment);
        }

        if (change is null)
        {
            return Outdated(comment);
        }

        return await VerifyPositionAsync(session, change, newBlobSha, comment, ct);
    }

    /// <summary>
    /// 在映射后的位置校验锚点原文，必要时在 ±5 行内搜索，更新位置或标记失效。
    /// </summary>
    /// <param name="session">本次重新锚定的会话状态</param>
    /// <param name="change">命中的文件变更</param>
    /// <param name="newBlobSha">新版本 blob id</param>
    /// <param name="comment">待处理评论</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>单条评论的处理结果</returns>
    private async Task<ReanchorOutcome> VerifyPositionAsync(
        ReanchorSession session,
        DiffFile change,
        string newBlobSha,
        Comment comment,
        CancellationToken ct)
    {
        var mapped = LineMapper.Map(change, NewSide, comment.LineNumber);
        if (mapped is null)
        {
            return Outdated(comment);
        }

        var blob = await LoadBlobAsync(session, change.Path, ct);
        if (blob is null)
        {
            return Outdated(comment);
        }

        var lines = SplitLines(blob.Content);
        if (mapped.Value < 1 || mapped.Value > lines.Count)
        {
            return Outdated(comment);
        }

        if (string.Equals(lines[mapped.Value - 1], comment.AnchorText, StringComparison.Ordinal))
        {
            ApplyPosition(comment, change.Path, mapped.Value, newBlobSha);
            return ReanchorOutcome.Kept;
        }

        var nearby = FindNearby(lines, mapped.Value - 1, comment.AnchorText);
        if (nearby is null)
        {
            return Outdated(comment);
        }

        ApplyPosition(comment, change.Path, nearby.Value + 1, newBlobSha);
        return ReanchorOutcome.Moved;
    }

    /// <summary>
    /// 在文件变更列表中按当前路径或重命名前的路径查找文件。
    /// </summary>
    /// <param name="changes">版本间全部文件变更</param>
    /// <param name="path">评论记录的文件路径</param>
    /// <returns>命中的文件变更，未变化时为 null</returns>
    private static DiffFile? FindChange(IReadOnlyList<DiffFile> changes, string path)
    {
        return changes.FirstOrDefault(m =>
            string.Equals(m.Path, path, StringComparison.Ordinal)
            || string.Equals(m.OldPath, path, StringComparison.Ordinal));
    }

    /// <summary>
    /// 读取指定提交下文件的 blob id，按提交与路径缓存，文件不存在时返回 null。
    /// </summary>
    /// <param name="session">本次重新锚定的会话状态</param>
    /// <param name="commitSha">提交 SHA</param>
    /// <param name="path">文件路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>blob id 或 null</returns>
    private async Task<string?> GetBlobShaAsync(
        ReanchorSession session,
        string commitSha,
        string path,
        CancellationToken ct)
    {
        var key = (commitSha, path);
        if (session.BlobShas.TryGetValue(key, out var cached))
        {
            return cached;
        }

        string? sha;
        try
        {
            sha = await diffService.GetBlobShaAsync(session.RepoId, commitSha, path, ct);
        }
        catch (ApiException)
        {
            sha = null;
        }

        session.BlobShas[key] = sha;
        return sha;
    }

    /// <summary>
    /// 读取新版本文件内容；超大或二进制文件不参与匹配，返回 null。
    /// </summary>
    /// <param name="session">本次重新锚定的会话状态</param>
    /// <param name="path">文件路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>可匹配的文件内容，不可评论时为 null</returns>
    private async Task<BlobContent?> LoadBlobAsync(
        ReanchorSession session,
        string path,
        CancellationToken ct)
    {
        if (session.Blobs.TryGetValue(path, out var cached))
        {
            return IsCommentable(cached) ? cached : null;
        }

        BlobContent blob;
        try
        {
            blob = await diffService.GetBlobAsync(session.RepoId, session.ToHead, path, ct);
        }
        catch (ApiException)
        {
            return null;
        }

        session.Blobs[path] = blob;
        return IsCommentable(blob) ? blob : null;
    }

    /// <summary>
    /// 判断文件内容是否可用于锚点匹配。
    /// </summary>
    /// <param name="blob">文件内容</param>
    /// <returns>是否可用于匹配</returns>
    private static bool IsCommentable(BlobContent blob)
    {
        return !blob.IsBinary && !blob.IsTooLarge;
    }

    /// <summary>
    /// 在映射位置 ±5 行内查找与锚点原文完全一致的行；大文件只检查映射行。
    /// </summary>
    /// <param name="lines">新版本文件各行内容</param>
    /// <param name="index">映射行的零基下标</param>
    /// <param name="anchorText">锚点原文</param>
    /// <returns>命中的零基下标，未命中返回 null</returns>
    private static int? FindNearby(
        IReadOnlyList<string> lines,
        int index,
        string anchorText)
    {
        if (lines.Count > MaxScanLines)
        {
            return null;
        }

        var start = Math.Max(0, index - SearchRadius);
        var end = Math.Min(lines.Count - 1, index + SearchRadius);
        for (var i = start; i <= end; i++)
        {
            if (i != index && string.Equals(lines[i], anchorText, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// 把文件内容按行拆分，去掉行尾 CR 与末尾换行产生的空行。
    /// </summary>
    /// <param name="content">文件文本内容</param>
    /// <returns>行列表</returns>
    private static IReadOnlyList<string> SplitLines(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var raw = content.Split('\n');
        var count = raw.Length;
        if (raw[^1].Length == 0)
        {
            count--;
        }

        var lines = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var line = raw[i];
            lines.Add(line.EndsWith('\r') ? line[..^1] : line);
        }

        return lines;
    }

    /// <summary>
    /// 更新评论的文件路径、行号与 blob id；不重写创建时所属版本。
    /// </summary>
    /// <param name="comment">评论</param>
    /// <param name="path">新版本中的文件路径</param>
    /// <param name="line">新版本中的行号</param>
    /// <param name="blobSha">新版本文件的 blob id</param>
    private static void ApplyPosition(Comment comment, string path, int line, string blobSha)
    {
        comment.FilePath = path;
        comment.LineNumber = line;
        comment.BlobSha = blobSha;
    }

    /// <summary>
    /// 还原评论的既有字段并清除跟踪状态，避免失败后残留部分修改。
    /// </summary>
    /// <param name="comments">本次处理的评论</param>
    /// <param name="snapshots">处理前的字段快照</param>
    private void RestoreSnapshots(
        IReadOnlyList<Comment> comments,
        Dictionary<Guid, CommentSnapshot> snapshots)
    {
        foreach (var comment in comments)
        {
            if (!snapshots.TryGetValue(comment.Id, out var snapshot))
            {
                continue;
            }

            comment.Status = snapshot.Status;
            comment.FilePath = snapshot.FilePath;
            comment.LineNumber = snapshot.LineNumber;
            comment.BlobSha = snapshot.BlobSha;
            db.Entry(comment).State = EntityState.Unchanged;
        }
    }

    /// <summary>
    /// 把评论标记为失效。
    /// </summary>
    /// <param name="comment">评论</param>
    /// <returns>失效结果</returns>
    private static ReanchorOutcome Outdated(Comment comment)
    {
        comment.Status = CommentStatus.Outdated;
        return ReanchorOutcome.Outdated;
    }

    /// <summary>
    /// 一次重新锚定中复用的上下文：仓库、新旧 head、文件变更与按文件缓存的 blob 数据。
    /// </summary>
    /// <param name="RepoId">仓库主键</param>
    /// <param name="FromHead">旧版本 head</param>
    /// <param name="ToHead">新版本 head</param>
    /// <param name="Changes">版本间全部文件变更</param>
    private sealed class ReanchorSession(
        Guid RepoId,
        string FromHead,
        string ToHead,
        IReadOnlyList<DiffFile> Changes)
    {
        /// <summary>
        /// 仓库主键。
        /// </summary>
        public Guid RepoId { get; } = RepoId;

        /// <summary>
        /// 旧版本 head。
        /// </summary>
        public string FromHead { get; } = FromHead;

        /// <summary>
        /// 新版本 head。
        /// </summary>
        public string ToHead { get; } = ToHead;

        /// <summary>
        /// 版本间全部文件变更。
        /// </summary>
        public IReadOnlyList<DiffFile> Changes { get; } = Changes;

        /// <summary>
        /// 按（提交, 路径）缓存的 blob id，缺失的文件也缓存为 null。
        /// </summary>
        public Dictionary<(string CommitSha, string Path), string?> BlobShas { get; } = new();

        /// <summary>
        /// 按路径缓存的新版本文件内容。
        /// </summary>
        public Dictionary<string, BlobContent> Blobs { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 评论处理前的字段快照，用于失败回滚。
    /// </summary>
    /// <param name="Status">评论状态</param>
    /// <param name="FilePath">文件路径</param>
    /// <param name="LineNumber">行号</param>
    /// <param name="BlobSha">blob id</param>
    private sealed record CommentSnapshot(
        CommentStatus Status,
        string FilePath,
        int LineNumber,
        string BlobSha);

    /// <summary>
    /// 单条评论的重新锚定结果。
    /// </summary>
    private enum ReanchorOutcome
    {
        /// <summary>
        /// 位置保持不变。
        /// </summary>
        Kept,

        /// <summary>
        /// 在附近找到了锚点原文，位置已更新。
        /// </summary>
        Moved,

        /// <summary>
        /// 无法定位，评论已失效。
        /// </summary>
        Outdated
    }
}

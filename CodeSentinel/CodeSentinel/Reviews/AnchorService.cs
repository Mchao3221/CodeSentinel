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
/// 评论重新锚定服务：新版本生成后，把旧版本上的 NEW 侧评论映射到新 head。
/// </summary>
/// <param name="db">数据库上下文</param>
/// <param name="diffService">diff 引擎，用于读取版本间 diff 与文件内容</param>
public sealed class AnchorService(AppDbContext db, DiffService diffService)
{
    private const string NewSide = "NEW";
    private const int SearchRadius = 5;
    private const int MaxScanLines = 2000;

    /// <summary>
    /// 把旧版本上的评论重新锚定到新版本。
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

        var comments = await db.Comments
            .Where(m => m.ReviewId == reviewId
                && m.RevisionId == fromRevisionId
                && m.Status == CommentStatus.Open
                && m.Side == NewSide)
            .ToListAsync(ct);
        if (comments.Count == 0)
        {
            return new ReanchorReport(0, 0, 0);
        }

        var changes = await diffService.GetChangesAsync(
            review.RepoId, heads.Value.FromHead, heads.Value.ToHead, ct);
        var blobs = new Dictionary<string, BlobContent>(StringComparer.Ordinal);
        var kept = 0;
        var moved = 0;
        var outdated = 0;

        foreach (var comment in comments)
        {
            var outcome = await ReanchorOneAsync(
                review.RepoId, heads.Value, changes, comment, blobs, ct);

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

        await db.SaveChangesAsync(ct);
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
    /// 处理单条评论：未变保留、可映射则更新位置、无法定位则标记失效。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="heads">新旧版本的 head</param>
    /// <param name="changes">版本间全部文件变更</param>
    /// <param name="comment">待处理评论</param>
    /// <param name="blobs">新版本文件内容缓存</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>单条评论的处理结果</returns>
    private async Task<ReanchorOutcome> ReanchorOneAsync(
        Guid repoId,
        (string FromHead, string ToHead) heads,
        IReadOnlyList<DiffFile> changes,
        Comment comment,
        Dictionary<string, BlobContent> blobs,
        CancellationToken ct)
    {
        var change = FindChange(changes, comment.FilePath);
        var probe = await ProbeBlobAsync(repoId, heads, change, comment, ct);
        if (probe.Final is not null)
        {
            return probe.Final.Value;
        }

        // ProbeBlobAsync 只在文件发生变更且内容不同时返回 null，此处必有变更
        if (change is null)
        {
            return Outdated(comment);
        }

        return await VerifyPositionAsync(
            repoId, heads.ToHead, change, probe.NewBlobSha, comment, blobs, ct);
    }

    /// <summary>
    /// 按文件删除与 blob id 判定评论能否直接保留；需要继续映射时 Final 为 null。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="heads">新旧版本的 head</param>
    /// <param name="change">命中的文件变更，未变化时为 null</param>
    /// <param name="comment">待处理评论</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>最终结果与新版本 blob id，需继续映射时 Final 为 null</returns>
    private async Task<(ReanchorOutcome? Final, string NewBlobSha)> ProbeBlobAsync(
        Guid repoId,
        (string FromHead, string ToHead) heads,
        DiffFile? change,
        Comment comment,
        CancellationToken ct)
    {
        var newPath = change?.Path ?? comment.FilePath;
        if (change?.Status == FileChangeStatus.Deleted)
        {
            return (Outdated(comment), string.Empty);
        }

        var newBlobSha = await TryGetBlobShaAsync(repoId, heads.ToHead, newPath, ct);
        if (newBlobSha is null)
        {
            return (Outdated(comment), string.Empty);
        }

        // 内容未变化（含纯重命名）：保留位置，只同步路径与 blob id
        var oldBlobSha = await TryGetBlobShaAsync(repoId, heads.FromHead, comment.FilePath, ct);
        if (string.Equals(oldBlobSha, newBlobSha, StringComparison.Ordinal))
        {
            ApplyPosition(comment, newPath, comment.LineNumber, newBlobSha);
            return (ReanchorOutcome.Kept, newBlobSha);
        }

        return (change is null ? ReanchorOutcome.Outdated : null, newBlobSha);
    }

    /// <summary>
    /// 在映射后的位置校验锚点原文，必要时在 ±5 行内搜索，更新位置或标记失效。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="toHead">新版本 head</param>
    /// <param name="change">命中的文件变更</param>
    /// <param name="newBlobSha">新版本 blob id</param>
    /// <param name="comment">待处理评论</param>
    /// <param name="blobs">新版本文件内容缓存</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>单条评论的处理结果</returns>
    private async Task<ReanchorOutcome> VerifyPositionAsync(
        Guid repoId,
        string toHead,
        DiffFile change,
        string newBlobSha,
        Comment comment,
        Dictionary<string, BlobContent> blobs,
        CancellationToken ct)
    {
        var mapped = LineMapper.Map(change, NewSide, comment.LineNumber);
        if (mapped is null)
        {
            return Outdated(comment);
        }

        var blob = await LoadBlobAsync(repoId, toHead, change.Path, blobs, ct);
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
    /// 读取 blob id；文件不存在时返回 null，供失效判定使用。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="commitSha">提交 SHA</param>
    /// <param name="path">文件路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>blob id 或 null</returns>
    private async Task<string?> TryGetBlobShaAsync(
        Guid repoId,
        string commitSha,
        string path,
        CancellationToken ct)
    {
        try
        {
            return await diffService.GetBlobShaAsync(repoId, commitSha, path, ct);
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// 读取新版本文件内容；超大或二进制文件不参与匹配，返回 null。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="commitSha">提交 SHA</param>
    /// <param name="path">文件路径</param>
    /// <param name="cache">按路径缓存的文件内容</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>可匹配的文件内容，不可评论时为 null</returns>
    private async Task<BlobContent?> LoadBlobAsync(
        Guid repoId,
        string commitSha,
        string path,
        Dictionary<string, BlobContent> cache,
        CancellationToken ct)
    {
        if (cache.TryGetValue(path, out var cached))
        {
            return IsCommentable(cached) ? cached : null;
        }

        BlobContent blob;
        try
        {
            blob = await diffService.GetBlobAsync(repoId, commitSha, path, ct);
        }
        catch (ApiException)
        {
            return null;
        }

        cache[path] = blob;
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

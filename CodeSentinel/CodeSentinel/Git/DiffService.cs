using System.Globalization;
using System.Text;
using CodeSentinel.Common;
using CodeSentinel.Data.Entities;
using Microsoft.AspNetCore.Http;

namespace CodeSentinel.Git;

/// <summary>
/// diff 引擎：文件列表、单文件 diff 与全文读取，带缓存与大小限额。
/// </summary>
public sealed class DiffService
{
    /// <summary>
    /// 单文件 diff 输出大小上限（2 MB）。
    /// </summary>
    private const int MaxDiffSizeBytes = 2 * 1024 * 1024;

    /// <summary>
    /// blob 全文大小上限（2 MB）。
    /// </summary>
    private const int MaxBlobSizeBytes = 2 * 1024 * 1024;

    private readonly GitCli _git;
    private readonly MirrorService _mirror;
    private readonly DiffCache _cache;

    /// <summary>
    /// 初始化 diff 引擎。
    /// </summary>
    /// <param name="git">git 命令封装</param>
    /// <param name="mirror">镜像服务</param>
    /// <param name="cache">diff 缓存</param>
    public DiffService(GitCli git, MirrorService mirror, DiffCache cache)
    {
        _git = git;
        _mirror = mirror;
        _cache = cache;
    }

    /// <summary>
    /// 读取版本的文件列表，优先命中缓存。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="baseSha">基线提交</param>
    /// <param name="headSha">评审提交</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>文件摘要列表，按路径排序</returns>
    public async Task<IReadOnlyList<DiffFileSummary>> GetFilesAsync(
        Guid repoId,
        string baseSha,
        string headSha,
        CancellationToken ct)
    {
        var cached = await _cache.TryGetIndexAsync(repoId, baseSha, headSha, ct);
        if (cached is not null)
        {
            return cached;
        }

        var mirrorPath = _mirror.GetMirrorPath(repoId);
        var range = $"{baseSha}...{headSha}";
        var nameStatus = await _git.RunAsync(
            mirrorPath, ["diff", "--name-status", "-z", "--find-renames=50%", range], null, ct);
        EnsureSucceeded(nameStatus.ExitCode, nameStatus.StdErr, "读取 diff 文件列表失败");

        var numstat = await _git.RunAsync(
            mirrorPath, ["diff", "--numstat", "-z", "--find-renames=50%", range], null, ct);
        EnsureSucceeded(numstat.ExitCode, numstat.StdErr, "读取 diff 统计失败");

        var files = MergeFileList(ParseNameStatus(nameStatus.StdOut), ParseNumstat(numstat.StdOut));
        await _cache.WriteIndexAsync(repoId, baseSha, headSha, files, ct);
        return files;
    }

    /// <summary>
    /// 读取单个文件的 diff，优先命中缓存；二进制或超限时返回空 hunk。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="baseSha">基线提交</param>
    /// <param name="headSha">评审提交</param>
    /// <param name="path">文件路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>文件 diff</returns>
    public async Task<DiffFile> GetFileDiffAsync(
        Guid repoId,
        string baseSha,
        string headSha,
        string path,
        CancellationToken ct)
    {
        var summary = await FindSummaryAsync(repoId, baseSha, headSha, path, ct);

        var cached = await _cache.TryGetFileAsync(repoId, baseSha, headSha, path, ct);
        if (cached is not null)
        {
            return cached;
        }

        var mirrorPath = _mirror.GetMirrorPath(repoId);
        var diffArgs = new List<string>
        {
            "diff", "--no-color", "-U3", "--find-renames=50%", $"{baseSha}...{headSha}", "--"
        };
        if (summary.OldPath is not null)
        {
            // 重命名：同时传旧、新两个路径，恢复 git 的重命名检测，避免把新路径当整文件新增
            diffArgs.Add(summary.OldPath);
        }

        diffArgs.Add(summary.Path);

        var result = await _git.RunRawAsync(mirrorPath, diffArgs, null, ct);
        EnsureSucceeded(result.ExitCode, result.StdErr, "读取单文件 diff 失败");

        var tooLarge = result.StdOut.Length > MaxDiffSizeBytes;
        if (tooLarge)
        {
            var large = new DiffFile(
                summary.Path, summary.OldPath, summary.Status,
                summary.Additions, summary.Deletions, summary.IsBinary, true, []);
            await _cache.WriteFileAsync(repoId, baseSha, headSha, path, large, ct);
            return large;
        }

        var parsed = UnifiedDiffParser.Parse(Encoding.UTF8.GetString(result.StdOut));
        var isBinary = summary.IsBinary || parsed.Any(m => m.IsBinary);
        IReadOnlyList<DiffHunk> hunks = isBinary
            ? []
            : parsed.SelectMany(m => m.Hunks).ToList();
        var file = new DiffFile(
            summary.Path, summary.OldPath, summary.Status,
            summary.Additions, summary.Deletions, isBinary, false, hunks);
        await _cache.WriteFileAsync(repoId, baseSha, headSha, path, file, ct);
        return file;
    }

    /// <summary>
    /// 读取某个提交下文件全文；超过 2 MB 或含 NUL 字节时标记并清空内容。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="commitSha">提交 SHA</param>
    /// <param name="path">文件路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>文件全文</returns>
    public async Task<BlobContent> GetBlobAsync(
        Guid repoId,
        string commitSha,
        string path,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var mirrorPath = _mirror.GetMirrorPath(repoId);
        var result = await _git.RunRawAsync(
            mirrorPath, ["show", $"{commitSha}:{path}"], null, ct);

        if (result.ExitCode != 0)
        {
            throw FileNotInDiff();
        }

        if (result.StdOut.Length > MaxBlobSizeBytes)
        {
            return new BlobContent(path, string.Empty, false, true);
        }

        if (ContainsNulByte(result.StdOut))
        {
            return new BlobContent(path, string.Empty, true, false);
        }

        return new BlobContent(path, Encoding.UTF8.GetString(result.StdOut), false, false);
    }

    /// <summary>
    /// 读取某个提交下文件内容的 blob id，用于评论锚点；文件不存在时返回 404 FILE_NOT_IN_DIFF。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="commitSha">提交 SHA</param>
    /// <param name="path">文件路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>blob id</returns>
    public async Task<string> GetBlobShaAsync(
        Guid repoId,
        string commitSha,
        string path,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var mirrorPath = _mirror.GetMirrorPath(repoId);
        var result = await _git.RunAsync(
            mirrorPath, ["rev-parse", "--verify", "--quiet", $"{commitSha}:{path}"], null, ct);
        var sha = result.StdOut.Trim();

        if (result.ExitCode != 0 || sha.Length == 0)
        {
            throw FileNotInDiff();
        }

        return sha;
    }

    /// <summary>
    /// 校验路径属于该版本 diff 的文件列表，否则返回 404 FILE_NOT_IN_DIFF。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="baseSha">基线提交</param>
    /// <param name="headSha">评审提交</param>
    /// <param name="path">文件路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task EnsurePathInDiffAsync(
        Guid repoId,
        string baseSha,
        string headSha,
        string path,
        CancellationToken ct)
    {
        await FindSummaryAsync(repoId, baseSha, headSha, path, ct);
    }

    /// <summary>
    /// 在文件列表中查找指定路径（匹配当前路径或重命名前的路径），未命中抛 404。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="baseSha">基线提交</param>
    /// <param name="headSha">评审提交</param>
    /// <param name="path">文件路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>文件摘要</returns>
    private async Task<DiffFileSummary> FindSummaryAsync(
        Guid repoId,
        string baseSha,
        string headSha,
        string path,
        CancellationToken ct)
    {
        var files = await GetFilesAsync(repoId, baseSha, headSha, ct);
        return files.FirstOrDefault(m =>
            string.Equals(m.Path, path, StringComparison.Ordinal)
            || string.Equals(m.OldPath, path, StringComparison.Ordinal))
            ?? throw FileNotInDiff();
    }

    /// <summary>
    /// 解析 --name-status -z 输出：普通记录 STATUS\0path\0，重命名 Rxxx\0old\0new\0。
    /// </summary>
    /// <param name="stdOut">git 输出</param>
    /// <returns>名称状态条目</returns>
    private static IReadOnlyList<NameStatusEntry> ParseNameStatus(string stdOut)
    {
        var entries = new List<NameStatusEntry>();
        var tokens = stdOut.Split('\0');
        var index = 0;
        while (index < tokens.Length)
        {
            var status = tokens[index];
            if (status.Length == 0)
            {
                index++;
                continue;
            }

            if (status.StartsWith('R'))
            {
                var oldPath = index + 1 < tokens.Length ? tokens[index + 1] : string.Empty;
                var newPath = index + 2 < tokens.Length ? tokens[index + 2] : string.Empty;
                entries.Add(new NameStatusEntry(FileChangeStatus.Renamed, oldPath, newPath));
                index += 3;
            }
            else
            {
                var path = index + 1 < tokens.Length ? tokens[index + 1] : string.Empty;
                entries.Add(new NameStatusEntry(ParseStatus(status), null, path));
                index += 2;
            }
        }

        return entries;
    }

    /// <summary>
    /// 把单个字符状态映射为枚举。
    /// </summary>
    /// <param name="status">状态字符</param>
    /// <returns>变更状态</returns>
    private static FileChangeStatus ParseStatus(string status)
    {
        return status switch
        {
            "A" => FileChangeStatus.Added,
            "D" => FileChangeStatus.Deleted,
            "M" => FileChangeStatus.Modified,
            _ => FileChangeStatus.Modified
        };
    }

    /// <summary>
    /// 解析 --numstat -z 输出：普通记录 added\tdeleted\tpath\0，重命名后跟 old\0new\0，-/- 表示二进制。
    /// </summary>
    /// <param name="stdOut">git 输出</param>
    /// <returns>统计条目</returns>
    private static IReadOnlyList<NumstatEntry> ParseNumstat(string stdOut)
    {
        var entries = new List<NumstatEntry>();
        var tokens = stdOut.Split('\0');
        var index = 0;
        while (index < tokens.Length)
        {
            var token = tokens[index];
            if (token.Length == 0)
            {
                index++;
                continue;
            }

            var (added, deleted, path) = SplitNumstatToken(token);
            if (path.Length == 0 && index + 2 < tokens.Length)
            {
                // 重命名记录：added\tdeleted\t\0oldPath\0newPath\0
                entries.Add(new NumstatEntry(added, deleted, tokens[index + 2], tokens[index + 1]));
                index += 3;
            }
            else
            {
                entries.Add(new NumstatEntry(added, deleted, path, null));
                index += 1;
            }
        }

        return entries;
    }

    /// <summary>
    /// 拆分 numstat 记录 token 为增删行数与路径。
    /// </summary>
    /// <param name="token">记录 token</param>
    /// <returns>新增、删除与路径</returns>
    private static (int? Added, int? Deleted, string Path) SplitNumstatToken(string token)
    {
        var firstTab = token.IndexOf('\t');
        var secondTab = firstTab >= 0 ? token.IndexOf('\t', firstTab + 1) : -1;
        if (firstTab < 0 || secondTab < 0)
        {
            return (null, null, token);
        }

        var addedText = token[..firstTab];
        var deletedText = token[(firstTab + 1)..secondTab];
        var path = token[(secondTab + 1)..];
        return (ParseCount(addedText), ParseCount(deletedText), path);
    }

    /// <summary>
    /// 解析增删行数，- 表示二进制返回 null。
    /// </summary>
    /// <param name="value">数值文本</param>
    /// <returns>行数或 null</returns>
    private static int? ParseCount(string value)
    {
        return value == "-" ? null : int.Parse(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 按路径合并名称状态与统计信息为文件摘要。
    /// </summary>
    /// <param name="names">名称状态条目</param>
    /// <param name="numstats">统计条目</param>
    /// <returns>文件摘要列表</returns>
    private static IReadOnlyList<DiffFileSummary> MergeFileList(
        IReadOnlyList<NameStatusEntry> names,
        IReadOnlyList<NumstatEntry> numstats)
    {
        var statsByPath = new Dictionary<string, NumstatEntry>(StringComparer.Ordinal);
        foreach (var entry in numstats)
        {
            statsByPath[entry.Path] = entry;
        }

        var files = new List<DiffFileSummary>();
        foreach (var name in names)
        {
            var path = name.NewPath ?? name.OldPath ?? string.Empty;
            var additions = 0;
            var deletions = 0;
            var isBinary = false;
            if (statsByPath.TryGetValue(name.NewPath ?? string.Empty, out var stat))
            {
                additions = stat.Added ?? 0;
                deletions = stat.Deleted ?? 0;
                isBinary = stat.Added is null || stat.Deleted is null;
            }

            files.Add(new DiffFileSummary(
                path, name.OldPath, name.Status, additions, deletions, isBinary, false));
        }

        return files.OrderBy(m => m.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 校验 git 命令成功，失败时抛出 500 GIT_FAILED。
    /// </summary>
    /// <param name="exitCode">退出码</param>
    /// <param name="stdErr">标准错误</param>
    /// <param name="message">错误描述</param>
    private static void EnsureSucceeded(int exitCode, string stdErr, string message)
    {
        if (exitCode != 0)
        {
            var detail = stdErr.Trim();
            throw new ApiException(
                StatusCodes.Status500InternalServerError,
                "GIT_FAILED",
                string.IsNullOrWhiteSpace(detail) ? message : $"{message}：{detail}");
        }
    }

    /// <summary>
    /// 构造“文件不在本次 diff 中”异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException FileNotInDiff()
    {
        return new ApiException(
            StatusCodes.Status404NotFound,
            "FILE_NOT_IN_DIFF",
            "文件不在本次 diff 中");
    }

    /// <summary>
    /// 判断字节数组是否包含 NUL 字节。
    /// </summary>
    /// <param name="bytes">字节数组</param>
    /// <returns>是否包含 NUL</returns>
    private static bool ContainsNulByte(byte[] bytes)
    {
        return Array.IndexOf(bytes, (byte)0) >= 0;
    }

    /// <summary>
    /// 名称状态条目。
    /// </summary>
    /// <param name="Status">变更状态</param>
    /// <param name="OldPath">旧路径，重命名时为旧名</param>
    /// <param name="NewPath">新路径</param>
    private sealed record NameStatusEntry(FileChangeStatus Status, string? OldPath, string? NewPath);

    /// <summary>
    /// 统计条目。
    /// </summary>
    /// <param name="Added">新增行数，二进制为 null</param>
    /// <param name="Deleted">删除行数，二进制为 null</param>
    /// <param name="Path">当前路径</param>
    /// <param name="OldPath">重命名时的旧路径</param>
    private sealed record NumstatEntry(int? Added, int? Deleted, string Path, string? OldPath);
}

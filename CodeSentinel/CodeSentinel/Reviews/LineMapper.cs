using CodeSentinel.Git;

namespace CodeSentinel.Reviews;

/// <summary>
/// 行号映射：把旧版本一侧的行号映射到新版本同一侧的行号。
/// </summary>
public static class LineMapper
{
    /// <summary>
    /// 把旧版本行号映射到新版本行号。
    /// </summary>
    /// <param name="change">从旧 head 到新 head 的单文件 diff</param>
    /// <param name="side">评论所在侧，OLD 或 NEW</param>
    /// <param name="oldLine">旧版本中的行号</param>
    /// <returns>新版本中的行号；行已不存在或无法映射时返回 null</returns>
    public static int? Map(DiffFile change, string side, int oldLine)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(side);

        // P31：基线提交按版本锁定，OLD 侧坐标不会随 head 前进而变化
        if (string.Equals(side, "OLD", StringComparison.OrdinalIgnoreCase))
        {
            return oldLine;
        }

        if (!string.Equals(side, "NEW", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var offset = 0;
        foreach (var hunk in change.Hunks)
        {
            if (hunk.OldLines == 0)
            {
                // 纯新增块不占用旧行，插入点及其之前的行保持原号
                if (oldLine <= hunk.OldStart)
                {
                    return oldLine + offset;
                }

                offset += hunk.NewLines;
                continue;
            }

            // 位于本块之前的行保留原号，只需补上前面块的累计行数差
            if (oldLine < hunk.OldStart)
            {
                return oldLine + offset;
            }

            // 位于本块覆盖的旧行范围内，逐行判定
            if (oldLine < hunk.OldStart + hunk.OldLines)
            {
                return MapInHunk(hunk, oldLine);
            }

            offset += hunk.NewLines - hunk.OldLines;
        }

        return oldLine + offset;
    }

    /// <summary>
    /// 在单个 hunk 内查找旧行对应的新行号：上下文行可映射，删除行返回 null。
    /// </summary>
    /// <param name="hunk">diff 块</param>
    /// <param name="oldLine">旧文件行号</param>
    /// <returns>新文件行号或 null</returns>
    private static int? MapInHunk(DiffHunk hunk, int oldLine)
    {
        foreach (var line in hunk.Lines)
        {
            if (line.OldLine != oldLine)
            {
                continue;
            }

            return line.Type == DiffLineType.Context ? line.NewLine : null;
        }

        return null;
    }
}

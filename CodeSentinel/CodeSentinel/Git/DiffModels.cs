using CodeSentinel.Data.Entities;

namespace CodeSentinel.Git;

/// <summary>
/// 文件列表中的单个文件摘要。
/// </summary>
/// <param name="Path">当前路径（重命名后为新路径）</param>
/// <param name="OldPath">重命名前的路径，非重命名时为 null</param>
/// <param name="Status">变更类型</param>
/// <param name="Additions">新增行数</param>
/// <param name="Deletions">删除行数</param>
/// <param name="IsBinary">是否为二进制文件</param>
/// <param name="IsTooLarge">是否超过单文件 diff 大小限制</param>
public sealed record DiffFileSummary(
    string Path,
    string? OldPath,
    FileChangeStatus Status,
    int Additions,
    int Deletions,
    bool IsBinary,
    bool IsTooLarge);

/// <summary>
/// 单文件 diff 详情，包含结构化 hunk 与行。
/// </summary>
/// <param name="Path">当前路径（重命名后为新路径）</param>
/// <param name="OldPath">重命名前的路径，非重命名时为 null</param>
/// <param name="Status">变更类型</param>
/// <param name="Additions">新增行数</param>
/// <param name="Deletions">删除行数</param>
/// <param name="IsBinary">是否为二进制文件</param>
/// <param name="IsTooLarge">是否超过单文件 diff 大小限制</param>
/// <param name="Hunks">diff 块，二进制或超限时为空</param>
public sealed record DiffFile(
    string Path,
    string? OldPath,
    FileChangeStatus Status,
    int Additions,
    int Deletions,
    bool IsBinary,
    bool IsTooLarge,
    IReadOnlyList<DiffHunk> Hunks);

/// <summary>
/// 一个 diff 块及其行号范围。
/// </summary>
/// <param name="OldStart">旧文件起始行号</param>
/// <param name="OldLines">旧文件覆盖行数</param>
/// <param name="NewStart">新文件起始行号</param>
/// <param name="NewLines">新文件覆盖行数</param>
/// <param name="Lines">块内的行</param>
public sealed record DiffHunk(
    int OldStart,
    int OldLines,
    int NewStart,
    int NewLines,
    IReadOnlyList<DiffLine> Lines);

/// <summary>
/// diff 中的一行。
/// </summary>
/// <param name="Type">行类型</param>
/// <param name="OldLine">旧文件行号，新增行时为 null</param>
/// <param name="NewLine">新文件行号，删除行时为 null</param>
/// <param name="Text">不带前缀的行文本</param>
public sealed record DiffLine(
    DiffLineType Type,
    int? OldLine,
    int? NewLine,
    string Text);

/// <summary>
/// diff 行的类型。
/// </summary>
public enum DiffLineType
{
    /// <summary>
    /// 上下文行。
    /// </summary>
    Context,

    /// <summary>
    /// 新增行。
    /// </summary>
    Add,

    /// <summary>
    /// 删除行。
    /// </summary>
    Delete
}

/// <summary>
/// 单个文件的全文内容。
/// </summary>
/// <param name="Path">文件路径</param>
/// <param name="Content">UTF-8 文本内容，二进制或超限时为空字符串</param>
/// <param name="IsBinary">是否包含 NUL 字节的二进制内容</param>
/// <param name="IsTooLarge">是否超过大小限制</param>
public sealed record BlobContent(
    string Path,
    string Content,
    bool IsBinary,
    bool IsTooLarge);

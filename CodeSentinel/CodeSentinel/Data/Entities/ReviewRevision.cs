using System.ComponentModel.DataAnnotations.Schema;

namespace CodeSentinel.Data.Entities;

/// <summary>
/// 评审版本，分支 B 每有新提交追加一条。
/// </summary>
public class ReviewRevision
{
    /// <summary>
    /// 主键。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 所属评审单。
    /// </summary>
    public Guid ReviewId { get; set; }

    /// <summary>
    /// 从 1 开始递增的版本号。
    /// </summary>
    public int Seq { get; set; }

    /// <summary>
    /// 本版本 diff 的基线提交。
    /// </summary>
    [Column(TypeName = "varchar(64)")]
    public string BaseSha { get; set; } = string.Empty;

    /// <summary>
    /// 本版本 diff 的终点提交。
    /// </summary>
    [Column(TypeName = "varchar(64)")]
    public string HeadSha { get; set; } = string.Empty;

    /// <summary>
    /// 创建时间（UTC）。
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 更新时间（UTC）。
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

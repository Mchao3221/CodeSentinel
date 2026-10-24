using System.ComponentModel.DataAnnotations.Schema;

namespace CodeSentinel.Data.Entities;

/// <summary>
/// 用户对某个评审文件的“已看过”标记。
/// </summary>
public class ReviewFileView : IAuditableEntity
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
    /// 用户。
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// 文件路径。
    /// </summary>
    [Column(TypeName = "varchar(1000)")]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// 勾选时该文件新侧的 blob id。
    /// </summary>
    [Column(TypeName = "varchar(64)")]
    public string BlobSha { get; set; } = string.Empty;

    /// <summary>
    /// 勾选时间（UTC）。
    /// </summary>
    public DateTime ViewedAt { get; set; }

    /// <summary>
    /// 创建时间（UTC）。
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 更新时间（UTC）。
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

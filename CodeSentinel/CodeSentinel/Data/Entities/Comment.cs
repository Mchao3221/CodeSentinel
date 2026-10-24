using System.ComponentModel.DataAnnotations.Schema;

namespace CodeSentinel.Data.Entities;

/// <summary>
/// 行级评论。
/// </summary>
public class Comment : IAuditableEntity
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
    /// 评论创建时所属的评审版本。
    /// </summary>
    public Guid RevisionId { get; set; }

    /// <summary>
    /// 评论作者。
    /// </summary>
    public Guid AuthorId { get; set; }

    /// <summary>
    /// 文件路径。
    /// </summary>
    [Column(TypeName = "varchar(1000)")]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// 评论所在侧：OLD 或 NEW。
    /// </summary>
    [Column(TypeName = "varchar(10)")]
    public string Side { get; set; } = string.Empty;

    /// <summary>
    /// 该侧文件中的行号。
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// 该行原文，用于新版本重新定位。
    /// </summary>
    [Column(TypeName = "text")]
    public string AnchorText { get; set; } = string.Empty;

    /// <summary>
    /// 该侧文件内容的 blob id。
    /// </summary>
    [Column(TypeName = "varchar(64)")]
    public string BlobSha { get; set; } = string.Empty;

    /// <summary>
    /// 评论内容（Markdown 子集）。
    /// </summary>
    [Column(TypeName = "text")]
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// 回复的目标评论。
    /// </summary>
    public Guid? ParentId { get; set; }

    /// <summary>
    /// 评论状态。
    /// </summary>
    public CommentStatus Status { get; set; }

    /// <summary>
    /// 解决人。
    /// </summary>
    public Guid? ResolvedBy { get; set; }

    /// <summary>
    /// 解决时间（UTC）。
    /// </summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// 创建时间（UTC）。
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 更新时间（UTC）。
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

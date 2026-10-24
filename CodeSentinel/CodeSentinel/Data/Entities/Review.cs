using System.ComponentModel.DataAnnotations.Schema;

namespace CodeSentinel.Data.Entities;

/// <summary>
/// 评审单。
/// </summary>
public class Review
{
    /// <summary>
    /// 主键。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 所属仓库。
    /// </summary>
    public Guid RepoId { get; set; }

    /// <summary>
    /// 目标分支 A。
    /// </summary>
    [Column(TypeName = "varchar(200)")]
    public string BaseBranch { get; set; } = string.Empty;

    /// <summary>
    /// 提交人的分支 B。
    /// </summary>
    [Column(TypeName = "varchar(200)")]
    public string HeadBranch { get; set; } = string.Empty;

    /// <summary>
    /// 提交人。
    /// </summary>
    public Guid SubmitterId { get; set; }

    /// <summary>
    /// 标题。
    /// </summary>
    [Column(TypeName = "varchar(200)")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 评审说明。
    /// </summary>
    [Column(TypeName = "text")]
    public string? Description { get; set; }

    /// <summary>
    /// 当前状态。
    /// </summary>
    public ReviewStatus Status { get; set; }

    /// <summary>
    /// 当前评审版本。
    /// </summary>
    public Guid? CurrentRevisionId { get; set; }

    /// <summary>
    /// 当前版本锁定的基线提交（A 当时的最新提交）。
    /// </summary>
    [Column(TypeName = "varchar(64)")]
    public string BaseSha { get; set; } = string.Empty;

    /// <summary>
    /// 当前版本评审的 B 分支提交。
    /// </summary>
    [Column(TypeName = "varchar(64)")]
    public string HeadSha { get; set; } = string.Empty;

    /// <summary>
    /// 通过时间（UTC）。
    /// </summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>
    /// 合并时间（UTC）。
    /// </summary>
    public DateTime? MergedAt { get; set; }

    /// <summary>
    /// 关闭时间（UTC）。
    /// </summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>
    /// 检测到的合并结果提交。
    /// </summary>
    [Column(TypeName = "varchar(64)")]
    public string? MergeCommitSha { get; set; }

    /// <summary>
    /// 是否提示“未检测到合并，可能使用了 squash”。
    /// </summary>
    public bool MergeHint { get; set; }

    /// <summary>
    /// 连续未检测到合并的同步次数。
    /// </summary>
    public int MergeHintCount { get; set; }

    /// <summary>
    /// 关闭原因。
    /// </summary>
    [Column(TypeName = "varchar(500)")]
    public string? CloseReason { get; set; }

    /// <summary>
    /// 创建时间（UTC）。
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 更新时间（UTC）。
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

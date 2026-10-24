using System.ComponentModel.DataAnnotations.Schema;

namespace CodeSentinel.Data.Entities;

/// <summary>
/// 审核结论历史记录，只追加不修改。
/// </summary>
public class ReviewDecision
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
    /// 结论对应的评审版本。
    /// </summary>
    public Guid RevisionId { get; set; }

    /// <summary>
    /// 审核人。
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// 结论状态。
    /// </summary>
    public ReviewerState State { get; set; }

    /// <summary>
    /// 结论附言。
    /// </summary>
    [Column(TypeName = "text")]
    public string? Note { get; set; }

    /// <summary>
    /// 创建时间（UTC）。
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 更新时间（UTC）。
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

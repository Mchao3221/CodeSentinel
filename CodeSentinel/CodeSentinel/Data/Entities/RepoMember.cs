namespace CodeSentinel.Data.Entities;

/// <summary>
/// 仓库成员及其合并人标记。
/// </summary>
public class RepoMember : IAuditableEntity
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
    /// 成员用户。
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// 是否为合并人。
    /// </summary>
    public bool IsMerger { get; set; }

    /// <summary>
    /// 创建时间（UTC）。
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 更新时间（UTC）。
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

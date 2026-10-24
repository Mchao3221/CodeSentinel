namespace CodeSentinel.Data.Entities;

/// <summary>
/// 带创建/更新时间戳的实体，由 DbContext 自动维护。
/// </summary>
public interface IAuditableEntity
{
    /// <summary>
    /// 创建时间（UTC）。
    /// </summary>
    DateTime CreatedAt { get; set; }

    /// <summary>
    /// 更新时间（UTC）。
    /// </summary>
    DateTime UpdatedAt { get; set; }
}

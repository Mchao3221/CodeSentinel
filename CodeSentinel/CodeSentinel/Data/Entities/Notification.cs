using System.ComponentModel.DataAnnotations.Schema;

namespace CodeSentinel.Data.Entities;

/// <summary>
/// 站内通知。
/// </summary>
public class Notification : IAuditableEntity
{
    /// <summary>
    /// 主键。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 接收人。
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// 通知类型。
    /// </summary>
    public NotificationType Type { get; set; }

    /// <summary>
    /// 关联的评审单，可为空。
    /// </summary>
    public Guid? ReviewId { get; set; }

    /// <summary>
    /// 渲染所需的最小字段（JSON）。
    /// </summary>
    [Column(TypeName = "text")]
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// 阅读时间（UTC），空表示未读。
    /// </summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>
    /// 创建时间（UTC）。
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 更新时间（UTC）。
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

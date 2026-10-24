using System.ComponentModel.DataAnnotations.Schema;

namespace CodeSentinel.Data.Entities;

/// <summary>
/// 审计日志。
/// </summary>
public class AuditLog
{
    /// <summary>
    /// 主键。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 操作人，系统操作时可为空。
    /// </summary>
    public Guid? ActorId { get; set; }

    /// <summary>
    /// 动作标识。
    /// </summary>
    [Column(TypeName = "varchar(100)")]
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// 目标类型。
    /// </summary>
    [Column(TypeName = "varchar(50)")]
    public string TargetType { get; set; } = string.Empty;

    /// <summary>
    /// 目标主键。
    /// </summary>
    public Guid? TargetId { get; set; }

    /// <summary>
    /// 动作明细（JSON）。
    /// </summary>
    [Column(TypeName = "text")]
    public string? DetailJson { get; set; }

    /// <summary>
    /// 创建时间（UTC）。
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 更新时间（UTC）。
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

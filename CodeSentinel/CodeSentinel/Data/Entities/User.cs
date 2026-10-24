using System.ComponentModel.DataAnnotations.Schema;

namespace CodeSentinel.Data.Entities;

/// <summary>
/// 系统用户。
/// </summary>
public class User
{
    /// <summary>
    /// 主键。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 唯一登录名。
    /// </summary>
    [Column(TypeName = "varchar(64)")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 显示名。
    /// </summary>
    [Column(TypeName = "varchar(100)")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 邮箱，仅用于展示。
    /// </summary>
    [Column(TypeName = "varchar(256)")]
    public string? Email { get; set; }

    /// <summary>
    /// 密码哈希。
    /// </summary>
    [Column(TypeName = "varchar(256)")]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// 是否为系统管理员。
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// 是否启用，停用后不能登录。
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// 是否必须先修改密码。
    /// </summary>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// 创建时间（UTC）。
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 更新时间（UTC）。
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

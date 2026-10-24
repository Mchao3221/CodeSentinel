using System.ComponentModel.DataAnnotations.Schema;

namespace CodeSentinel.Data.Entities;

/// <summary>
/// 受管 git 仓库。
/// </summary>
public class Repo : IAuditableEntity
{
    /// <summary>
    /// 主键。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 系统内显示名，唯一。
    /// </summary>
    [Column(TypeName = "varchar(100)")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 仓库地址，仅支持 http 或 https。
    /// </summary>
    [Column(TypeName = "varchar(500)")]
    public string GitUrl { get; set; } = string.Empty;

    /// <summary>
    /// 新建评审时的默认目标分支。
    /// </summary>
    [Column(TypeName = "varchar(200)")]
    public string DefaultBaseBranch { get; set; } = string.Empty;

    /// <summary>
    /// 只读凭据密文（Data Protection 加密）。
    /// </summary>
    [Column(TypeName = "text")]
    public string? CredentialCipher { get; set; }

    /// <summary>
    /// 凭据类型。
    /// </summary>
    public RepoCredentialKind? CredentialKind { get; set; }

    /// <summary>
    /// 镜像同步间隔（秒）。
    /// </summary>
    public int SyncIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// 最近一次成功同步时间（UTC）。
    /// </summary>
    public DateTime? LastFetchedAt { get; set; }

    /// <summary>
    /// 最近一次同步失败信息。
    /// </summary>
    [Column(TypeName = "text")]
    public string? LastError { get; set; }

    /// <summary>
    /// 连续同步失败次数，成功后清零。
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// 下次允许重试的时间（UTC），用于失败退避。
    /// </summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>
    /// 是否启用，停用后不再同步。
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// 创建时间（UTC）。
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 更新时间（UTC）。
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

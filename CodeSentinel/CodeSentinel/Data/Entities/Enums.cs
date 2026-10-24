namespace CodeSentinel.Data.Entities;

/// <summary>
/// 评审单状态。
/// </summary>
public enum ReviewStatus
{
    /// <summary>
    /// 评审中，尚未得出结论。
    /// </summary>
    Open,

    /// <summary>
    /// 有审核人要求修改。
    /// </summary>
    ChangesRequested,

    /// <summary>
    /// 全部审核人通过。
    /// </summary>
    Approved,

    /// <summary>
    /// 已合并。
    /// </summary>
    Merged,

    /// <summary>
    /// 已关闭。
    /// </summary>
    Closed
}

/// <summary>
/// 审核人在评审单上的结论状态。
/// </summary>
public enum ReviewerState
{
    /// <summary>
    /// 待审核。
    /// </summary>
    Pending,

    /// <summary>
    /// 已通过。
    /// </summary>
    Approved,

    /// <summary>
    /// 要求修改。
    /// </summary>
    ChangesRequested,

    /// <summary>
    /// 已被移出审核人名单。
    /// </summary>
    Removed
}

/// <summary>
/// 评论状态。
/// </summary>
public enum CommentStatus
{
    /// <summary>
    /// 未解决。
    /// </summary>
    Open,

    /// <summary>
    /// 已解决。
    /// </summary>
    Resolved,

    /// <summary>
    /// 因新版本无法定位而失效。
    /// </summary>
    Outdated,

    /// <summary>
    /// 已软删除。
    /// </summary>
    Deleted
}

/// <summary>
/// diff 中的文件变更类型。
/// </summary>
public enum FileChangeStatus
{
    /// <summary>
    /// 新增文件。
    /// </summary>
    Added,

    /// <summary>
    /// 修改文件。
    /// </summary>
    Modified,

    /// <summary>
    /// 删除文件。
    /// </summary>
    Deleted,

    /// <summary>
    /// 重命名文件。
    /// </summary>
    Renamed
}

/// <summary>
/// 站内通知类型。
/// </summary>
public enum NotificationType
{
    /// <summary>
    /// 收到新的评审单。
    /// </summary>
    ReviewSubmitted,

    /// <summary>
    /// 审核人要求修改。
    /// </summary>
    ChangesRequested,

    /// <summary>
    /// 评审分支有新提交。
    /// </summary>
    NewCommits,

    /// <summary>
    /// 评审已通过。
    /// </summary>
    Approved,

    /// <summary>
    /// 评审已合并。
    /// </summary>
    Merged,

    /// <summary>
    /// 评审已关闭。
    /// </summary>
    Closed,

    /// <summary>
    /// 收到评论回复。
    /// </summary>
    CommentReply,

    /// <summary>
    /// 评审单上的审核人被移出，需要提交人补人。
    /// </summary>
    ReviewerRemoved
}

/// <summary>
/// 仓库只读凭据类型。
/// </summary>
public enum RepoCredentialKind
{
    /// <summary>
    /// 用户名加密码。
    /// </summary>
    Password,

    /// <summary>
    /// 访问令牌。
    /// </summary>
    Token
}

using CodeSentinel.Data.Entities;

namespace CodeSentinel.Reviews;

/// <summary>
/// 创建评审单请求。
/// </summary>
/// <param name="RepoId">仓库主键</param>
/// <param name="HeadBranch">提交分支</param>
/// <param name="BaseBranch">目标分支</param>
/// <param name="Title">标题</param>
/// <param name="Description">评审说明</param>
/// <param name="ReviewerIds">审核人主键列表</param>
public sealed record CreateReviewRequest(
    Guid RepoId,
    string HeadBranch,
    string BaseBranch,
    string Title,
    string? Description,
    IReadOnlyList<Guid> ReviewerIds);

/// <summary>
/// 评审单关联的仓库。
/// </summary>
/// <param name="Id">仓库主键</param>
/// <param name="Name">仓库名称</param>
public sealed record ReviewRepoRef(Guid Id, string Name);

/// <summary>
/// 评审单关联的用户。
/// </summary>
/// <param name="Id">用户主键</param>
/// <param name="DisplayName">显示名</param>
public sealed record ReviewUserRef(Guid Id, string DisplayName);

/// <summary>
/// 评审版本。
/// </summary>
/// <param name="Id">版本主键</param>
/// <param name="Seq">版本号</param>
/// <param name="HeadSha">版本终点提交</param>
/// <param name="CreatedAt">创建时间</param>
public sealed record ReviewRevisionDto(Guid Id, int Seq, string HeadSha, DateTime CreatedAt);

/// <summary>
/// 评审单审核人。
/// </summary>
/// <param name="UserId">审核人主键</param>
/// <param name="DisplayName">显示名</param>
/// <param name="State">结论状态</param>
/// <param name="Note">结论附言</param>
public sealed record ReviewReviewerDto(
    Guid UserId,
    string DisplayName,
    ReviewerState State,
    string? Note);

/// <summary>
/// 当前用户对评审单可执行的操作。
/// </summary>
/// <param name="CanDecide">能否给出结论</param>
/// <param name="CanManageReviewers">能否管理审核人</param>
/// <param name="CanClose">能否关闭评审单</param>
/// <param name="CanMarkMerged">能否标记为已合并</param>
public sealed record ReviewPermissionsDto(
    bool CanDecide,
    bool CanManageReviewers,
    bool CanClose,
    bool CanMarkMerged);

/// <summary>
/// 评审单详情。
/// </summary>
/// <param name="Id">评审单主键</param>
/// <param name="Repo">所属仓库</param>
/// <param name="BaseBranch">目标分支</param>
/// <param name="HeadBranch">提交分支</param>
/// <param name="Submitter">提交人</param>
/// <param name="Title">标题</param>
/// <param name="Description">评审说明</param>
/// <param name="Status">状态</param>
/// <param name="BaseSha">锁定的基线提交</param>
/// <param name="HeadSha">锁定的评审提交</param>
/// <param name="CurrentRevision">当前版本号</param>
/// <param name="Revisions">版本列表，按版本号升序</param>
/// <param name="Reviewers">审核人列表</param>
/// <param name="Permissions">当前用户的操作权限</param>
/// <param name="MergeHint">是否提示未检测到合并</param>
/// <param name="MergedAt">合并时间</param>
/// <param name="MergeCommitSha">合并结果提交</param>
/// <param name="CloseReason">关闭原因</param>
/// <param name="CreatedAt">创建时间</param>
/// <param name="UpdatedAt">更新时间</param>
public sealed record ReviewDetailDto(
    Guid Id,
    ReviewRepoRef Repo,
    string BaseBranch,
    string HeadBranch,
    ReviewUserRef Submitter,
    string Title,
    string? Description,
    ReviewStatus Status,
    string BaseSha,
    string HeadSha,
    int CurrentRevision,
    IReadOnlyList<ReviewRevisionDto> Revisions,
    IReadOnlyList<ReviewReviewerDto> Reviewers,
    ReviewPermissionsDto Permissions,
    bool MergeHint,
    DateTime? MergedAt,
    string? MergeCommitSha,
    string? CloseReason,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// 评审列表项。
/// </summary>
/// <param name="Id">评审单主键</param>
/// <param name="Repo">所属仓库</param>
/// <param name="Title">标题</param>
/// <param name="Status">状态</param>
/// <param name="BaseBranch">目标分支</param>
/// <param name="HeadBranch">提交分支</param>
/// <param name="Submitter">提交人</param>
/// <param name="ReviewersTotal">在册审核人数，不含已移除</param>
/// <param name="ReviewersApproved">已通过人数</param>
/// <param name="CurrentRevision">当前版本号</param>
/// <param name="CreatedAt">创建时间</param>
/// <param name="UpdatedAt">更新时间</param>
public sealed record ReviewListItemDto(
    Guid Id,
    ReviewRepoRef Repo,
    string Title,
    ReviewStatus Status,
    string BaseBranch,
    string HeadBranch,
    ReviewUserRef Submitter,
    int ReviewersTotal,
    int ReviewersApproved,
    int CurrentRevision,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// 活跃评审单冲突响应，在统一错误形状上追加已有评审单主键。
/// </summary>
/// <param name="Code">错误码</param>
/// <param name="Message">错误信息</param>
/// <param name="TraceId">请求追踪标识</param>
/// <param name="ReviewId">已存在的活跃评审单主键</param>
public sealed record ActiveReviewConflictResponse(
    string Code,
    string Message,
    string TraceId,
    Guid ReviewId);

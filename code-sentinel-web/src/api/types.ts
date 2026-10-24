/**
 * 接口层共享类型定义。
 */

/**
 * 当前登录用户。
 */
export interface AuthUser {
  /** 用户主键 */
  id: string;
  /** 登录名 */
  username: string;
  /** 显示名 */
  displayName: string;
  /** 是否管理员 */
  isAdmin: boolean;
  /** 是否必须修改密码 */
  mustChangePassword: boolean;
}

/**
 * 后端统一错误响应。
 */
export interface ApiErrorBody {
  /** 业务错误码 */
  code: string;
  /** 错误信息 */
  message: string;
  /** 请求追踪标识 */
  traceId: string;
}

/**
 * 统一分页响应。
 */
export interface Paginated<T> {
  /** 当前页数据 */
  items: T[];
  /** 总条数 */
  total: number;
  /** 当前页码 */
  page: number;
  /** 每页条数 */
  pageSize: number;
}

/**
 * 站内通知类型，后端以 SNAKE_CASE_UPPER 序列化。
 */
export type NotificationType =
  | 'REVIEW_SUBMITTED'
  | 'CHANGES_REQUESTED'
  | 'NEW_COMMITS'
  | 'APPROVED'
  | 'MERGED'
  | 'CLOSED'
  | 'COMMENT_REPLY'
  | 'REVIEWER_REMOVED'
  | 'DECISION_WITHDRAWN';

/**
 * 站内通知。
 */
export interface Notification {
  /** 通知主键 */
  id: string;
  /** 通知类型 */
  type: NotificationType;
  /** 关联评审单，可空 */
  reviewId: string | null;
  /** 通知附加数据 */
  payload: Record<string, unknown>;
  /** 阅读时间，空表示未读 */
  readAt: string | null;
  /** 创建时间 */
  createdAt: string;
}

/**
 * 未读通知数。
 */
export interface UnreadCount {
  /** 未读条数 */
  count: number;
}

/**
 * 评审单状态，后端以 SNAKE_CASE_UPPER 序列化。
 */
export type ReviewStatus = 'OPEN' | 'CHANGES_REQUESTED' | 'APPROVED' | 'MERGED' | 'CLOSED';

/**
 * 评审单列表查询范围。
 */
export type ReviewScope = 'mine' | 'to_review' | 'repo';

/**
 * 审核人在评审单上的结论状态。
 */
export type ReviewerState = 'PENDING' | 'APPROVED' | 'CHANGES_REQUESTED' | 'REMOVED';

/**
 * 评审单关联的仓库摘要。
 */
export interface ReviewRepoRef {
  /** 仓库主键 */
  id: string;
  /** 仓库名称 */
  name: string;
}

/**
 * 评审单关联的用户摘要。
 */
export interface ReviewUserRef {
  /** 用户主键 */
  id: string;
  /** 显示名 */
  displayName: string;
}

/**
 * 评审列表项。
 */
export interface ReviewListItem {
  /** 评审单主键 */
  id: string;
  /** 所属仓库 */
  repo: ReviewRepoRef;
  /** 标题 */
  title: string;
  /** 状态 */
  status: ReviewStatus;
  /** 目标分支 */
  baseBranch: string;
  /** 提交分支 */
  headBranch: string;
  /** 提交人 */
  submitter: ReviewUserRef;
  /** 在册审核人数 */
  reviewersTotal: number;
  /** 已通过人数 */
  reviewersApproved: number;
  /** 当前版本号 */
  currentRevision: number;
  /** 创建时间 */
  createdAt: string;
  /** 更新时间 */
  updatedAt: string;
}

/**
 * 评审版本。
 */
export interface ReviewRevision {
  /** 版本主键 */
  id: string;
  /** 版本号 */
  seq: number;
  /** 版本终点提交 */
  headSha: string;
  /** 创建时间 */
  createdAt: string;
}

/**
 * 评审单审核人。
 */
export interface ReviewReviewer {
  /** 审核人主键 */
  userId: string;
  /** 显示名 */
  displayName: string;
  /** 结论状态 */
  state: ReviewerState;
  /** 结论附言 */
  note: string | null;
}

/**
 * 当前用户对评审单可执行的操作。
 */
export interface ReviewPermissions {
  /** 能否给出结论 */
  canDecide: boolean;
  /** 能否管理审核人 */
  canManageReviewers: boolean;
  /** 能否关闭评审单 */
  canClose: boolean;
  /** 能否标记为已合并 */
  canMarkMerged: boolean;
}

/**
 * 评审单详情。
 */
export interface ReviewDetail {
  /** 评审单主键 */
  id: string;
  /** 所属仓库 */
  repo: ReviewRepoRef;
  /** 目标分支 */
  baseBranch: string;
  /** 提交分支 */
  headBranch: string;
  /** 提交人 */
  submitter: ReviewUserRef;
  /** 标题 */
  title: string;
  /** 评审说明 */
  description: string | null;
  /** 状态 */
  status: ReviewStatus;
  /** 锁定的基线提交 */
  baseSha: string;
  /** 锁定的评审提交 */
  headSha: string;
  /** 当前版本号 */
  currentRevision: number;
  /** 版本列表 */
  revisions: ReviewRevision[];
  /** 审核人列表 */
  reviewers: ReviewReviewer[];
  /** 当前用户的操作权限 */
  permissions: ReviewPermissions;
  /** 是否提示未检测到合并 */
  mergeHint: boolean;
  /** 合并时间 */
  mergedAt: string | null;
  /** 合并结果提交 */
  mergeCommitSha: string | null;
  /** 关闭原因 */
  closeReason: string | null;
  /** 创建时间 */
  createdAt: string;
  /** 更新时间 */
  updatedAt: string;
}

/**
 * 创建评审单请求。
 */
export interface CreateReviewInput {
  /** 仓库主键 */
  repoId: string;
  /** 目标分支 */
  baseBranch: string;
  /** 提交分支 */
  headBranch: string;
  /** 标题 */
  title: string;
  /** 评审说明，可选 */
  description?: string;
  /** 审核人主键列表 */
  reviewerIds: string[];
}

/**
 * 评审列表查询参数。
 */
export interface ReviewListQuery {
  /** 查询范围 */
  scope: ReviewScope;
  /** 仓库主键，不传表示全部 */
  repoId?: string;
  /** 状态，不传表示全部 */
  status?: ReviewStatus;
  /** 页码，从 1 开始 */
  page?: number;
  /** 每页条数 */
  pageSize?: number;
}

/**
 * 仓库。
 */
export interface Repo {
  /** 仓库主键 */
  id: string;
  /** 仓库名称 */
  name: string;
  /** git 地址 */
  gitUrl: string;
  /** 默认目标分支 */
  defaultBaseBranch: string;
  /** 同步间隔秒数 */
  syncIntervalSeconds: number;
  /** 最近一次成功同步时间 */
  lastFetchedAt: string | null;
  /** 最近一次同步错误 */
  lastError: string | null;
  /** 是否启用 */
  isActive: boolean;
  /** 成员数量 */
  memberCount: number;
  /** 当前用户是否为合并人 */
  isMerger: boolean;
}

/**
 * 分支。
 */
export interface Branch {
  /** 分支名 */
  name: string;
  /** 分支 head 提交 */
  headSha: string;
}

/**
 * 未开单分支。
 */
export interface UnreviewedBranch extends Branch {
  /** 领先默认目标分支的提交数 */
  aheadBy: number;
}

/**
 * 仓库成员。
 */
export interface RepoMember {
  /** 用户主键 */
  userId: string;
  /** 登录名 */
  username: string;
  /** 显示名 */
  displayName: string;
  /** 是否合并人 */
  isMerger: boolean;
}

/**
 * 活跃评审单冲突响应，在统一错误形状上追加已有评审单主键。
 */
export interface ActiveReviewConflict extends ApiErrorBody {
  /** 已存在的活跃评审单主键 */
  reviewId: string;
}

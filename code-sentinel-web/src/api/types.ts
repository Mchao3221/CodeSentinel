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
  /** 是否为该仓库的合并人，管理员不会自动具备 */
  isMerger: boolean;
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
  /** 用户是否启用 */
  isActive: boolean;
}

/**
 * 活跃评审单冲突响应，在统一错误形状上追加已有评审单主键。
 */
export interface ActiveReviewConflict extends ApiErrorBody {
  /** 已存在的活跃评审单主键 */
  reviewId: string;
}

/**
 * 审核人提交结论的请求。
 */
export interface DecideReviewInput {
  /** 结论状态，只支持 APPROVED、CHANGES_REQUESTED、PENDING */
  state: 'APPROVED' | 'CHANGES_REQUESTED' | 'PENDING';
  /** 结论附言，要求修改时必填 */
  note?: string;
}

/**
 * 关闭评审单的请求。
 */
export interface CloseReviewInput {
  /** 关闭原因，可选 */
  reason?: string;
}

/**
 * 手工标记评审单已合并的请求。
 */
export interface MarkMergedInput {
  /** 合并提交，未提供时后端取目标分支 head */
  mergeCommitSha?: string;
}

/**
 * 评论所在侧。
 */
export type CommentSide = 'OLD' | 'NEW';

/**
 * 评论状态。
 */
export type CommentStatus = 'OPEN' | 'RESOLVED' | 'OUTDATED' | 'DELETED';

/**
 * 行级评论。
 */
export interface ReviewComment {
  /** 评论主键 */
  id: string;
  /** 评论创建时所属评审版本 */
  revisionId: string;
  /** 作者主键 */
  authorId: string;
  /** 作者显示名 */
  authorDisplayName: string;
  /** 文件路径 */
  path: string;
  /** 行所在侧 */
  side: CommentSide;
  /** 该侧文件中的行号 */
  line: number;
  /** 评论内容 */
  body: string;
  /** 回复的父评论主键，可空 */
  parentId: string | null;
  /** 评论状态 */
  status: CommentStatus;
  /** 解决人主键 */
  resolvedBy: string | null;
  /** 解决时间 */
  resolvedAt: string | null;
  /** 创建时间 */
  createdAt: string;
  /** 更新时间 */
  updatedAt: string;
}

/**
 * 创建评论或回复的请求。
 */
export interface CreateCommentInput {
  /** 文件路径，重命名后传当前路径 */
  path: string;
  /** 行所在侧 */
  side: CommentSide;
  /** 该侧文件中的行号 */
  line: number;
  /** 评论内容，1 到 5000 个字符 */
  body: string;
  /** 回复的父评论主键，可空 */
  parentId?: string;
}

/**
 * diff 中的文件变更类型，后端以 SNAKE_CASE_UPPER 序列化。
 */
export type FileChangeStatus = 'ADDED' | 'MODIFIED' | 'DELETED' | 'RENAMED';

/**
 * diff 行的类型，后端以 SNAKE_CASE_UPPER 序列化。
 */
export type DiffLineType = 'CONTEXT' | 'ADD' | 'DELETE';

/**
 * blob 读取侧。
 */
export type DiffSide = 'OLD' | 'NEW';

/**
 * 文件列表项。
 */
export interface DiffFileListItem {
  /** 当前路径（重命名后为新路径） */
  path: string;
  /** 重命名前的路径，非重命名时为 null */
  oldPath: string | null;
  /** 变更类型 */
  status: FileChangeStatus;
  /** 新增行数 */
  additions: number;
  /** 删除行数 */
  deletions: number;
  /** 是否为二进制文件 */
  isBinary: boolean;
  /** 是否超过单文件 diff 大小限制 */
  isTooLarge: boolean;
  /** 当前用户是否已看过 */
  viewed: boolean;
}

/**
 * 文件列表响应。
 */
export interface DiffFileList {
  /** 文件摘要列表（截断后） */
  items: DiffFileListItem[];
  /** 真实文件总数 */
  total: number;
  /** 当前用户在当前版本已看过的文件数 */
  viewedCount: number;
  /** 当前版本文件总数 */
  fileCount: number;
  /** 是否因超过上限被截断 */
  truncated: boolean;
}

/**
 * diff 中的一行。
 */
export interface DiffLine {
  /** 行类型 */
  type: DiffLineType;
  /** 旧文件行号，新增行时为 null */
  oldLine: number | null;
  /** 新文件行号，删除行时为 null */
  newLine: number | null;
  /** 不带前缀的行文本 */
  text: string;
}

/**
 * 一个 diff 块及其行号范围。
 */
export interface DiffHunk {
  /** 旧文件起始行号 */
  oldStart: number;
  /** 旧文件覆盖行数 */
  oldLines: number;
  /** 新文件起始行号 */
  newStart: number;
  /** 新文件覆盖行数 */
  newLines: number;
  /** 块内的行 */
  lines: DiffLine[];
}

/**
 * 单文件 diff 详情。
 */
export interface DiffFile {
  /** 当前路径（重命名后为新路径） */
  path: string;
  /** 重命名前的路径，非重命名时为 null */
  oldPath: string | null;
  /** 变更类型 */
  status: FileChangeStatus;
  /** 新增行数 */
  additions: number;
  /** 删除行数 */
  deletions: number;
  /** 是否为二进制文件 */
  isBinary: boolean;
  /** 是否超过单文件 diff 大小限制 */
  isTooLarge: boolean;
  /** diff 块，二进制或超限时为空 */
  hunks: DiffHunk[];
}

/**
 * 单个文件的全文内容。
 */
export interface BlobContent {
  /** 文件路径 */
  path: string;
  /** UTF-8 文本内容，二进制或超限时为空字符串 */
  content: string;
  /** 是否为二进制内容 */
  isBinary: boolean;
  /** 是否超过大小限制 */
  isTooLarge: boolean;
}

/**
 * 仓库凭据类型。
 */
export type RepoCredentialKind = 'PASSWORD' | 'TOKEN';

/**
 * 新建仓库请求。
 */
export interface CreateRepoInput {
  /** 仓库名称 */
  name: string;
  /** git 地址 */
  gitUrl: string;
  /** 默认目标分支 */
  defaultBaseBranch: string;
  /** 只读凭据明文，格式为 username:secret，可选 */
  credential?: string;
  /** 凭据类型，默认 PASSWORD */
  credentialKind?: RepoCredentialKind;
  /** 同步间隔秒数，默认 300 */
  syncIntervalSeconds?: number;
}

/**
 * 局部更新仓库请求，未提供的字段保持不变。
 */
export interface UpdateRepoInput {
  /** 仓库名称 */
  name?: string;
  /** git 地址 */
  gitUrl?: string;
  /** 默认目标分支 */
  defaultBaseBranch?: string;
  /** 只读凭据明文，空串表示清空，不传表示保持不变 */
  credential?: string;
  /** 凭据类型 */
  credentialKind?: RepoCredentialKind;
  /** 同步间隔秒数 */
  syncIntervalSeconds?: number;
  /** 是否启用 */
  isActive?: boolean;
}

/**
 * 连接测试结果。
 */
export interface TestConnectionResult {
  /** 是否连接成功 */
  ok: boolean;
}

/**
 * 手动同步结果。
 */
export interface SyncResult {
  /** 是否完成同步 */
  synced: boolean;
}

/**
 * 仓库成员写入项。
 */
export interface RepoMemberInput {
  /** 用户主键 */
  userId: string;
  /** 是否合并人 */
  isMerger: boolean;
}

/**
 * 整体覆盖仓库成员的请求。
 */
export interface ReplaceRepoMembersInput {
  /** 目标成员列表 */
  members: RepoMemberInput[];
}

/**
 * 管理员用户列表项。
 */
export interface AdminUser {
  /** 用户主键 */
  id: string;
  /** 登录名 */
  username: string;
  /** 显示名 */
  displayName: string;
  /** 邮箱，可空 */
  email: string | null;
  /** 是否管理员 */
  isAdmin: boolean;
  /** 是否启用 */
  isActive: boolean;
  /** 是否必须修改密码 */
  mustChangePassword: boolean;
  /** 创建时间 */
  createdAt: string;
  /** 更新时间 */
  updatedAt: string;
}

/**
 * 新建用户请求。
 */
export interface CreateUserInput {
  /** 登录名 */
  username: string;
  /** 初始密码，用户首次登录后必须修改 */
  password: string;
  /** 显示名 */
  displayName: string;
  /** 邮箱，可选 */
  email?: string;
  /** 是否管理员 */
  isAdmin: boolean;
}

/**
 * 局部更新用户请求，未提供的字段保持不变。
 */
export interface UpdateUserInput {
  /** 显示名 */
  displayName?: string;
  /** 是否管理员 */
  isAdmin?: boolean;
  /** 是否启用 */
  isActive?: boolean;
}

/**
 * 重置密码结果。
 */
export interface ResetPasswordResult {
  /** 一次性新密码，仅本次响应返回 */
  password: string;
}

/**
 * 通知列表查询参数。
 */
export interface NotificationListQuery {
  /** 是否只看未读 */
  unreadOnly?: boolean;
  /** 页码，从 1 开始 */
  page?: number;
  /** 每页条数 */
  pageSize?: number;
}

/**
 * 标记通知已读请求，ids 与 all 至少提供一个。
 */
export type MarkNotificationsReadInput = { ids: string[] } | { all: true };

/**
 * 标记通知已读结果。
 */
export interface MarkReadResult {
  /** 本次实际更新行数 */
  updated: number;
}

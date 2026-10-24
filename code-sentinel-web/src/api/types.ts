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

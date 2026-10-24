/**
 * 站内通知接口。
 */
import { api } from './client';
import type {
  MarkNotificationsReadInput,
  MarkReadResult,
  Notification,
  NotificationListQuery,
  Paginated,
  UnreadCount,
} from './types';

/** 通知列表默认分页大小 */
const DEFAULT_PAGE_SIZE = 20;

/**
 * 分页获取当前用户的通知。
 * @param query 查询参数
 * @returns 分页通知列表
 */
export async function fetchNotifications(
  query: NotificationListQuery = {},
): Promise<Paginated<Notification>> {
  const { data } = await api.get<Paginated<Notification>>('/api/notifications', {
    params: {
      unreadOnly: query.unreadOnly,
      page: query.page ?? 1,
      pageSize: query.pageSize ?? DEFAULT_PAGE_SIZE,
    },
  });
  return data;
}

/**
 * 获取当前用户的未读通知数。
 */
export async function fetchUnreadCount(): Promise<UnreadCount> {
  const { data } = await api.get<UnreadCount>('/api/notifications/unread-count');
  return data;
}

/**
 * 按主键列表或全部标记当前用户的通知为已读。
 * @param input ids 列表或 all 标记
 * @returns 本次实际更新行数
 */
export async function markNotificationsRead(
  input: MarkNotificationsReadInput,
): Promise<MarkReadResult> {
  const { data } = await api.post<MarkReadResult>('/api/notifications/read', input);
  return data;
}

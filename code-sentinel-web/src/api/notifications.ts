import { api } from './client';
import type { UnreadCount } from './types';

/**
 * 获取当前用户的未读通知数。
 */
export async function fetchUnreadCount(): Promise<UnreadCount> {
  const { data } = await api.get<UnreadCount>('/api/notifications/unread-count');
  return data;
}

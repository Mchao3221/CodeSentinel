import { api, resetCsrfToken } from './client';
import type { AuthUser } from './types';

/**
 * 登录并返回当前用户。
 * 登录会重建会话，必须丢弃绑定在旧会话上的 CSRF 令牌。
 */
export async function login(username: string, password: string): Promise<AuthUser> {
  const { data } = await api.post<AuthUser>('/api/auth/login', { username, password });
  resetCsrfToken();
  return data;
}

/**
 * 获取当前登录用户；401 交给路由守卫处理，不触发全局跳转。
 */
export async function fetchMe(): Promise<AuthUser> {
  const { data } = await api.get<AuthUser>('/api/auth/me', { skipAuthRedirect: true });
  return data;
}

/**
 * 退出登录。
 */
export async function logout(): Promise<void> {
  await api.post('/api/auth/logout');
  resetCsrfToken();
}

/**
 * 修改当前用户密码。
 */
export async function changePassword(currentPassword: string, newPassword: string): Promise<void> {
  await api.post('/api/auth/change-password', { currentPassword, newPassword });
  resetCsrfToken();
}

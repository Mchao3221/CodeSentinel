/**
 * 管理员用户管理接口。
 */
import { api } from './client';
import type {
  AdminUser,
  CreateUserInput,
  Paginated,
  ResetPasswordResult,
  UpdateUserInput,
} from './types';

/** 用户列表默认分页大小，与后端上限一致 */
const DEFAULT_PAGE_SIZE = 20;

/**
 * 分页获取用户，按更新时间倒序。
 * @param page 页码，从 1 开始
 * @param pageSize 每页条数
 * @returns 分页用户列表
 */
export async function fetchUsers(
  page = 1,
  pageSize = DEFAULT_PAGE_SIZE,
): Promise<Paginated<AdminUser>> {
  const { data } = await api.get<Paginated<AdminUser>>('/api/users', {
    params: { page, pageSize },
  });
  return data;
}

/**
 * 新建用户，初始密码在首次登录后必须修改。
 * @param input 新建用户请求
 * @returns 新建的用户
 */
export async function createUser(input: CreateUserInput): Promise<AdminUser> {
  const { data } = await api.post<AdminUser>('/api/users', input);
  return data;
}

/**
 * 局部更新用户：显示名、管理员标记与启用状态。
 * @param userId 用户主键
 * @param input 更新请求
 * @returns 更新后的用户
 */
export async function updateUser(
  userId: string,
  input: UpdateUserInput,
): Promise<AdminUser> {
  const { data } = await api.patch<AdminUser>(`/api/users/${userId}`, input);
  return data;
}

/**
 * 重置用户密码，返回仅展示一次的新密码。
 * @param userId 用户主键
 * @returns 一次性新密码
 */
export async function resetUserPassword(userId: string): Promise<ResetPasswordResult> {
  const { data } = await api.post<ResetPasswordResult>(
    `/api/users/${userId}/reset-password`,
  );
  return data;
}

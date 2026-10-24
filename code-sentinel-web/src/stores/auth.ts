import { computed, ref } from 'vue';
import { defineStore } from 'pinia';

import {
  changePassword as changePasswordApi,
  fetchMe as fetchMeApi,
  login as loginApi,
  logout as logoutApi,
} from '../api/auth';
import type { AuthUser } from '../api/types';

/**
 * 登录状态仓库：保存当前用户并提供认证相关动作。
 */
export const useAuthStore = defineStore('auth', () => {
  /** 当前用户，null 表示未登录 */
  const user = ref<AuthUser | null>(null);

  /** 是否已完成一次会话探测，避免每次导航都请求 /auth/me */
  const initialized = ref(false);

  /** 是否已登录 */
  const isAuthenticated = computed(() => user.value !== null);

  /** 是否管理员 */
  const isAdmin = computed(() => user.value?.isAdmin === true);

  /** 是否必须先修改密码 */
  const mustChangePassword = computed(() => user.value?.mustChangePassword === true);

  /**
   * 登录成功后写入用户信息。
   */
  async function login(username: string, password: string): Promise<AuthUser> {
    const current = await loginApi(username, password);
    user.value = current;
    initialized.value = true;
    return current;
  }

  /**
   * 拉取当前用户；未登录时返回 null 并清空状态。
   */
  async function fetchMe(): Promise<AuthUser | null> {
    try {
      const current = await fetchMeApi();
      user.value = current;
      return current;
    } catch {
      user.value = null;
      return null;
    } finally {
      initialized.value = true;
    }
  }

  /**
   * 供路由守卫使用：首次导航时探测一次会话，之后直接读缓存。
   */
  async function ensureLoaded(): Promise<boolean> {
    if (initialized.value) {
      return isAuthenticated.value;
    }

    if (user.value !== null) {
      initialized.value = true;
      return true;
    }

    return (await fetchMe()) !== null;
  }

  /**
   * 清空本地登录状态，供 401 拦截器调用。
   */
  function clear(): void {
    user.value = null;
    initialized.value = true;
  }

  /**
   * 退出登录并清空本地状态。
   */
  async function logout(): Promise<void> {
    try {
      await logoutApi();
    } finally {
      clear();
    }
  }

  /**
   * 修改密码后清掉强制改密标记。
   */
  async function changePassword(currentPassword: string, newPassword: string): Promise<void> {
    await changePasswordApi(currentPassword, newPassword);
    if (user.value !== null) {
      user.value = { ...user.value, mustChangePassword: false };
    }
  }

  return {
    user,
    initialized,
    isAuthenticated,
    isAdmin,
    mustChangePassword,
    login,
    fetchMe,
    ensureLoaded,
    clear,
    logout,
    changePassword,
  };
});

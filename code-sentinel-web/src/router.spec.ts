import { AxiosError, type AxiosAdapter } from 'axios';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it } from 'vitest';

import { api, resetCsrfToken } from './api/client';
import router from './router';
import { useAuthStore } from './stores/auth';

/**
 * 让所有请求返回 401，模拟未登录会话。
 */
function mockUnauthenticated(): void {
  const adapter: AxiosAdapter = async (config) => {
    const response = {
      data: { code: 'UNAUTHENTICATED', message: '未登录', traceId: 'trace-router' },
      status: 401,
      statusText: 'Unauthorized',
      headers: {},
      config,
    };
    throw new AxiosError('未登录', undefined, config, undefined, response);
  };

  api.defaults.adapter = adapter;
}

/** 构造一个测试用户 */
function buildUser(isAdmin: boolean) {
  return {
    id: '9e87b9a2-1c3f-4d6a-8b2f-000000000002',
    username: 'bob',
    displayName: 'Bob',
    isAdmin,
    mustChangePassword: false,
  };
}

describe('路由守卫', () => {
  beforeEach(async () => {
    setActivePinia(createPinia());
    resetCsrfToken();
    // 每个用例从登录页出发，避免相同路由的重复导航被跳过
    await router.replace('/login');
  });

  it('未登录访问 /reviews 时跳转 /login 并带上 redirect', async () => {
    mockUnauthenticated();

    await router.push('/reviews');

    expect(router.currentRoute.value.path).toBe('/login');
    expect(router.currentRoute.value.query.redirect).toBe('/reviews');
  });

  it('非管理员访问 /repos 时跳转 /reviews', async () => {
    useAuthStore().user = buildUser(false);

    await router.push('/repos');

    expect(router.currentRoute.value.path).toBe('/reviews');
  });

  it('待改密用户访问 /reviews 时回到 /login 完成改密', async () => {
    useAuthStore().user = { ...buildUser(false), mustChangePassword: true };

    await router.push('/reviews');

    expect(router.currentRoute.value.path).toBe('/login');
    expect(router.currentRoute.value.query.redirect).toBe('/reviews');
  });

  it('管理员可以进入 /repos', async () => {
    useAuthStore().user = buildUser(true);

    await router.push('/repos');

    expect(router.currentRoute.value.path).toBe('/repos');
  });
});

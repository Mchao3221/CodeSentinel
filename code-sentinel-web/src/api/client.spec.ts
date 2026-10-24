import { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import router from '../router';
import { useAuthStore } from '../stores/auth';
import { api, resetCsrfToken } from './client';

/** 自定义适配器返回的模拟响应 */
interface MockResult {
  /** HTTP 状态码 */
  status: number;
  /** 响应体 */
  data: unknown;
}

/**
 * 把 axios 实例的适配器替换为内存实现，避免测试发起真实网络请求。
 */
function mockResponses(handler: (config: InternalAxiosRequestConfig) => MockResult): void {
  const adapter: AxiosAdapter = async (config) => {
    const result = handler(config);
    const response = {
      data: result.data,
      status: result.status,
      statusText: result.status >= 400 ? 'ERROR' : 'OK',
      headers: {},
      config,
    };

    if (result.status >= 400) {
      throw new AxiosError('请求失败', undefined, config, undefined, response);
    }

    return response;
  };

  api.defaults.adapter = adapter;
}

/** 构造一个已登录用户的测试数据 */
function buildUser(isAdmin = false) {
  return {
    id: '9e87b9a2-1c3f-4d6a-8b2f-000000000001',
    username: 'alice',
    displayName: 'Alice',
    isAdmin,
    mustChangePassword: false,
  };
}

/**
 * 等待 401 拦截器触发的异步跳转完成。
 */
async function waitForLoginRoute(): Promise<void> {
  await vi.waitFor(() => {
    expect(router.currentRoute.value.path).toBe('/login');
  });
}

describe('api 客户端', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    resetCsrfToken();
  });

  it('为变更请求添加 X-CSRF-TOKEN 并复用缓存的令牌', async () => {
    const sentTokens: string[] = [];
    let csrfCalls = 0;

    mockResponses((config) => {
      if (config.url === '/api/auth/csrf') {
        csrfCalls += 1;
        return { status: 200, data: { token: 'csrf-abc' } };
      }

      sentTokens.push(String(config.headers.get('X-CSRF-TOKEN') ?? ''));
      return { status: 200, data: {} };
    });

    await api.post('/api/notifications/read', { all: true });
    await api.post('/api/notifications/read', { all: true });

    expect(csrfCalls).toBe(1);
    expect(sentTokens).toEqual(['csrf-abc', 'csrf-abc']);
  });

  it('CSRF_INVALID 时重新获取令牌并重试一次', async () => {
    let attempts = 0;
    let csrfCalls = 0;

    mockResponses((config) => {
      if (config.url === '/api/auth/csrf') {
        csrfCalls += 1;
        return { status: 200, data: { token: `csrf-${csrfCalls}` } };
      }

      attempts += 1;
      if (attempts === 1) {
        return {
          status: 400,
          data: { code: 'CSRF_INVALID', message: 'CSRF 令牌缺失或无效', traceId: 'trace-1' },
        };
      }

      return { status: 200, data: {} };
    });

    await api.post('/api/notifications/read', { all: true });

    expect(attempts).toBe(2);
    expect(csrfCalls).toBe(2);
  });

  it('收到 401 时清除登录状态并跳转 /login', async () => {
    const authStore = useAuthStore();
    authStore.user = buildUser();
    await router.push('/reviews');
    expect(router.currentRoute.value.path).toBe('/reviews');

    mockResponses(() => ({
      status: 401,
      data: { code: 'UNAUTHENTICATED', message: '未登录', traceId: 'trace-2' },
    }));

    await expect(api.get('/api/repos')).rejects.toBeInstanceOf(AxiosError);

    await waitForLoginRoute();
    expect(authStore.isAuthenticated).toBe(false);
    expect(router.currentRoute.value.query.redirect).toBe('/reviews');
  });

  it('登录后重新获取 CSRF 令牌', async () => {
    let csrfCalls = 0;

    mockResponses((config) => {
      if (config.url === '/api/auth/csrf') {
        csrfCalls += 1;
        return { status: 200, data: { token: `csrf-${csrfCalls}` } };
      }

      if (config.url === '/api/auth/login') {
        return { status: 200, data: buildUser() };
      }

      return { status: 200, data: {} };
    });

    await api.post('/api/notifications/read', { all: true });
    expect(csrfCalls).toBe(1);

    await useAuthStore().login('alice', 'password1');
    await api.post('/api/notifications/read', { all: true });

    expect(csrfCalls).toBe(2);
  });
});

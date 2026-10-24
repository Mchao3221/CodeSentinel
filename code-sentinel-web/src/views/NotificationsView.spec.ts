/**
 * 通知中心测试：全部已读与点击通知跳转评审。
 */
import { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia, type Pinia } from 'pinia';
import ElementPlus from 'element-plus';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

import { api, resetCsrfToken } from '../api/client';
import NotificationsView from './NotificationsView.vue';

/** 关联评审单主键 */
const REVIEW_ID = '77777777-7777-4777-8777-777777777777';

/** 通知主键 */
const NOTIFICATION_ID = '88888888-8888-4888-8888-888888888888';

/** 模拟响应 */
interface MockResult {
  /** HTTP 状态码 */
  status: number;
  /** 响应体 */
  data: unknown;
}

/**
 * 把 axios 实例的适配器替换为内存实现，避免测试发起真实网络请求。
 * @param handler 根据请求返回模拟响应
 */
function mockAdapter(handler: (config: InternalAxiosRequestConfig) => MockResult): void {
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

/**
 * 构造通知列表与标记已读接口数据。
 * @returns 标记已读请求体记录
 */
function mockNotificationApis(): Record<string, unknown>[] {
  const reads: Record<string, unknown>[] = [];
  let read = false;

  mockAdapter((config) => {
    const method = (config.method ?? 'get').toLowerCase();
    const url = config.url ?? '';

    if (url === '/api/auth/csrf') {
      return { status: 200, data: { token: 'csrf-test' } };
    }

    if (url === '/api/notifications' && method === 'get') {
      return {
        status: 200,
        data: {
          items: [
            {
              id: NOTIFICATION_ID,
              type: 'REVIEW_SUBMITTED',
              reviewId: REVIEW_ID,
              payload: { title: '修复登录问题', repoName: '示例仓库' },
              readAt: read ? '2026-01-02T00:00:00Z' : null,
              createdAt: '2026-01-01T00:00:00Z',
            },
          ],
          total: 1,
          page: 1,
          pageSize: 20,
        },
      };
    }

    if (url === '/api/notifications/read' && method === 'post') {
      const body = JSON.parse(String(config.data)) as Record<string, unknown>;
      reads.push(body);
      read = true;
      return { status: 200, data: { updated: 1 } };
    }

    if (url === '/api/notifications/unread-count' && method === 'get') {
      return { status: 200, data: { count: read ? 0 : 1 } };
    }

    return { status: 404, data: { code: 'NOT_FOUND', message: url, traceId: 'trace' } };
  });

  return reads;
}

/**
 * 注册测试用的 Pinia。
 */
function createTestPinia(): Pinia {
  const pinia = createPinia();
  setActivePinia(pinia);
  return pinia;
}

/**
 * 挂载通知中心。
 * @param pinia 测试 Pinia
 * @param router 测试路由
 * @returns 组件包装器
 */
function mountView(pinia: Pinia, router: Router): VueWrapper {
  return mount(NotificationsView, {
    global: { plugins: [pinia, ElementPlus, router] },
  });
}

/**
 * 创建测试用路由。
 */
function createTestRouter(): Router {
  return createRouter({ history: createMemoryHistory(), routes: [] });
}

describe('通知中心视图', () => {
  beforeAll(() => {
    // Element Plus 表格依赖 ResizeObserver，jsdom 未实现
    globalThis.ResizeObserver = class {
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
    } as unknown as typeof ResizeObserver;
  });

  beforeEach(() => {
    resetCsrfToken();
    vi.restoreAllMocks();
  });

  it('全部已读时提交 all=true 并刷新列表', async () => {
    const reads = mockNotificationApis();
    const wrapper = mountView(createTestPinia(), createTestRouter());
    await flushPromises();

    expect(wrapper.text()).toContain('修复登录问题');
    await wrapper.find('.mark-all-read').trigger('click');
    await flushPromises();

    expect(reads).toEqual([{ all: true }]);
    expect(wrapper.text()).toContain('已读');
  });

  it('点击带评审单的通知会标记已读并跳转详情', async () => {
    const reads = mockNotificationApis();
    const router = createTestRouter();
    const pushSpy = vi.spyOn(router, 'push').mockResolvedValue(undefined);
    const wrapper = mountView(createTestPinia(), router);
    await flushPromises();

    await wrapper.find('.notification-row').trigger('click');
    await flushPromises();

    expect(reads).toEqual([{ ids: [NOTIFICATION_ID] }]);
    expect(pushSpy).toHaveBeenCalledWith(`/reviews/${REVIEW_ID}`);
  });
});

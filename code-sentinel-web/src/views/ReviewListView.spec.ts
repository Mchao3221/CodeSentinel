import { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia, type Pinia } from 'pinia';
import ElementPlus, { ElSelect } from 'element-plus';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

import { api, resetCsrfToken } from '../api/client';
import { useAuthStore } from '../stores/auth';
import ReviewListView from './ReviewListView.vue';

/** 测试评审单主键 */
const REVIEW_ID = '55555555-5555-4555-8555-555555555555';

/** 模拟响应 */
interface MockResult {
  /** HTTP 状态码 */
  status: number;
  /** 响应体 */
  data: unknown;
}

/**
 * 把 axios 实例的适配器替换为内存实现，避免测试发起真实网络请求。
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
 * 构造列表数据并记录每次列表请求的参数。
 */
function mockListApis(): { paramsList: Record<string, unknown>[] } {
  const paramsList: Record<string, unknown>[] = [];

  mockAdapter((config) => {
    const url = config.url ?? '';

    if (url === '/api/auth/csrf') {
      return { status: 200, data: { token: 'csrf-test' } };
    }

    if (url === '/api/repos' && (config.method ?? 'get').toLowerCase() === 'get') {
      return {
        status: 200,
        data: {
          items: [
            {
              id: '33333333-3333-4333-8333-333333333333',
              name: '示例仓库',
              gitUrl: 'https://example.com/repo.git',
              defaultBaseBranch: 'main',
              syncIntervalSeconds: 300,
              lastFetchedAt: null,
              lastError: null,
              isActive: true,
              memberCount: 2,
              isMerger: false,
            },
          ],
          total: 1,
          page: 1,
          pageSize: 100,
        },
      };
    }

    if (url === '/api/reviews') {
      paramsList.push((config.params ?? {}) as Record<string, unknown>);
      return {
        status: 200,
        data: {
          items: [
            {
              id: REVIEW_ID,
              repo: { id: '33333333-3333-4333-8333-333333333333', name: '示例仓库' },
              title: '修复登录问题',
              status: 'OPEN',
              baseBranch: 'main',
              headBranch: 'feature-a',
              submitter: {
                id: '11111111-1111-4111-8111-111111111111',
                displayName: 'Alice',
              },
              reviewersTotal: 3,
              reviewersApproved: 2,
              currentRevision: 1,
              createdAt: '2026-10-24T01:00:00Z',
              updatedAt: '2026-10-24T02:30:00Z',
            },
          ],
          total: 1,
          page: 1,
          pageSize: 20,
        },
      };
    }

    return { status: 404, data: { code: 'NOT_FOUND', message: url, traceId: 'trace' } };
  });

  return { paramsList };
}

/**
 * 注册测试用的 Pinia 与登录用户。
 */
function createTestPinia(): Pinia {
  const pinia = createPinia();
  setActivePinia(pinia);
  useAuthStore().user = {
    id: '11111111-1111-4111-8111-111111111111',
    username: 'alice',
    displayName: 'Alice',
    isAdmin: false,
    mustChangePassword: false,
  };
  return pinia;
}

/**
 * 挂载列表视图，并返回包装器与路由。
 */
function mountView(pinia: Pinia, router: Router): VueWrapper {
  return mount(ReviewListView, {
    global: { plugins: [pinia, ElementPlus, router] },
  });
}

describe('评审列表视图', () => {
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
  });

  it('渲染三个标签，并按状态筛选时重置分页', async () => {
    const { paramsList } = mockListApis();
    const pinia = createTestPinia();
    const router = createRouter({ history: createMemoryHistory(), routes: [] });
    const wrapper = mountView(pinia, router);

    await flushPromises();

    const tabLabels = wrapper.findAll('.el-tabs__item').map((tab) => tab.text());
    expect(tabLabels).toEqual(['待我评审', '我提交的', '全部']);

    // 默认查询“待我评审”
    expect(paramsList[0]).toMatchObject({ scope: 'to_review', page: 1, pageSize: 20 });

    wrapper.findAllComponents(ElSelect)[1].vm.$emit('update:modelValue', 'APPROVED');
    await flushPromises();

    await vi.waitFor(() => {
      expect(paramsList.at(-1)).toMatchObject({
        scope: 'to_review',
        status: 'APPROVED',
        page: 1,
      });
    });
  });

  it('点击行跳转到评审详情', async () => {
    mockListApis();
    const pinia = createTestPinia();
    const router = createRouter({ history: createMemoryHistory(), routes: [] });
    const pushSpy = vi.spyOn(router, 'push').mockResolvedValue(undefined);
    const wrapper = mountView(pinia, router);

    await flushPromises();
    await wrapper.find('.el-table__row').trigger('click');

    expect(pushSpy).toHaveBeenCalledWith(`/reviews/${REVIEW_ID}`);
  });
});

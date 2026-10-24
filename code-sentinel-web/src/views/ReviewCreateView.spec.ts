import { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia, type Pinia } from 'pinia';
import ElementPlus, { ElOption, ElSelect } from 'element-plus';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

import { api, resetCsrfToken } from '../api/client';
import { useAuthStore } from '../stores/auth';
import ReviewCreateView from './ReviewCreateView.vue';

/** 当前登录用户主键，评审人选项里必须被排除 */
const SELF_ID = '11111111-1111-4111-8111-111111111111';

/** 其他成员主键，可作为审核人 */
const OTHER_ID = '22222222-2222-4222-8222-222222222222';

/** 测试仓库主键 */
const REPO_ID = '33333333-3333-4333-8333-333333333333';

/** 创建成功后返回的评审单主键 */
const REVIEW_ID = '44444444-4444-4444-8444-444444444444';

/** 冲突时后端返回的已有评审单主键 */
const EXISTING_REVIEW_ID = '66666666-6666-4666-8666-666666666666';

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
 * 构造创建视图所需的接口数据，并记录创建请求体。
 * @param options.conflict 为 true 时创建接口返回 409 活跃评审冲突
 */
function mockCreateApis(options: { conflict?: boolean } = {}): {
  bodies: Record<string, unknown>[];
} {
  const bodies: Record<string, unknown>[] = [];

  mockAdapter((config) => {
    const method = (config.method ?? 'get').toLowerCase();
    const url = config.url ?? '';

    if (url === '/api/auth/csrf') {
      return { status: 200, data: { token: 'csrf-test' } };
    }

    if (url === `/api/repos/${REPO_ID}/branches/unreviewed`) {
      return {
        status: 200,
        data: { items: [{ name: 'feature-a', headSha: 'sha-a', aheadBy: 2 }] },
      };
    }

    if (url === `/api/repos/${REPO_ID}/branches`) {
      return {
        status: 200,
        data: {
          items: [
            { name: 'main', headSha: 'sha-main' },
            { name: 'feature-a', headSha: 'sha-a' },
            { name: 'feature-b', headSha: 'sha-b' },
          ],
        },
      };
    }

    if (url === `/api/repos/${REPO_ID}/members`) {
      return {
        status: 200,
        data: {
          items: [
            {
              userId: OTHER_ID,
              username: 'bob',
              displayName: 'Bob',
              isMerger: false,
            },
            {
              userId: SELF_ID,
              username: 'alice',
              displayName: 'Alice',
              isMerger: false,
            },
          ],
        },
      };
    }

    if (url === '/api/repos' && method === 'get') {
      return {
        status: 200,
        data: {
          items: [
            {
              id: REPO_ID,
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

    if (url === '/api/reviews' && method === 'post') {
      const body = JSON.parse(String(config.data)) as Record<string, unknown>;
      bodies.push(body);
      if (options.conflict === true) {
        return {
          status: 409,
          data: {
            code: 'ACTIVE_REVIEW_EXISTS',
            message: '该分支已有评审单',
            traceId: 'trace-conflict',
            reviewId: EXISTING_REVIEW_ID,
          },
        };
      }

      return { status: 201, data: { id: REVIEW_ID } };
    }

    return { status: 404, data: { code: 'NOT_FOUND', message: url, traceId: 'trace' } };
  });

  return { bodies };
}

/**
 * 注册测试用的 Pinia 与登录用户。
 */
function createTestPinia(): Pinia {
  const pinia = createPinia();
  setActivePinia(pinia);
  useAuthStore().user = {
    id: SELF_ID,
    username: 'alice',
    displayName: 'Alice',
    isAdmin: false,
    mustChangePassword: false,
  };
  return pinia;
}

/**
 * 挂载创建视图，并返回包装器与路由。
 */
function mountView(pinia: Pinia, router: Router): VueWrapper {
  return mount(ReviewCreateView, {
    global: { plugins: [pinia, ElementPlus, router] },
  });
}

/**
 * 等待仓库选项加载完成并选中测试仓库。
 */
async function selectRepo(wrapper: VueWrapper): Promise<void> {
  await flushPromises();
  const selects = wrapper.findAllComponents(ElSelect);
  selects[0].vm.$emit('update:modelValue', REPO_ID);
  await flushPromises();
}

/**
 * 读取指定序号的 el-select 组件。
 */
function selectAt(wrapper: VueWrapper, index: number) {
  return wrapper.findAllComponents(ElSelect)[index];
}

describe('新建评审视图', () => {
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

  it('提交 repoId、baseBranch、headBranch、reviewerIds 和 title', async () => {
    const { bodies } = mockCreateApis();
    const pinia = createTestPinia();
    const router = createRouter({ history: createMemoryHistory(), routes: [] });
    const pushSpy = vi.spyOn(router, 'push').mockResolvedValue(undefined);
    const wrapper = mountView(pinia, router);

    await selectRepo(wrapper);

    // 目标分支默认预选仓库的默认分支
    expect(selectAt(wrapper, 1).props('modelValue')).toBe('main');

    selectAt(wrapper, 2).vm.$emit('update:modelValue', 'feature-a');
    selectAt(wrapper, 3).vm.$emit('update:modelValue', [OTHER_ID]);
    await flushPromises();

    await wrapper.find('input[placeholder="请输入标题"]').setValue('修复登录问题');
    await flushPromises();

    await wrapper.find('.submit-button').trigger('click');
    await flushPromises();

    expect(bodies).toHaveLength(1);
    expect(bodies[0]).toMatchObject({
      repoId: REPO_ID,
      baseBranch: 'main',
      headBranch: 'feature-a',
      title: '修复登录问题',
      reviewerIds: [OTHER_ID],
    });
    expect(bodies[0].description).toBeUndefined();
    expect(pushSpy).toHaveBeenCalledWith(`/reviews/${REVIEW_ID}`);
  });

  it('审核人选项排除当前用户', async () => {
    mockCreateApis();
    const pinia = createTestPinia();
    const router = createRouter({ history: createMemoryHistory(), routes: [] });
    const wrapper = mountView(pinia, router);

    await selectRepo(wrapper);

    const optionValues = wrapper
      .findAllComponents(ElOption)
      .map((option) => String(option.props('value')));

    expect(optionValues).toContain(OTHER_ID);
    expect(optionValues).not.toContain(SELF_ID);
  });

  it('未选仓库、我的分支和审核人时提交按钮禁用，补齐标题后启用', async () => {
    mockCreateApis();
    const pinia = createTestPinia();
    const router = createRouter({ history: createMemoryHistory(), routes: [] });
    const wrapper = mountView(pinia, router);

    await flushPromises();
    expect(wrapper.find('.submit-button').attributes('disabled')).toBeDefined();

    // 选仓库后仍缺我的分支、审核人和标题
    await selectRepo(wrapper);
    expect(wrapper.find('.submit-button').attributes('disabled')).toBeDefined();

    selectAt(wrapper, 2).vm.$emit('update:modelValue', 'feature-a');
    await flushPromises();
    expect(wrapper.find('.submit-button').attributes('disabled')).toBeDefined();

    selectAt(wrapper, 3).vm.$emit('update:modelValue', [OTHER_ID]);
    await flushPromises();
    expect(wrapper.find('.submit-button').attributes('disabled')).toBeDefined();

    await wrapper.find('input[placeholder="请输入标题"]').setValue('补充说明');
    await flushPromises();
    expect(wrapper.find('.submit-button').attributes('disabled')).toBeUndefined();
  });

  it('409 冲突时保留表单并展示已有评审单跳转', async () => {
    mockCreateApis({ conflict: true });
    const pinia = createTestPinia();
    const router = createRouter({ history: createMemoryHistory(), routes: [] });
    const pushSpy = vi.spyOn(router, 'push').mockResolvedValue(undefined);
    const wrapper = mountView(pinia, router);

    await selectRepo(wrapper);
    selectAt(wrapper, 2).vm.$emit('update:modelValue', 'feature-a');
    selectAt(wrapper, 3).vm.$emit('update:modelValue', [OTHER_ID]);
    await flushPromises();
    await wrapper.find('input[placeholder="请输入标题"]').setValue('修复登录问题');
    await flushPromises();

    await wrapper.find('.submit-button').trigger('click');
    await flushPromises();

    expect(wrapper.find('.review-create-conflict').text()).toContain('该分支已有评审单');
    // 表单状态必须保留
    expect(
      (wrapper.find('input[placeholder="请输入标题"]').element as HTMLInputElement).value,
    ).toBe('修复登录问题');

    await wrapper.find('.review-create-conflict .el-button').trigger('click');
    await flushPromises();
    expect(pushSpy).toHaveBeenCalledWith(`/reviews/${EXISTING_REVIEW_ID}`);
  });
});

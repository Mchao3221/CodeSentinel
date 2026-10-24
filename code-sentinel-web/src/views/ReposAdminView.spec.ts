/**
 * 仓库管理页测试：新建、连接测试、成员合并人校验与列表展示。
 */
import { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia, type Pinia } from 'pinia';
import ElementPlus, { ElCheckbox } from 'element-plus';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';

import { api, resetCsrfToken } from '../api/client';
import ReposAdminView from './ReposAdminView.vue';

/** 测试仓库主键 */
const REPO_ID = '33333333-3333-4333-8333-333333333333';

/** 可分配为仓库成员的用户主键 */
const USER_ID = '44444444-4444-4444-8444-444444444444';

/** 第二页用户主键，同时是既有成员 */
const PAGE2_ID = '99999999-9999-4999-8999-999999999999';

/** 已停用的既有成员主键 */
const INACTIVE_ID = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';

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

/** 模拟用户 */
interface UserStub {
  /** 用户主键 */
  id: string;
  /** 登录名 */
  username: string;
  /** 显示名 */
  displayName: string;
  /** 是否启用 */
  isActive: boolean;
}

/** 模拟成员 */
interface MemberStub {
  /** 用户主键 */
  userId: string;
  /** 登录名 */
  username: string;
  /** 显示名 */
  displayName: string;
  /** 是否合并人 */
  isMerger: boolean;
  /** 是否启用 */
  isActive: boolean;
}

/** 默认用户 */
const DEFAULT_USER: UserStub = {
  id: USER_ID,
  username: 'alice',
  displayName: 'Alice',
  isActive: true,
};

/** 仓库接口模拟选项 */
interface RepoApiOptions {
  /** 连接测试前 N 次失败，之后成功 */
  connectionFailures?: number;
  /** 用户列表，按分页切片返回 */
  users?: UserStub[];
  /** 仓库既有成员，含停用成员 */
  members?: MemberStub[];
}

/** 仓库接口模拟结果 */
interface RepoApiMocks {
  /** 新建仓库请求体 */
  created: Record<string, unknown>[];
  /** 修改仓库请求体 */
  patched: Record<string, unknown>[];
  /** 覆盖成员请求体 */
  memberPuts: Record<string, unknown>[];
  /** 连接测试调用次数 */
  connectionCalls: number;
}

/**
 * 构造仓库管理页所需的接口数据。
 * @param options 接口模拟选项
 * @returns 记录到的请求体与调用次数
 */
function mockRepoApis(options: RepoApiOptions = {}): RepoApiMocks {
  const mocks: RepoApiMocks = {
    created: [],
    patched: [],
    memberPuts: [],
    connectionCalls: 0,
  };
  let repo = {
    id: REPO_ID,
    name: '示例仓库',
    gitUrl: 'https://example.com/repo.git',
    defaultBaseBranch: 'main',
    syncIntervalSeconds: 300,
    lastFetchedAt: null,
    lastError: null,
    isActive: true,
    memberCount: 1,
    isMerger: true,
  };

  mockAdapter((config) => {
    const method = (config.method ?? 'get').toLowerCase();
    const url = config.url ?? '';

    if (url === '/api/auth/csrf') {
      return { status: 200, data: { token: 'csrf-test' } };
    }

    if (url === '/api/repos' && method === 'get') {
      return {
        status: 200,
        data: { items: [repo], total: 1, page: 1, pageSize: 20 },
      };
    }

    if (url === '/api/repos' && method === 'post') {
      const body = JSON.parse(String(config.data)) as Record<string, unknown>;
      mocks.created.push(body);
      repo = { ...repo, ...body, id: REPO_ID };
      return { status: 201, data: repo };
    }

    if (url === `/api/repos/${REPO_ID}` && method === 'patch') {
      const body = JSON.parse(String(config.data)) as Record<string, unknown>;
      mocks.patched.push(body);
      repo = { ...repo, ...body };
      return { status: 200, data: repo };
    }

    if (url === `/api/repos/${REPO_ID}/test-connection` && method === 'post') {
      mocks.connectionCalls += 1;
      const failures = options.connectionFailures ?? 0;
      if (mocks.connectionCalls <= failures) {
        return {
          status: 400,
          data: {
            code: 'REPO_UNREACHABLE',
            message: '无法连接远端仓库',
            traceId: 'trace-connection',
          },
        };
      }

      return { status: 200, data: { ok: true } };
    }

    if (url === `/api/repos/${REPO_ID}/sync` && method === 'post') {
      return { status: 200, data: { synced: true } };
    }

    if (url === '/api/users' && method === 'get') {
      const params = (config.params ?? {}) as { page?: number; pageSize?: number };
      const allUsers = options.users ?? [DEFAULT_USER];
      const currentPage = params.page ?? 1;
      const currentPageSize = params.pageSize ?? 100;
      const start = (currentPage - 1) * currentPageSize;

      return {
        status: 200,
        data: {
          items: allUsers.slice(start, start + currentPageSize).map((user) => ({
            ...user,
            email: null,
            isAdmin: false,
            mustChangePassword: false,
            createdAt: '2026-01-01T00:00:00Z',
            updatedAt: '2026-01-01T00:00:00Z',
          })),
          total: allUsers.length,
          page: currentPage,
          pageSize: currentPageSize,
        },
      };
    }

    if (url === `/api/repos/${REPO_ID}/members` && method === 'get') {
      return { status: 200, data: { items: options.members ?? [] } };
    }

    if (url === `/api/repos/${REPO_ID}/members` && method === 'put') {
      const body = JSON.parse(String(config.data)) as Record<string, unknown>;
      mocks.memberPuts.push(body);
      return {
        status: 200,
        data: {
          items: [{ userId: USER_ID, username: 'alice', displayName: 'Alice', isMerger: true }],
        },
      };
    }

    return { status: 404, data: { code: 'NOT_FOUND', message: url, traceId: 'trace' } };
  });

  return mocks;
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
 * 挂载仓库管理页。
 * @param pinia 测试 Pinia
 * @param router 测试路由
 * @returns 组件包装器
 */
function mountView(pinia: Pinia, router: Router): VueWrapper {
  return mount(ReposAdminView, {
    global: { plugins: [pinia, ElementPlus, router] },
  });
}

/**
 * 创建测试用路由。
 */
function createTestRouter(): Router {
  return createRouter({ history: createMemoryHistory(), routes: [] });
}

/**
 * 按 class 筛选复选框组件。
 * @param wrapper 组件包装器
 * @param className 目标 class
 * @returns 复选框组件列表
 */
function checkboxesByClass(wrapper: VueWrapper, className: string) {
  return wrapper
    .findAllComponents(ElCheckbox)
    .filter((item) => item.classes().includes(className));
}

describe('仓库管理视图', () => {
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

  it('新建仓库时提交名称、地址、默认分支与凭据', async () => {
    const mocks = mockRepoApis();
    const wrapper = mountView(createTestPinia(), createTestRouter());
    await flushPromises();

    await wrapper.find('.create-repo-button').trigger('click');
    await flushPromises();

    await wrapper.find('input[placeholder="请输入仓库名称"]').setValue('新仓库');
    await wrapper
      .find('input[placeholder="请输入 git 地址"]')
      .setValue('https://example.com/new.git');
    await wrapper.find('input[placeholder="请输入默认目标分支"]').setValue('develop');
    await wrapper.find('input[placeholder="username:secret"]').setValue('git:secret');
    await wrapper.find('.repo-drawer-save').trigger('click');
    await flushPromises();

    expect(mocks.created).toHaveLength(1);
    expect(mocks.created[0]).toMatchObject({
      name: '新仓库',
      gitUrl: 'https://example.com/new.git',
      defaultBaseBranch: 'develop',
      credential: 'git:secret',
      credentialKind: 'PASSWORD',
      syncIntervalSeconds: 300,
    });
  });

  it('连接测试失败后阻止保存，修改地址并测试成功后恢复', async () => {
    const mocks = mockRepoApis({ connectionFailures: 1 });
    const wrapper = mountView(createTestPinia(), createTestRouter());
    await flushPromises();

    await wrapper.find('.edit-repo-button').trigger('click');
    await flushPromises();

    await wrapper.find('.test-connection-button').trigger('click');
    await flushPromises();

    expect(wrapper.find('.connection-result').text()).toContain('无法连接远端仓库');
    expect(wrapper.find('.repo-drawer-save').attributes('disabled')).toBeDefined();

    // 修改地址视为重新验证，保存按钮恢复可点
    await wrapper
      .find('input[placeholder="请输入 git 地址"]')
      .setValue('https://example.com/other.git');
    await flushPromises();
    expect(wrapper.find('.repo-drawer-save').attributes('disabled')).toBeUndefined();

    await wrapper.find('.test-connection-button').trigger('click');
    await flushPromises();

    expect(wrapper.find('.connection-result').text()).toContain('连接成功');
    await wrapper.find('.repo-drawer-save').trigger('click');
    await flushPromises();

    expect(mocks.patched).toHaveLength(1);
    expect(mocks.patched[0]).toMatchObject({ gitUrl: 'https://example.com/other.git' });
  });

  it('没有合并人时禁用成员保存，勾选后可提交完整成员列表', async () => {
    const mocks = mockRepoApis();
    const wrapper = mountView(createTestPinia(), createTestRouter());
    await flushPromises();

    await wrapper.find('.members-repo-button').trigger('click');
    await flushPromises();

    const selectBoxes = checkboxesByClass(wrapper, 'member-select');
    expect(selectBoxes).toHaveLength(1);
    expect(wrapper.find('.members-save').attributes('disabled')).toBeDefined();

    selectBoxes[0].vm.$emit('update:modelValue', true);
    await flushPromises();
    expect(wrapper.find('.members-save').attributes('disabled')).toBeDefined();

    const mergerBoxes = checkboxesByClass(wrapper, 'member-merger');
    mergerBoxes[0].vm.$emit('update:modelValue', true);
    await flushPromises();
    expect(wrapper.find('.members-save').attributes('disabled')).toBeUndefined();

    await wrapper.find('.members-save').trigger('click');
    await flushPromises();

    expect(mocks.memberPuts).toHaveLength(1);
    expect(mocks.memberPuts[0]).toEqual({
      members: [{ userId: USER_ID, isMerger: true }],
    });
  });

  it('分页加载全部用户，保存时保留第二页既有成员并排除停用成员', async () => {
    const users: UserStub[] = Array.from({ length: 100 }, (_, index) => ({
      id: `user-${index}`,
      username: `user-${index}`,
      displayName: `用户 ${index}`,
      isActive: true,
    }));
    users.push({
      id: PAGE2_ID,
      username: 'page-two',
      displayName: '第二页成员',
      isActive: true,
    });

    const mocks = mockRepoApis({
      users,
      members: [
        {
          userId: PAGE2_ID,
          username: 'page-two',
          displayName: '第二页成员',
          isMerger: true,
          isActive: true,
        },
        {
          userId: INACTIVE_ID,
          username: 'ghost',
          displayName: '停用成员',
          isMerger: false,
          isActive: false,
        },
      ],
    });
    const wrapper = mountView(createTestPinia(), createTestRouter());
    await flushPromises();

    await wrapper.find('.members-repo-button').trigger('click');
    await flushPromises();

    // 全量用户翻页加载完成，停用成员保留展示并标注
    expect(checkboxesByClass(wrapper, 'member-select')).toHaveLength(101);
    expect(wrapper.text()).toContain('已停用（保存后将移除）');
    expect(wrapper.find('.members-save').attributes('disabled')).toBeUndefined();

    await wrapper.find('.members-save').trigger('click');
    await flushPromises();

    const body = mocks.memberPuts[0] as {
      members: { userId: string; isMerger: boolean }[];
    };
    expect(body.members).toEqual([{ userId: PAGE2_ID, isMerger: true }]);
    expect(body.members.some((member) => member.userId === INACTIVE_ID)).toBe(false);
  });
});

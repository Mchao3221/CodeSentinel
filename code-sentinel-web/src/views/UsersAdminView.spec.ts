/**
 * 用户管理页测试：新建用户、停用用户与重置密码。
 */
import { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia, type Pinia } from 'pinia';
import ElementPlus, { ElMessageBox, type MessageBoxData } from 'element-plus';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

import { api, resetCsrfToken } from '../api/client';
import UsersAdminView from './UsersAdminView.vue';

/** 被管理用户主键 */
const USER_ID = '55555555-5555-4555-8555-555555555555';

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

/** 用户接口模拟记录 */
interface UserApiMocks {
  /** 新建用户请求体 */
  created: Record<string, unknown>[];
  /** 修改用户请求体 */
  patched: Record<string, unknown>[];
  /** 用户列表读取次数 */
  listCalls: number;
}

/**
 * 构造用户管理页所需的接口数据，停用后列表返回停用状态。
 * @returns 记录到的请求体与调用次数
 */
function mockUserApis(): UserApiMocks {
  const mocks: UserApiMocks = { created: [], patched: [], listCalls: 0 };
  let user = {
    id: USER_ID,
    username: 'bob',
    displayName: 'Bob',
    email: null,
    isAdmin: false,
    isActive: true,
    mustChangePassword: true,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };

  mockAdapter((config) => {
    const method = (config.method ?? 'get').toLowerCase();
    const url = config.url ?? '';

    if (url === '/api/auth/csrf') {
      return { status: 200, data: { token: 'csrf-test' } };
    }

    if (url === '/api/users' && method === 'get') {
      mocks.listCalls += 1;
      return {
        status: 200,
        data: { items: [user], total: 1, page: 1, pageSize: 20 },
      };
    }

    if (url === '/api/users' && method === 'post') {
      const body = JSON.parse(String(config.data)) as Record<string, unknown>;
      mocks.created.push(body);
      return { status: 201, data: { ...user, ...body, id: USER_ID } };
    }

    if (url === `/api/users/${USER_ID}` && method === 'patch') {
      const body = JSON.parse(String(config.data)) as Record<string, unknown>;
      mocks.patched.push(body);
      user = { ...user, ...body };
      return { status: 200, data: user };
    }

    if (url === `/api/users/${USER_ID}/reset-password` && method === 'post') {
      return { status: 200, data: { password: 'TempPass123' } };
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
 * 挂载用户管理页。
 * @param pinia 测试 Pinia
 * @param router 测试路由
 * @returns 组件包装器
 */
function mountView(pinia: Pinia, router: Router): VueWrapper {
  return mount(UsersAdminView, {
    global: { plugins: [pinia, ElementPlus, router] },
  });
}

/**
 * 创建测试用路由。
 */
function createTestRouter(): Router {
  return createRouter({ history: createMemoryHistory(), routes: [] });
}

describe('用户管理视图', () => {
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

  it('新建用户提交用户名、显示名与初始密码', async () => {
    const mocks = mockUserApis();
    const wrapper = mountView(createTestPinia(), createTestRouter());
    await flushPromises();

    await wrapper.find('.create-user-button').trigger('click');
    await flushPromises();

    await wrapper.find('input[placeholder="请输入用户名"]').setValue('carol');
    await wrapper.find('input[placeholder="请输入显示名"]').setValue('Carol');
    await wrapper.find('input[placeholder="请输入初始密码"]').setValue('Passw0rd!');
    await wrapper.find('.user-dialog-save').trigger('click');
    await flushPromises();

    expect(mocks.created).toHaveLength(1);
    expect(mocks.created[0]).toMatchObject({
      username: 'carol',
      displayName: 'Carol',
      password: 'Passw0rd!',
      isAdmin: false,
    });
  });

  it('确认停用用户后提交 isActive=false 并刷新列表', async () => {
    const mocks = mockUserApis();
    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as unknown as MessageBoxData);
    const wrapper = mountView(createTestPinia(), createTestRouter());
    await flushPromises();

    expect(mocks.listCalls).toBe(1);
    await wrapper.find('.deactivate-user-button').trigger('click');
    await flushPromises();

    expect(mocks.patched).toHaveLength(1);
    expect(mocks.patched[0]).toEqual({ isActive: false });
    expect(mocks.listCalls).toBe(2);
    expect(wrapper.text()).toContain('已停用');
  });

  it('重置密码后展示一次性密码提示', async () => {
    mockUserApis();
    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as unknown as MessageBoxData);
    const wrapper = mountView(createTestPinia(), createTestRouter());
    await flushPromises();

    await wrapper.find('.reset-password-button').trigger('click');
    await flushPromises();

    expect(wrapper.find('.password-dialog').text()).toContain('TempPass123');
    expect(wrapper.find('.password-dialog').text()).toContain('仅显示一次');
  });
});

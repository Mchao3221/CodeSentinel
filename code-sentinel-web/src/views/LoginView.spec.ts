/**
 * 登录页测试：登录跳转、强制改密、错误提示与开放重定向防护。
 */
import { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia, type Pinia } from 'pinia';
import ElementPlus, { ElMessage } from 'element-plus';
import { createMemoryHistory, createRouter, type RouteRecordRaw, type Router } from 'vue-router';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

import { api, resetCsrfToken } from '../api/client';
import type { AuthUser } from '../api/types';
import LoginView from './LoginView.vue';

/** 登录成功返回的用户 */
const AUTH_USER: AuthUser = {
  id: '9e87b9a2-1c3f-4d6a-8b2f-000000000010',
  username: 'bob',
  displayName: 'Bob',
  isAdmin: false,
  mustChangePassword: false,
};

/** 模拟响应 */
interface MockResult {
  /** HTTP 状态码 */
  status: number;
  /** 响应体 */
  data: unknown;
}

/** 登录接口模拟记录 */
interface LoginApiMocks {
  /** 登录请求体 */
  logins: Record<string, unknown>[];
  /** 改密请求体 */
  changePasswords: Record<string, unknown>[];
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
 * 构造登录与改密接口数据。
 * @param options 可覆盖登录接口的响应
 * @returns 记录到的请求体
 */
function mockAuthApis(options: { login?: MockResult } = {}): LoginApiMocks {
  const mocks: LoginApiMocks = { logins: [], changePasswords: [] };

  mockAdapter((config) => {
    const method = (config.method ?? 'get').toLowerCase();
    const url = config.url ?? '';

    if (url === '/api/auth/csrf') {
      return { status: 200, data: { token: 'csrf-test' } };
    }

    if (url === '/api/auth/login' && method === 'post') {
      mocks.logins.push(JSON.parse(String(config.data)) as Record<string, unknown>);
      return options.login ?? { status: 200, data: AUTH_USER };
    }

    if (url === '/api/auth/change-password' && method === 'post') {
      mocks.changePasswords.push(JSON.parse(String(config.data)) as Record<string, unknown>);
      return { status: 200, data: {} };
    }

    return { status: 404, data: { code: 'NOT_FOUND', message: url, traceId: 'trace' } };
  });

  return mocks;
}

/** 登录页测试所需的路由表 */
const ROUTES: RouteRecordRaw[] = [
  { path: '/login', component: { template: '<div />' } },
  { path: '/reviews', component: { template: '<div />' } },
  { path: '/reviews/:id', component: { template: '<div />' } },
];

/**
 * 注册测试用的 Pinia。
 */
function createTestPinia(): Pinia {
  const pinia = createPinia();
  setActivePinia(pinia);
  return pinia;
}

/**
 * 创建测试用路由。
 */
function createTestRouter(): Router {
  return createRouter({ history: createMemoryHistory(), routes: ROUTES });
}

/**
 * 按文本查找按钮。
 * @param wrapper 组件包装器
 * @param text 按钮文本
 * @returns 按钮包装器
 */
function findButton(wrapper: VueWrapper, text: string): ReturnType<VueWrapper['find']> {
  const button = wrapper.findAll('button').find((item) => item.text() === text);
  if (button === undefined) {
    throw new Error(`未找到按钮：${text}`);
  }
  return button;
}

/**
 * 挂载登录页并定位到带 redirect 查询参数的登录路由。
 * @param redirect 回跳地址，省略时无回跳
 * @returns 包装器、路由与 replace 间谍
 */
async function mountLoginView(redirect?: string): Promise<{
  wrapper: VueWrapper;
  router: Router;
  replaceSpy: ReturnType<typeof vi.spyOn>;
}> {
  const router = createTestRouter();
  await router.push(redirect === undefined ? '/login' : { path: '/login', query: { redirect } });
  const replaceSpy = vi.spyOn(router, 'replace').mockResolvedValue(undefined as never);
  const wrapper = mount(LoginView, {
    global: { plugins: [createTestPinia(), ElementPlus, router] },
  });
  await flushPromises();
  return { wrapper, router, replaceSpy };
}

/**
 * 填写登录表单并提交。
 * @param wrapper 组件包装器
 */
async function submitLogin(wrapper: VueWrapper): Promise<void> {
  await wrapper.find('input[autocomplete="username"]').setValue('bob');
  await wrapper.find('input[autocomplete="current-password"]').setValue('Passw0rd!');
  await wrapper.find('form').trigger('submit');
  await flushPromises();
}

describe('登录视图', () => {
  beforeAll(() => {
    // Element Plus 组件依赖 ResizeObserver，jsdom 未实现
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

  it('登录成功后跳转到 redirect 指定的地址', async () => {
    mockAuthApis();
    const { wrapper, replaceSpy } = await mountLoginView('/reviews/123');

    await submitLogin(wrapper);

    expect(replaceSpy).toHaveBeenCalledWith('/reviews/123');
  });

  it('需强制改密时弹出不可关闭弹窗，提交新密码后进入系统', async () => {
    const mocks = mockAuthApis({
      login: { status: 200, data: { ...AUTH_USER, mustChangePassword: true } },
    });
    const { wrapper, replaceSpy } = await mountLoginView('/reviews');

    await wrapper.find('input[autocomplete="username"]').setValue('bob');
    await wrapper.find('input[autocomplete="current-password"]').setValue('OldPass123');
    await wrapper.find('form').trigger('submit');
    await flushPromises();

    // 弹窗出现且不可关闭（无关闭按钮）
    expect(wrapper.text()).toContain('首次登录请修改密码');
    expect(wrapper.find('.el-dialog__headerbtn').exists()).toBe(false);

    // 提交新密码
    const newPasswordInputs = wrapper.findAll('input[autocomplete="new-password"]');
    await newPasswordInputs[0].setValue('NewPass123!');
    await newPasswordInputs[1].setValue('NewPass123!');
    await findButton(wrapper, '保存并进入').trigger('click');
    await flushPromises();

    expect(mocks.changePasswords).toEqual([
      { currentPassword: 'OldPass123', newPassword: 'NewPass123!' },
    ]);
    expect(replaceSpy).toHaveBeenCalledWith('/reviews');
  });

  it('密码错误时展示后端信息并停留原页', async () => {
    mockAuthApis({
      login: {
        status: 401,
        data: { code: 'INVALID_CREDENTIALS', message: '用户名或密码错误', traceId: 'trace' },
      },
    });
    const errorSpy = vi.spyOn(ElMessage, 'error').mockImplementation(() => undefined as never);
    const { wrapper, replaceSpy } = await mountLoginView('/reviews');

    await submitLogin(wrapper);

    expect(errorSpy).toHaveBeenCalledWith('用户名或密码错误');
    expect(replaceSpy).not.toHaveBeenCalled();
  });

  it.each(['//evil.com', '\\evil.com', 'http://evil.com', ''])(
    '不安全的 redirect %s 回退到 /reviews',
    async (redirect) => {
      mockAuthApis();
      const { wrapper, replaceSpy } = await mountLoginView(redirect);

      await submitLogin(wrapper);

      expect(replaceSpy).toHaveBeenCalledWith('/reviews');
    },
  );
});

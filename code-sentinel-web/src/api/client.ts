import axios, { AxiosError, type InternalAxiosRequestConfig } from 'axios';
import { getActivePinia } from 'pinia';

import router from '../router';
import { useAuthStore } from '../stores/auth';
import type { ApiErrorBody } from './types';

declare module 'axios' {
  export interface AxiosRequestConfig {
    /** 为 true 时 401 不触发全局跳转，由调用方自行处理 */
    skipAuthRedirect?: boolean;
  }
}

/** 与后端约定的 CSRF 请求头 */
const CSRF_HEADER = 'X-CSRF-TOKEN';

/** CSRF 令牌接口 */
const CSRF_PATH = '/api/auth/csrf';

/** 无需 CSRF 的写请求，与后端豁免规则保持一致 */
const CSRF_EXEMPT_PATHS = ['/auth/login', '/setup/admin'];

/** 带重试标记的请求配置 */
type RetryableConfig = InternalAxiosRequestConfig & { csrfRetried?: boolean };

/** 全局 axios 实例：自动携带会话 Cookie */
export const api = axios.create({ withCredentials: true });

let csrfToken: string | null = null;
let csrfRequest: Promise<string> | null = null;

/**
 * 清空缓存的 CSRF 令牌。
 * 登录、登出、改密都会重建会话，令牌随之失效，必须重新获取。
 */
export function resetCsrfToken(): void {
  csrfToken = null;
  csrfRequest = null;
}

/**
 * 获取 CSRF 令牌，并发请求共用同一次拉取。
 */
async function getCsrfToken(): Promise<string> {
  if (csrfToken !== null) {
    return csrfToken;
  }

  if (csrfRequest === null) {
    csrfRequest = api
      .get<{ token: string }>(CSRF_PATH)
      .then((response) => {
        csrfToken = response.data.token;
        return response.data.token;
      })
      .finally(() => {
        csrfRequest = null;
      });
  }

  return csrfRequest;
}

/**
 * 判断写请求是否免于 CSRF 校验。
 */
function isCsrfExempt(url: string | undefined): boolean {
  if (!url) {
    return false;
  }

  return CSRF_EXEMPT_PATHS.some((path) => url.includes(path));
}

/**
 * 判断请求是否会改动数据。
 */
function isMutating(config: InternalAxiosRequestConfig): boolean {
  const method = (config.method ?? 'get').toLowerCase();
  return method !== 'get' && method !== 'head' && method !== 'options';
}

api.interceptors.request.use(async (config) => {
  if (!isMutating(config) || isCsrfExempt(config.url)) {
    return config;
  }

  config.headers.set(CSRF_HEADER, await getCsrfToken());
  return config;
});

/**
 * 401 时清空登录状态并跳转登录页，保留当前路径供登录后回跳。
 */
async function handleUnauthorized(): Promise<void> {
  // 拦截器可能在组件外触发，没有活动 Pinia 时只负责跳转
  if (getActivePinia() !== undefined) {
    useAuthStore().clear();
  }

  if (router.currentRoute.value.path !== '/login') {
    await router.replace({
      path: '/login',
      query: { redirect: router.currentRoute.value.fullPath },
    });
  }
}

api.interceptors.response.use(
  (response) => response,
  async (error: AxiosError<ApiErrorBody>) => {
    const config = error.config as RetryableConfig | undefined;
    const status = error.response?.status;
    const code = error.response?.data?.code;

    // CSRF 令牌失效时重新获取并重试一次，避免会话续期后的首次写操作失败
    if (status === 400 && code === 'CSRF_INVALID' && config && config.csrfRetried !== true) {
      config.csrfRetried = true;
      resetCsrfToken();
      return api.request(config);
    }

    if (status === 401 && config?.skipAuthRedirect !== true) {
      await handleUnauthorized();
    }

    return Promise.reject(error);
  },
);

/**
 * 接口错误提示工具：优先使用后端 message，并对同步限流做统一提示。
 */
import axios from 'axios';

import type { ApiErrorBody } from '../api/types';

/** 需要映射为“同步过于频繁”的错误码 */
const SYNC_LIMIT_CODES = new Set(['SYNC_THROTTLED', 'SYNC_IN_PROGRESS']);

/**
 * 提取后端错误信息，缺失时使用兜底文案。
 * @param error 捕获到的异常
 * @param fallback 兜底文案
 * @returns 可展示的错误信息
 */
export function apiErrorMessage(error: unknown, fallback: string): string {
  if (!axios.isAxiosError<ApiErrorBody>(error)) {
    return fallback;
  }

  const message = error.response?.data?.message;
  return typeof message === 'string' && message.length > 0 ? message : fallback;
}

/**
 * 刷新评审单失败的提示：429 节流统一文案，其余使用后端 message。
 * @param error 捕获到的异常
 * @returns 可展示的错误信息
 */
export function refreshErrorMessage(error: unknown): string {
  if (axios.isAxiosError<ApiErrorBody>(error) && error.response?.status === 429) {
    const code = error.response.data?.code;
    if (typeof code === 'string' && SYNC_LIMIT_CODES.has(code)) {
      return '同步过于频繁，请稍后重试';
    }
  }

  return apiErrorMessage(error, '刷新失败，请稍后重试');
}

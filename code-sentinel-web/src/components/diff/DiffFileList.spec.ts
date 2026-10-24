import { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import ElementPlus, { ElCheckbox } from 'element-plus';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';

import { api, resetCsrfToken } from '../../api/client';
import type { DiffFileList, DiffFileListItem, ReviewComment } from '../../api/types';
import DiffFileListView from './DiffFileList.vue';

/** 评审单主键 */
const REVIEW_ID = '55555555-5555-4555-8555-555555555555';

/** 模拟响应 */
interface MockResult {
  /** HTTP 状态码 */
  status: number;
  /** 响应体 */
  data: unknown;
}

/**
 * 把 axios 实例的适配器替换为内存实现。
 * @param handler 请求处理器
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
 * 构造文件摘要。
 * @param path 文件路径
 * @param viewed 是否已看过
 * @returns 文件摘要
 */
function makeFile(path: string, viewed: boolean): DiffFileListItem {
  return {
    path,
    oldPath: null,
    status: 'MODIFIED',
    additions: 1,
    deletions: 1,
    isBinary: false,
    isTooLarge: false,
    viewed,
  };
}

/**
 * 构造五个文件，前三个已看过。
 * @returns 文件列表响应
 */
function makeFiles(): DiffFileList {
  const items = [
    makeFile('a1.txt', true),
    makeFile('a2.txt', true),
    makeFile('a3.txt', true),
    makeFile('a4.txt', false),
    makeFile('a5.txt', false),
  ];

  return { items, total: 5, viewedCount: 3, fileCount: 5, truncated: false };
}

/** 挂载选项 */
interface MountOptions {
  /** 文件列表响应 */
  files?: DiffFileList;
  /** 是否历史版本 */
  isHistorical?: boolean;
  /** 评论 */
  comments?: ReviewComment[];
}

/**
 * 挂载 DiffFileList 并返回包装器与已看过调用记录。
 * @param options 挂载选项
 * @returns 包装器与调用记录
 */
function mountList(options: MountOptions = {}): {
  wrapper: VueWrapper;
  viewedCalls: { path: string; viewed: boolean }[];
} {
  const viewedCalls: { path: string; viewed: boolean }[] = [];

  mockAdapter((config) => {
    const url = config.url ?? '';
    const method = (config.method ?? 'get').toLowerCase();

    if (url === '/api/auth/csrf') {
      return { status: 200, data: { token: 'csrf-test' } };
    }

    if (url === `/api/reviews/${REVIEW_ID}/files` && method === 'get') {
      return { status: 200, data: options.files ?? makeFiles() };
    }

    if (url === `/api/reviews/${REVIEW_ID}/comments` && method === 'get') {
      return { status: 200, data: { items: options.comments ?? [] } };
    }

    if (url === `/api/reviews/${REVIEW_ID}/files/viewed` && method === 'put') {
      const body = JSON.parse(String(config.data)) as { path: string; viewed: boolean };
      viewedCalls.push({ path: body.path, viewed: body.viewed });
      return { status: 200, data: { path: body.path, viewed: body.viewed } };
    }

    return { status: 404, data: { code: 'NOT_FOUND', message: url, traceId: 't' } };
  });

  const wrapper = mount(DiffFileListView, {
    props: {
      reviewId: REVIEW_ID,
      revisionId: '',
      isHistorical: options.isHistorical ?? false,
    },
    global: { plugins: [ElementPlus] },
  });

  return { wrapper, viewedCalls };
}

describe('DiffFileList', () => {
  beforeAll(() => {
    globalThis.ResizeObserver = class {
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
    } as unknown as typeof ResizeObserver;
  });

  beforeEach(() => {
    resetCsrfToken();
  });

  it('根据已看过标记展示 3/5 进度，切换时调用接口', async () => {
    const { wrapper, viewedCalls } = mountList();
    await flushPromises();

    expect(wrapper.find('.diff-file-progress').text()).toBe('已看过 3/5');

    // 第 4 个文件（未看过）勾选后乐观更新进度并调用接口
    const checkbox = wrapper.findAllComponents(ElCheckbox)[3];
    checkbox.vm.$emit('update:modelValue', true);
    await flushPromises();

    expect(wrapper.find('.diff-file-progress').text()).toBe('已看过 4/5');
    expect(viewedCalls).toEqual([{ path: 'a4.txt', viewed: true }]);
  });

  it('截断时展示提示', async () => {
    const files = makeFiles();
    const { wrapper } = mountList({ files: { ...files, truncated: true } });
    await flushPromises();

    expect(wrapper.find('.diff-file-truncated').exists()).toBe(true);
  });

  it('历史版本展示只读横幅且勾选不调用接口', async () => {
    const { wrapper, viewedCalls } = mountList({ isHistorical: true });
    await flushPromises();

    expect(wrapper.find('.diff-historical-banner').text()).toContain('正在查看历史版本，仅可查看');

    const checkbox = wrapper.findAllComponents(ElCheckbox)[0];
    expect(checkbox.props('disabled')).toBe(true);
    checkbox.vm.$emit('update:modelValue', true);
    await flushPromises();

    expect(viewedCalls).toEqual([]);
  });
});

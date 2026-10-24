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

  it('行内新增评论后立即插入本地列表，无需重新加载', async () => {
    const commentCalls = { count: 0 };

    mockAdapter((config) => {
      const url = config.url ?? '';
      const method = (config.method ?? 'get').toLowerCase();

      if (url === '/api/auth/csrf') {
        return { status: 200, data: { token: 'csrf-test' } };
      }

      if (url === `/api/reviews/${REVIEW_ID}/files` && method === 'get') {
        return {
          status: 200,
          data: { items: [makeFile('a.txt', false)], total: 1, viewedCount: 0, fileCount: 1, truncated: false },
        };
      }

      if (url === `/api/reviews/${REVIEW_ID}/comments` && method === 'get') {
        commentCalls.count += 1;
        return { status: 200, data: { items: [] } };
      }

      if (url === `/api/reviews/${REVIEW_ID}/comments` && method === 'post') {
        const body = JSON.parse(String(config.data)) as {
          path: string;
          side: ReviewComment['side'];
          line: number;
          body: string;
        };
        return {
          status: 201,
          data: {
            id: 'new-comment-1',
            revisionId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
            authorId: '22222222-2222-4222-8222-222222222222',
            authorDisplayName: '当前用户',
            path: body.path,
            side: body.side,
            line: body.line,
            body: body.body,
            parentId: null,
            status: 'OPEN',
            resolvedBy: null,
            resolvedAt: null,
            createdAt: '2026-10-24T01:00:00Z',
            updatedAt: '2026-10-24T01:00:00Z',
          },
        };
      }

      if (url === `/api/reviews/${REVIEW_ID}/diff` && method === 'get') {
        return {
          status: 200,
          data: {
            path: 'a.txt',
            oldPath: null,
            status: 'MODIFIED',
            additions: 1,
            deletions: 0,
            isBinary: false,
            isTooLarge: false,
            hunks: [
              {
                oldStart: 1,
                oldLines: 0,
                newStart: 1,
                newLines: 1,
                lines: [{ type: 'ADD', oldLine: null, newLine: 1, text: 'added' }],
              },
            ],
          },
        };
      }

      if (url === `/api/reviews/${REVIEW_ID}/blob` && method === 'get') {
        return { status: 200, data: { path: 'a.txt', content: '', isBinary: false, isTooLarge: false } };
      }

      return { status: 404, data: { code: 'NOT_FOUND', message: url, traceId: 't' } };
    });

    const wrapper = mount(DiffFileListView, {
      props: {
        reviewId: REVIEW_ID,
        revisionId: '',
        isHistorical: false,
        currentUserId: '22222222-2222-4222-8222-222222222222',
        isAdmin: false,
        isMerger: false,
      },
      global: { plugins: [ElementPlus] },
    });
    await flushPromises();

    await wrapper.find('.diff-file-toggle').trigger('click');
    await flushPromises();
    expect(wrapper.find('.comment-thread').exists()).toBe(false);

    await wrapper.find('.diff-comment-add').trigger('click');
    await wrapper.find('.comment-composer textarea').setValue('新的行内评论');
    await wrapper.find('.comment-composer-submit').trigger('click');
    await flushPromises();

    expect(wrapper.find('.comment-thread').exists()).toBe(true);
    expect(wrapper.find('.comment-body').text()).toContain('新的行内评论');
    // 仅在首次加载时请求评论列表，创建后不再整页刷新
    expect(commentCalls.count).toBe(1);
  });

  it('切换版本后同一路径重新请求并渲染新内容', async () => {
    const diffCalls: { revisionId?: string }[] = [];

    mockAdapter((config) => {
      const url = config.url ?? '';
      const method = (config.method ?? 'get').toLowerCase();
      const params = (config.params ?? {}) as { path?: string; revisionId?: string };

      if (url === '/api/auth/csrf') {
        return { status: 200, data: { token: 'csrf-test' } };
      }

      if (url === `/api/reviews/${REVIEW_ID}/files` && method === 'get') {
        return {
          status: 200,
          data: { items: [makeFile('a.txt', false)], total: 1, viewedCount: 0, fileCount: 1, truncated: false },
        };
      }

      if (url === `/api/reviews/${REVIEW_ID}/comments` && method === 'get') {
        return { status: 200, data: { items: [] } };
      }

      if (url === `/api/reviews/${REVIEW_ID}/diff` && method === 'get') {
        diffCalls.push({ revisionId: params.revisionId });
        const text = params.revisionId === 'rev-b' ? 'content b' : 'content a';
        return {
          status: 200,
          data: {
            path: 'a.txt',
            oldPath: null,
            status: 'MODIFIED',
            additions: 1,
            deletions: 0,
            isBinary: false,
            isTooLarge: false,
            hunks: [
              {
                oldStart: 1,
                oldLines: 1,
                newStart: 1,
                newLines: 1,
                lines: [{ type: 'ADD', oldLine: null, newLine: 1, text }],
              },
            ],
          },
        };
      }

      if (url === `/api/reviews/${REVIEW_ID}/blob` && method === 'get') {
        return { status: 200, data: { path: 'a.txt', content: '', isBinary: false, isTooLarge: false } };
      }

      return { status: 404, data: { code: 'NOT_FOUND', message: url, traceId: 't' } };
    });

    const wrapper = mount(DiffFileListView, {
      props: { reviewId: REVIEW_ID, revisionId: 'rev-a', isHistorical: false },
      global: { plugins: [ElementPlus] },
    });
    await flushPromises();

    // 版本 A 展开渲染内容 A
    await wrapper.find('.diff-file-toggle').trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('content a');

    // 切换到版本 B 后同一路径重新挂载，展开渲染内容 B
    await wrapper.setProps({ revisionId: 'rev-b' });
    await flushPromises();
    await wrapper.find('.diff-file-toggle').trigger('click');
    await flushPromises();

    expect(wrapper.text()).toContain('content b');
    expect(wrapper.text()).not.toContain('content a');
    expect(diffCalls.some((call) => call.revisionId === 'rev-b')).toBe(true);
  });
});

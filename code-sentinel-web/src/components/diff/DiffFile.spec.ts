import { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import ElementPlus, { ElCheckbox } from 'element-plus';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';

import { api, resetCsrfToken } from '../../api/client';
import type {
  DiffFile as DiffFileData,
  DiffFileListItem,
  ReviewComment,
} from '../../api/types';
import DiffFileView from './DiffFile.vue';

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
 * @param overrides 覆盖项
 * @returns 文件摘要
 */
function makeFile(overrides: Partial<DiffFileListItem> = {}): DiffFileListItem {
  return {
    path: 'a.txt',
    oldPath: null,
    status: 'MODIFIED',
    additions: 2,
    deletions: 1,
    isBinary: false,
    isTooLarge: false,
    viewed: false,
    ...overrides,
  };
}

/**
 * 构造评论。
 * @param overrides 覆盖项
 * @returns 评论
 */
function makeComment(overrides: Partial<ReviewComment> = {}): ReviewComment {
  return {
    id: '11111111-1111-4111-8111-111111111111',
    revisionId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    authorId: '22222222-2222-4222-8222-222222222222',
    authorDisplayName: 'Alice',
    path: 'a.txt',
    side: 'NEW',
    line: 1,
    body: '这是一条评论',
    parentId: null,
    status: 'OPEN',
    resolvedBy: null,
    resolvedAt: null,
    createdAt: '2026-10-24T01:00:00Z',
    updatedAt: '2026-10-24T01:00:00Z',
    ...overrides,
  };
}

/**
 * 构造单文件 diff，默认含上下文、新增、删除三行。
 * @param overrides 覆盖项
 * @returns 单文件 diff
 */
function makeDiff(overrides: Partial<DiffFileData> = {}): DiffFileData {
  return {
    path: 'a.txt',
    oldPath: null,
    status: 'MODIFIED',
    additions: 1,
    deletions: 1,
    isBinary: false,
    isTooLarge: false,
    hunks: [
      {
        oldStart: 1,
        oldLines: 3,
        newStart: 1,
        newLines: 3,
        lines: [
          { type: 'CONTEXT', oldLine: 1, newLine: 1, text: 'context line' },
          { type: 'ADD', oldLine: null, newLine: 2, text: 'added line' },
          { type: 'DELETE', oldLine: 2, newLine: null, text: 'removed line' },
        ],
      },
    ],
    ...overrides,
  };
}

/** 挂载选项 */
interface MountOptions {
  /** 文件摘要 */
  file?: DiffFileListItem;
  /** 评论 */
  comments?: ReviewComment[];
  /** 是否历史版本 */
  isHistorical?: boolean;
  /** 单文件 diff，null 表示 404 */
  diff?: DiffFileData | null;
  /** OLD 侧 blob 是否二进制 */
  blobOldBinary?: boolean;
  /** NEW 侧 blob 是否二进制 */
  blobNewBinary?: boolean;
}

/**
 * 挂载 DiffFile 并返回包装器与接口调用计数。
 * @param options 挂载选项
 * @returns 包装器与调用计数
 */
function mountDiffFile(options: MountOptions = {}): {
  wrapper: VueWrapper;
  diffCalls: { count: number };
  blobCalls: { count: number };
} {
  const file = options.file ?? makeFile();
  const diff = options.diff ?? makeDiff();
  const diffCalls = { count: 0 };
  const blobCalls = { count: 0 };

  mockAdapter((config) => {
    const url = config.url ?? '';
    const params = (config.params ?? {}) as { path?: string; side?: string };

    if (url === `/api/reviews/${REVIEW_ID}/diff`) {
      diffCalls.count += 1;
      if (diff === null) {
        return { status: 404, data: { code: 'NOT_FOUND', message: 'x', traceId: 't' } };
      }

      return { status: 200, data: diff };
    }

    if (url === `/api/reviews/${REVIEW_ID}/blob`) {
      blobCalls.count += 1;
      const isBinary = params.side === 'OLD' ? options.blobOldBinary : options.blobNewBinary;
      return {
        status: 200,
        data: { path: params.path ?? '', content: '', isBinary: isBinary ?? false, isTooLarge: false },
      };
    }

    return { status: 404, data: { code: 'NOT_FOUND', message: url, traceId: 't' } };
  });

  const wrapper = mount(DiffFileView, {
    props: {
      reviewId: REVIEW_ID,
      revisionId: '',
      file,
      comments: options.comments ?? [],
      isHistorical: options.isHistorical ?? false,
      viewedPending: false,
    },
    global: { plugins: [ElementPlus] },
  });

  return { wrapper, diffCalls, blobCalls };
}

describe('DiffFile', () => {
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

  it('渲染旧行号、新行号与 +/- 前缀标记', async () => {
    const { wrapper } = mountDiffFile();

    await wrapper.find('.diff-file-toggle').trigger('click');
    await flushPromises();

    const oldNums = wrapper.findAll('.diff-line-num-old').map((node) => node.text());
    const newNums = wrapper.findAll('.diff-line-num-new').map((node) => node.text());
    const markers = wrapper
      .findAll('.diff-line-marker')
      .map((node) => node.element.textContent);

    expect(oldNums).toEqual(['1', '', '2']);
    expect(newNums).toEqual(['1', '2', '']);
    expect(markers).toEqual([' ', '+', '-']);
    expect(wrapper.find('.diff-line-text').text()).toContain('context line');
  });

  it('首次展开按需拉取 diff，第二次展开复用缓存', async () => {
    const { wrapper, diffCalls } = mountDiffFile();

    expect(diffCalls.count).toBe(0);

    await wrapper.find('.diff-file-toggle').trigger('click');
    await flushPromises();
    expect(diffCalls.count).toBe(1);

    await wrapper.find('.diff-file-toggle').trigger('click');
    await flushPromises();
    await wrapper.find('.diff-file-toggle').trigger('click');
    await flushPromises();

    expect(diffCalls.count).toBe(1);
  });

  it('在“已过期”折叠区渲染过期评论', async () => {
    const outdated = makeComment({
      id: 'outdated-1',
      status: 'OUTDATED',
      line: 5,
      body: '过期评论内容',
    });
    const { wrapper } = mountDiffFile({ comments: [outdated] });

    await wrapper.find('.diff-file-toggle').trigger('click');
    await flushPromises();

    const toggle = wrapper.find('.diff-outdated-toggle');
    expect(toggle.exists()).toBe(true);
    expect(toggle.text()).toContain('已过期');
    expect(wrapper.find('.diff-outdated-body').exists()).toBe(false);

    await toggle.trigger('click');
    await flushPromises();
    expect(wrapper.find('.diff-outdated-body').text()).toBe('过期评论内容');
  });

  it('高亮失败或文件过大时回退纯文本', async () => {
    const { wrapper } = mountDiffFile({
      file: makeFile({ path: 'code.ts' }),
      diff: makeDiff({ path: 'code.ts' }),
      blobNewBinary: true,
    });

    await wrapper.find('.diff-file-toggle').trigger('click');
    await flushPromises();

    expect(wrapper.find('.diff-token').exists()).toBe(false);
    const lineTexts = wrapper.findAll('.diff-line-text').map((node) => node.text());
    expect(lineTexts).toContain('added line');
  });

  it('二进制文件显示“不支持预览”且无 hunk', async () => {
    const { wrapper } = mountDiffFile({
      diff: makeDiff({ isBinary: true, hunks: [] }),
    });

    await wrapper.find('.diff-file-toggle').trigger('click');
    await flushPromises();

    expect(wrapper.text()).toContain('不支持预览');
    expect(wrapper.find('.diff-hunk').exists()).toBe(false);
  });

  it('超大文件显示“文件过大，未展示 diff”且无 hunk', async () => {
    const { wrapper } = mountDiffFile({
      diff: makeDiff({ isTooLarge: true, hunks: [] }),
    });

    await wrapper.find('.diff-file-toggle').trigger('click');
    await flushPromises();

    expect(wrapper.text()).toContain('文件过大，未展示 diff');
    expect(wrapper.find('.diff-hunk').exists()).toBe(false);
  });

  it('历史版本下禁用已看过复选框', async () => {
    const { wrapper } = mountDiffFile({ isHistorical: true });

    const checkbox = wrapper.findComponent(ElCheckbox);
    expect(checkbox.props('disabled')).toBe(true);
  });
});

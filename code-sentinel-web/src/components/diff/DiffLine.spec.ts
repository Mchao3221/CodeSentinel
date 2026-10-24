import { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import ElementPlus from 'element-plus';
import { beforeEach, describe, expect, it } from 'vitest';

import { api, resetCsrfToken } from '../../api/client';
import type { DiffLine, ReviewComment } from '../../api/types';
import DiffLineView from './DiffLine.vue';

/** 评审单主键 */
const REVIEW_ID = '55555555-5555-4555-8555-555555555555';

/** 当前用户主键 */
const CURRENT_USER_ID = '22222222-2222-4222-8222-222222222222';

/** 文件路径 */
const PATH = 'src/a.ts';

/** 模拟响应 */
interface MockResult {
  /** HTTP 状态码 */
  status: number;
  /** 响应体 */
  data: unknown;
}

/** 捕获到的创建请求 */
interface CapturedRequest {
  /** 请求体 */
  body: Record<string, unknown>;
}

/**
 * 把 axios 实例的适配器替换为内存实现。
 * @param handler 请求处理器
 * @param requests 请求记录
 */
function mockAdapter(
  handler: (config: InternalAxiosRequestConfig) => MockResult,
  requests: CapturedRequest[],
): void {
  const adapter: AxiosAdapter = async (config) => {
    if (config.url === '/api/auth/csrf') {
      return {
        data: { token: 'csrf-test' },
        status: 200,
        statusText: 'OK',
        headers: {},
        config,
      };
    }

    if ((config.method ?? 'get').toLowerCase() === 'post') {
      requests.push({
        body: config.data ? (JSON.parse(String(config.data)) as Record<string, unknown>) : {},
      });
    }

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
 * 构造创建成功的评论。
 * @param config 请求配置
 * @returns 评论
 */
function createdComment(config: InternalAxiosRequestConfig): ReviewComment {
  const body = JSON.parse(String(config.data)) as {
    path: string;
    side: ReviewComment['side'];
    line: number;
    body: string;
  };

  return {
    id: 'new-comment-1',
    revisionId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    authorId: CURRENT_USER_ID,
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
  };
}

/** 挂载选项 */
interface MountOptions {
  /** diff 行 */
  line: DiffLine;
  /** 是否历史版本 */
  isHistorical?: boolean;
  /** 挂在该行上的评论 */
  comments?: ReviewComment[];
}

/**
 * 挂载 diff 行。
 * @param options 挂载选项
 * @returns 包装器与创建请求记录
 */
function mountLine(options: MountOptions): {
  wrapper: VueWrapper;
  requests: CapturedRequest[];
} {
  const requests: CapturedRequest[] = [];
  mockAdapter(
    (config) => ({ status: 201, data: createdComment(config) }),
    requests,
  );

  const wrapper = mount(DiffLineView, {
    props: {
      line: options.line,
      tokens: null,
      comments: options.comments ?? [],
      reviewId: REVIEW_ID,
      path: PATH,
      isHistorical: options.isHistorical ?? false,
      currentUserId: CURRENT_USER_ID,
      isAdmin: false,
      isMerger: false,
    },
    global: { plugins: [ElementPlus] },
  });

  return { wrapper, requests };
}

describe('DiffLine 行内评论', () => {
  beforeEach(() => {
    resetCsrfToken();
  });

  it('新增行点击 + 后以 NEW 侧行号创建评论', async () => {
    const { wrapper, requests } = mountLine({
      line: { type: 'ADD', oldLine: null, newLine: 2, text: 'added line' },
    });

    await wrapper.find('.diff-comment-add').trigger('click');
    await wrapper.find('.comment-composer textarea').setValue('新增行评论');
    await wrapper.find('.comment-composer-submit').trigger('click');
    await flushPromises();

    expect(requests[0].body).toEqual({
      path: PATH,
      side: 'NEW',
      line: 2,
      body: '新增行评论',
    });
    expect(wrapper.emitted('created')?.[0]?.[0]).toMatchObject({
      id: 'new-comment-1',
      side: 'NEW',
      line: 2,
    });
  });

  it('上下文行以 NEW 侧行号创建评论', async () => {
    const { wrapper, requests } = mountLine({
      line: { type: 'CONTEXT', oldLine: 4, newLine: 5, text: 'context line' },
    });

    await wrapper.find('.diff-comment-add').trigger('click');
    await wrapper.find('.comment-composer textarea').setValue('上下文评论');
    await wrapper.find('.comment-composer-submit').trigger('click');
    await flushPromises();

    expect(requests[0].body).toMatchObject({ side: 'NEW', line: 5 });
  });

  it('删除行以 OLD 侧旧行号创建评论', async () => {
    const { wrapper, requests } = mountLine({
      line: { type: 'DELETE', oldLine: 7, newLine: null, text: 'removed line' },
    });

    await wrapper.find('.diff-comment-add').trigger('click');
    await wrapper.find('.comment-composer textarea').setValue('删除行评论');
    await wrapper.find('.comment-composer-submit').trigger('click');
    await flushPromises();

    expect(requests[0].body).toMatchObject({ side: 'OLD', line: 7 });
  });

  it('历史版本不显示 + 按钮与输入框', () => {
    const { wrapper } = mountLine({
      line: { type: 'ADD', oldLine: null, newLine: 2, text: 'added line' },
      isHistorical: true,
    });

    expect(wrapper.find('.diff-comment-add').exists()).toBe(false);
  });
});

import { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import ElementPlus, { ElMessage, ElMessageBox, type MessageBoxData } from 'element-plus';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { api, resetCsrfToken } from '../../api/client';
import type { ReviewComment } from '../../api/types';
import CommentThreadView from './CommentThread.vue';

/** 评审单主键 */
const REVIEW_ID = '55555555-5555-4555-8555-555555555555';

/** 当前登录用户主键 */
const CURRENT_USER_ID = '22222222-2222-4222-8222-222222222222';

/** 其他用户主键 */
const OTHER_USER_ID = '33333333-3333-4333-8333-333333333333';

/** 评论主键 */
const COMMENT_ID = '11111111-1111-4111-8111-111111111111';

/** 模拟响应 */
interface MockResult {
  /** HTTP 状态码 */
  status: number;
  /** 响应体 */
  data: unknown;
}

/** 捕获到的写请求 */
interface CapturedRequest {
  /** HTTP 方法 */
  method: string;
  /** 请求地址 */
  url: string;
  /** 请求体 */
  body: Record<string, unknown>;
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
 * 构造评论。
 * @param overrides 覆盖项
 * @returns 评论
 */
function makeComment(overrides: Partial<ReviewComment> = {}): ReviewComment {
  return {
    id: COMMENT_ID,
    revisionId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    authorId: CURRENT_USER_ID,
    authorDisplayName: '当前用户',
    path: 'src/a.ts',
    side: 'NEW',
    line: 2,
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

/** 挂载选项 */
interface MountOptions {
  /** 当前评论 */
  comment?: ReviewComment;
  /** 同一行上的全部评论 */
  comments?: ReviewComment[];
  /** 是否管理员 */
  isAdmin?: boolean;
  /** 是否合并人 */
  isMerger?: boolean;
  /** 是否历史版本 */
  isHistorical?: boolean;
}

/**
 * 挂载评论线程并返回包装器与写请求记录。
 * @param handler 写请求响应工厂
 * @param options 挂载选项
 * @returns 包装器与写请求记录
 */
function mountThread(
  handler: (config: InternalAxiosRequestConfig) => MockResult,
  options: MountOptions = {},
): { wrapper: VueWrapper; requests: CapturedRequest[] } {
  const requests: CapturedRequest[] = [];

  mockAdapter((config) => {
    if (config.url === '/api/auth/csrf') {
      return { status: 200, data: { token: 'csrf-test' } };
    }

    const method = (config.method ?? 'get').toLowerCase();
    if (method !== 'get') {
      requests.push({
        method,
        url: config.url ?? '',
        body: config.data ? (JSON.parse(String(config.data)) as Record<string, unknown>) : {},
      });
    }

    return handler(config);
  });

  const comment = options.comment ?? makeComment();
  const wrapper = mount(CommentThreadView, {
    props: {
      reviewId: REVIEW_ID,
      comment,
      comments: options.comments ?? [comment],
      currentUserId: CURRENT_USER_ID,
      isAdmin: options.isAdmin ?? false,
      isMerger: options.isMerger ?? false,
      isHistorical: options.isHistorical ?? false,
    },
    global: { plugins: [ElementPlus] },
  });

  return { wrapper, requests };
}

/**
 * 成功响应的评论写请求。
 * @param config 请求配置
 * @param status 返回的状态
 * @returns 模拟响应
 */
function echoComment(
  config: InternalAxiosRequestConfig,
  status: ReviewComment['status'],
): MockResult {
  const body = config.data ? (JSON.parse(String(config.data)) as { body?: string }) : {};
  return {
    status: 200,
    data: makeComment({ body: body.body ?? '这是一条评论', status }),
  };
}

describe('CommentThread', () => {
  beforeEach(() => {
    resetCsrfToken();
    vi.restoreAllMocks();
    vi.spyOn(ElMessage, 'error').mockReturnValue(
      {} as unknown as ReturnType<typeof ElMessage.error>,
    );
  });

  it('渲染 Markdown 但移除 script 标签与事件属性', async () => {
    const { wrapper } = mountThread(
      () => ({ status: 200, data: makeComment() }),
      {
        comment: makeComment({
          body: '**加粗** <script>alert(1)</script><img src="x" onerror="alert(2)">',
        }),
      },
    );

    await flushPromises();

    const html = wrapper.find('.comment-body').html();
    expect(html).toContain('<strong>加粗</strong>');
    expect(html).not.toContain('<script');
    expect(html).not.toContain('onerror');
    expect(wrapper.find('.comment-body').text()).toContain('加粗');
  });

  it('仅作者可见编辑与删除，管理员可见删除但不可编辑', async () => {
    const own = mountThread(() => ({ status: 200, data: makeComment() }));
    expect(own.wrapper.find('.comment-action-edit').exists()).toBe(true);
    expect(own.wrapper.find('.comment-action-delete').exists()).toBe(true);

    const other = makeComment({ authorId: OTHER_USER_ID, authorDisplayName: '其他人' });
    const otherThread = mountThread(() => ({ status: 200, data: other }), { comment: other });
    expect(otherThread.wrapper.find('.comment-action-edit').exists()).toBe(false);
    expect(otherThread.wrapper.find('.comment-action-delete').exists()).toBe(false);

    const adminThread = mountThread(() => ({ status: 200, data: other }), {
      comment: other,
      isAdmin: true,
    });
    expect(adminThread.wrapper.find('.comment-action-edit').exists()).toBe(false);
    expect(adminThread.wrapper.find('.comment-action-delete').exists()).toBe(true);
  });

  it('回复时提交 parentId', async () => {
    const { wrapper, requests } = mountThread((config) => ({
      status: 201,
      data: makeComment({
        id: 'reply-1',
        parentId: COMMENT_ID,
        body: (JSON.parse(String(config.data)) as { body: string }).body,
        authorDisplayName: '当前用户',
      }),
    }));

    await wrapper.find('.comment-action-reply').trigger('click');
    await wrapper.find('.comment-composer textarea').setValue('回复内容');
    await wrapper.find('.comment-composer-submit').trigger('click');
    await flushPromises();

    expect(requests).toHaveLength(1);
    expect(requests[0].method).toBe('post');
    expect(requests[0].url).toBe(`/api/reviews/${REVIEW_ID}/comments`);
    expect(requests[0].body).toMatchObject({
      path: 'src/a.ts',
      side: 'NEW',
      line: 2,
      body: '回复内容',
      parentId: COMMENT_ID,
    });
    expect(wrapper.emitted('created')?.[0]?.[0]).toMatchObject({ id: 'reply-1' });
    expect(wrapper.find('.comment-composer').exists()).toBe(false);
  });

  it('作者可解决与取消解决，并展示已解决标记', async () => {
    let resolved = false;
    const { wrapper, requests } = mountThread((config) => {
      resolved = (config.url ?? '').endsWith('/resolve');
      return { status: 200, data: makeComment({ status: resolved ? 'RESOLVED' : 'OPEN' }) };
    });

    expect(wrapper.find('.comment-resolved-badge').exists()).toBe(false);

    await wrapper.find('.comment-action-resolve').trigger('click');
    await flushPromises();

    expect(requests[0].url).toBe(`/api/comments/${COMMENT_ID}/resolve`);
    expect(wrapper.find('.comment-resolved-badge').exists()).toBe(true);
    expect(wrapper.find('.comment-action-resolve').text()).toBe('取消解决');

    await wrapper.find('.comment-action-resolve').trigger('click');
    await flushPromises();

    expect(requests[1].url).toBe(`/api/comments/${COMMENT_ID}/unresolve`);
    expect(wrapper.find('.comment-resolved-badge').exists()).toBe(false);
  });

  it('合并人可解决他人评论，普通成员不可', async () => {
    const other = makeComment({ authorId: OTHER_USER_ID, authorDisplayName: '其他人' });

    const plain = mountThread(() => ({ status: 200, data: other }), { comment: other });
    expect(plain.wrapper.find('.comment-action-resolve').exists()).toBe(false);

    const merger = mountThread(() => ({ status: 200, data: other }), {
      comment: other,
      isMerger: true,
    });
    expect(merger.wrapper.find('.comment-action-resolve').exists()).toBe(true);
  });

  it('解决失败时回滚状态并提示', async () => {
    const { wrapper } = mountThread(() => ({
      status: 500,
      data: { code: 'INTERNAL', message: '服务异常', traceId: 't' },
    }));

    await wrapper.find('.comment-action-resolve').trigger('click');
    await flushPromises();

    expect(wrapper.find('.comment-resolved-badge').exists()).toBe(false);
    expect(wrapper.find('.comment-action-resolve').text()).toBe('解决');
    expect(ElMessage.error).toHaveBeenCalledWith('服务异常');
  });

  it('编辑提交新版正文并更新展示', async () => {
    const { wrapper, requests } = mountThread((config) =>
      echoComment(config, 'OPEN'),
    );

    await wrapper.find('.comment-action-edit').trigger('click');
    await wrapper.find('.comment-edit textarea').setValue('修改后的内容');
    await wrapper.find('.comment-edit-save').trigger('click');
    await flushPromises();

    expect(requests[0].method).toBe('patch');
    expect(requests[0].url).toBe(`/api/comments/${COMMENT_ID}`);
    expect(requests[0].body).toEqual({ body: '修改后的内容' });
    expect(wrapper.find('.comment-body').text()).toContain('修改后的内容');
  });

  it('删除确认后调用接口并展示已删除占位且隐藏操作', async () => {
    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as unknown as MessageBoxData);
    const { wrapper, requests } = mountThread(() => ({
      status: 200,
      data: makeComment({ status: 'DELETED', body: '' }),
    }));

    await wrapper.find('.comment-action-delete').trigger('click');
    await flushPromises();

    expect(requests[0].method).toBe('delete');
    expect(requests[0].url).toBe(`/api/comments/${COMMENT_ID}`);
    expect(wrapper.find('.comment-body').text()).toBe('该评论已删除');
    expect(wrapper.find('.comment-action-edit').exists()).toBe(false);
    expect(wrapper.find('.comment-action-delete').exists()).toBe(false);
  });

  it('已删除评论不展示任何操作', () => {
    const deleted = makeComment({ status: 'DELETED', body: '' });
    const { wrapper } = mountThread(() => ({ status: 200, data: deleted }), {
      comment: deleted,
      isAdmin: true,
      isMerger: true,
    });

    expect(wrapper.find('.comment-body').text()).toBe('该评论已删除');
    expect(wrapper.findAll('.comment-action')).toHaveLength(0);
  });

  it('历史版本隐藏全部操作与回复输入框', async () => {
    const { wrapper } = mountThread(() => ({ status: 200, data: makeComment() }), {
      isHistorical: true,
      isAdmin: true,
      isMerger: true,
    });

    expect(wrapper.findAll('.comment-action')).toHaveLength(0);
    expect(wrapper.find('.comment-composer').exists()).toBe(false);
  });

  it('递归渲染 parentId 回复', () => {
    const parent = makeComment();
    const reply = makeComment({
      id: 'reply-1',
      parentId: COMMENT_ID,
      authorId: OTHER_USER_ID,
      authorDisplayName: '回复者',
      body: '回复内容',
    });
    const { wrapper } = mountThread(() => ({ status: 200, data: parent }), {
      comment: parent,
      comments: [parent, reply],
    });

    expect(wrapper.findAll('.comment-thread')).toHaveLength(2);
    expect(wrapper.text()).toContain('回复者');
    expect(wrapper.text()).toContain('回复内容');
  });
});

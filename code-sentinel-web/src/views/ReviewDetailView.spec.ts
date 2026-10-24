import { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia, type Pinia } from 'pinia';
import ElementPlus, { ElOption, ElSelect } from 'element-plus';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';

import { api, resetCsrfToken } from '../api/client';
import { useAuthStore } from '../stores/auth';
import type {
  RepoMember,
  ReviewerState,
  ReviewDetail,
  ReviewPermissions,
  ReviewReviewer,
  ReviewStatus,
} from '../api/types';
import ReviewStatusTag from '../components/ReviewStatusTag.vue';
import ReviewDetailView from './ReviewDetailView.vue';

/** 评审单主键 */
const REVIEW_ID = '55555555-5555-4555-8555-555555555555';

/** 仓库主键 */
const REPO_ID = '33333333-3333-4333-8333-333333333333';

/** 提交人主键 */
const SUBMITTER_ID = '11111111-1111-4111-8111-111111111111';

/** 当前登录用户（审核人）主键 */
const REVIEWER_ID = '22222222-2222-4222-8222-222222222222';

/** 其他审核人主键 */
const OTHER_REVIEWER_ID = '44444444-4444-4444-8444-444444444444';

/** 可新增的仓库成员主键 */
const NEW_MEMBER_ID = '66666666-6666-4666-8666-666666666666';

/** 第 1 版主键 */
const REVISION_1_ID = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';

/** 第 2 版主键 */
const REVISION_2_ID = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';

/** 模拟响应 */
interface MockResult {
  /** HTTP 状态码 */
  status: number;
  /** 响应体 */
  data: unknown;
}

/**
 * 把 axios 实例的适配器替换为内存实现，避免测试发起真实网络请求。
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
 * 详情测试的权限覆盖项。
 */
interface DetailOverrides {
  /** 评审状态 */
  status?: ReviewStatus;
  /** 能否给出结论 */
  canDecide?: boolean;
  /** 能否管理审核人 */
  canManageReviewers?: boolean;
  /** 能否关闭评审单 */
  canClose?: boolean;
  /** 能否标记为已合并 */
  canMarkMerged?: boolean;
  /** 是否仓库合并人 */
  isMerger?: boolean;
  /** 审核人列表 */
  reviewers?: ReviewReviewer[];
}

/**
 * 构造操作权限。
 * @param overrides 测试覆盖项
 * @returns 操作权限
 */
function buildPermissions(overrides: DetailOverrides): ReviewPermissions {
  return {
    canDecide: overrides.canDecide ?? true,
    canManageReviewers: overrides.canManageReviewers ?? false,
    canClose: overrides.canClose ?? false,
    canMarkMerged: overrides.canMarkMerged ?? false,
    isMerger: overrides.isMerger ?? false,
  };
}

/**
 * 构造默认审核人列表：当前用户已给出通过的结论，另一人待评审。
 * @param ownState 当前用户的结论状态
 * @returns 审核人列表
 */
function buildReviewers(ownState: ReviewerState = 'PENDING'): ReviewReviewer[] {
  return [
    {
      userId: REVIEWER_ID,
      displayName: 'Bob',
      state: ownState,
      note: ownState === 'APPROVED' ? '看起来没问题' : null,
    },
    {
      userId: OTHER_REVIEWER_ID,
      displayName: 'Carol',
      state: 'PENDING',
      note: null,
    },
  ];
}

/**
 * 构造评审详情。
 * @param overrides 测试覆盖项
 * @returns 评审详情
 */
function makeDetail(overrides: DetailOverrides = {}): ReviewDetail {
  return {
    id: REVIEW_ID,
    repo: { id: REPO_ID, name: '示例仓库' },
    baseBranch: 'main',
    headBranch: 'feature-a',
    submitter: { id: SUBMITTER_ID, displayName: 'Alice' },
    title: '修复登录问题',
    description: '补充登录校验',
    status: overrides.status ?? 'OPEN',
    baseSha: 'base-sha',
    headSha: 'sha-2',
    currentRevision: 2,
    revisions: [
      {
        id: REVISION_1_ID,
        seq: 1,
        headSha: 'sha-1',
        createdAt: '2026-10-24T01:00:00Z',
      },
      {
        id: REVISION_2_ID,
        seq: 2,
        headSha: 'sha-2',
        createdAt: '2026-10-24T02:00:00Z',
      },
    ],
    reviewers: overrides.reviewers ?? buildReviewers(),
    permissions: buildPermissions(overrides),
    mergeHint: false,
    mergedAt: null,
    mergeCommitSha: null,
    closeReason: null,
    createdAt: '2026-10-24T01:00:00Z',
    updatedAt: '2026-10-24T02:30:00Z',
  };
}

/**
 * 模拟详情页依赖的接口：详情与仓库成员。
 * @param detail 详情响应
 * @param members 仓库成员
 * @returns 详情接口调用计数
 */
function mockDetailApis(
  detail: ReviewDetail,
  members: RepoMember[],
): { detailCalls: { count: number } } {
  const detailCalls = { count: 0 };

  mockAdapter((config) => {
    const method = (config.method ?? 'get').toLowerCase();
    const url = config.url ?? '';

    if (url === '/api/auth/csrf') {
      return { status: 200, data: { token: 'csrf-test' } };
    }

    if (url === `/api/reviews/${REVIEW_ID}` && method === 'get') {
      detailCalls.count += 1;
      return { status: 200, data: detail };
    }

    if (url === `/api/repos/${REPO_ID}/members` && method === 'get') {
      return { status: 200, data: { items: members } };
    }

    return { status: 404, data: { code: 'NOT_FOUND', message: url, traceId: 'trace' } };
  });

  return { detailCalls };
}

/**
 * 构造仓库成员：提交人、已在评审单上的审核人、可新增的成员。
 * @returns 成员列表
 */
function buildMembers(): RepoMember[] {
  return [
    { userId: SUBMITTER_ID, username: 'alice', displayName: 'Alice', isMerger: false },
    { userId: REVIEWER_ID, username: 'bob', displayName: 'Bob', isMerger: false },
    { userId: NEW_MEMBER_ID, username: 'dave', displayName: 'Dave', isMerger: false },
  ];
}

/**
 * 注册测试用的 Pinia 与登录用户。
 * @param isAdmin 当前用户是否管理员
 * @returns Pinia 实例
 */
function createTestPinia(isAdmin: boolean): Pinia {
  const pinia = createPinia();
  setActivePinia(pinia);
  useAuthStore().user = {
    id: REVIEWER_ID,
    username: 'bob',
    displayName: 'Bob',
    isAdmin,
    mustChangePassword: false,
  };
  return pinia;
}

/**
 * 挂载详情页并完成首屏加载。
 * @param overrides 详情覆盖项
 * @param isAdmin 当前用户是否管理员
 * @returns 包装器与路由
 */
async function mountDetail(
  overrides: DetailOverrides = {},
  isAdmin = false,
): Promise<{
  wrapper: VueWrapper;
  router: Router;
  detail: ReviewDetail;
  detailCalls: { count: number };
}> {
  const detail = makeDetail(overrides);
  const { detailCalls } = mockDetailApis(detail, buildMembers());
  const pinia = createTestPinia(isAdmin);
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/reviews/:id', component: { template: '<div />' } }],
  });
  await router.push(`/reviews/${REVIEW_ID}`);
  await router.isReady();

  const wrapper = mount(ReviewDetailView, {
    props: { id: REVIEW_ID },
    global: {
      plugins: [pinia, ElementPlus, router],
    },
  });
  await flushPromises();

  return { wrapper, router, detail, detailCalls };
}

describe('评审详情视图', () => {
  beforeAll(() => {
    // Element Plus 表格与弹窗依赖 ResizeObserver，jsdom 未实现
    globalThis.ResizeObserver = class {
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
    } as unknown as typeof ResizeObserver;
  });

  beforeEach(() => {
    resetCsrfToken();
  });

  it('只在活跃评审单上向被分配的审核人显示通过与要求修改', async () => {
    const active = await mountDetail({ canDecide: true, status: 'OPEN' });
    expect(active.wrapper.find('.approve-button').exists()).toBe(true);
    expect(active.wrapper.find('.request-changes-button').exists()).toBe(true);
    active.wrapper.unmount();

    const notAssigned = await mountDetail({ canDecide: false, status: 'OPEN' });
    expect(notAssigned.wrapper.find('.approve-button').exists()).toBe(false);
    expect(notAssigned.wrapper.find('.request-changes-button').exists()).toBe(false);
    notAssigned.wrapper.unmount();

    const closed = await mountDetail({ canDecide: true, status: 'CLOSED' });
    expect(closed.wrapper.find('.approve-button').exists()).toBe(false);
    expect(closed.wrapper.find('.request-changes-button').exists()).toBe(false);
    closed.wrapper.unmount();
  });

  it('审核人给出结论后显示撤回按钮', async () => {
    const decided = await mountDetail({
      canDecide: true,
      status: 'OPEN',
      reviewers: buildReviewers('APPROVED'),
    });
    expect(decided.wrapper.find('.withdraw-button').exists()).toBe(true);
    decided.wrapper.unmount();

    const pending = await mountDetail({
      canDecide: true,
      status: 'OPEN',
      reviewers: buildReviewers('PENDING'),
    });
    expect(pending.wrapper.find('.withdraw-button').exists()).toBe(false);
    pending.wrapper.unmount();

    // 其他审核人已通过而自己待评审时不显示撤回
    const othersDecided = await mountDetail({
      canDecide: true,
      status: 'OPEN',
      reviewers: [
        { userId: REVIEWER_ID, displayName: 'Bob', state: 'PENDING', note: null },
        {
          userId: OTHER_REVIEWER_ID,
          displayName: 'Carol',
          state: 'CHANGES_REQUESTED',
          note: '需要补充测试',
        },
      ],
    });
    expect(othersDecided.wrapper.find('.withdraw-button').exists()).toBe(false);
    othersDecided.wrapper.unmount();
  });

  it('仅在评审通过后显示合并指引，标记已合并还要求权限', async () => {
    const approvedWithPermission = await mountDetail({
      status: 'APPROVED',
      canMarkMerged: true,
    });
    expect(approvedWithPermission.wrapper.find('.merge-guide').exists()).toBe(true);
    expect(approvedWithPermission.wrapper.find('.mark-merged-button').exists()).toBe(true);
    approvedWithPermission.wrapper.unmount();

    const approvedWithoutPermission = await mountDetail({
      status: 'APPROVED',
      canMarkMerged: false,
    });
    expect(approvedWithoutPermission.wrapper.find('.merge-guide').exists()).toBe(true);
    expect(approvedWithoutPermission.wrapper.find('.mark-merged-button').exists()).toBe(false);
    approvedWithoutPermission.wrapper.unmount();

    const open = await mountDetail({ status: 'OPEN', canMarkMerged: true });
    expect(open.wrapper.find('.merge-guide').exists()).toBe(false);
    expect(open.wrapper.find('.mark-merged-button').exists()).toBe(false);
    open.wrapper.unmount();
  });

  it('五种状态都能渲染对应的状态标签', async () => {
    const expected: Record<ReviewStatus, string> = {
      OPEN: '评审中',
      CHANGES_REQUESTED: '需修改',
      APPROVED: '已通过',
      MERGED: '已合并',
      CLOSED: '已关闭',
    };

    for (const [status, label] of Object.entries(expected) as [ReviewStatus, string][]) {
      const { wrapper } = await mountDetail({ status });
      expect(wrapper.findComponent(ReviewStatusTag).text()).toBe(label);
      wrapper.unmount();
    }
  });

  it('没有关闭权限时不显示关闭按钮', async () => {
    const noPermission = await mountDetail({ status: 'OPEN', canClose: false });
    expect(noPermission.wrapper.find('.close-button').exists()).toBe(false);
    noPermission.wrapper.unmount();

    const withPermission = await mountDetail({ status: 'OPEN', canClose: true });
    expect(withPermission.wrapper.find('.close-button').exists()).toBe(true);
    withPermission.wrapper.unmount();
  });

  it('添加审核人弹窗排除提交人与已有审核人', async () => {
    const { wrapper } = await mountDetail({
      status: 'OPEN',
      canManageReviewers: true,
    });

    await wrapper.find('.add-reviewers-button').trigger('click');
    await flushPromises();

    const optionValues = wrapper
      .findAllComponents(ElOption)
      .map((option) => String(option.props('value')));

    expect(optionValues).toContain(NEW_MEMBER_ID);
    expect(optionValues).not.toContain(SUBMITTER_ID);
    expect(optionValues).not.toContain(REVIEWER_ID);
    expect(optionValues).not.toContain(OTHER_REVIEWER_ID);

    wrapper.unmount();
  });

  it('切换版本只写入 revision 查询参数', async () => {
    const { wrapper, router, detailCalls } = await mountDetail({
      status: 'OPEN',
      canDecide: true,
    });

    // 默认选中最新版本
    expect(wrapper.findComponent(ElSelect).props('modelValue')).toBe(REVISION_2_ID);

    wrapper.findComponent(ElSelect).vm.$emit('update:modelValue', REVISION_1_ID);
    await flushPromises();

    expect(router.currentRoute.value.query.revision).toBe(REVISION_1_ID);
    // 只更新展示元数据，不重新请求详情
    expect(detailCalls.count).toBe(1);
    wrapper.unmount();
  });
});

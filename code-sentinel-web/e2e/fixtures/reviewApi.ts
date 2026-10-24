import type { BrowserContext, Route } from '@playwright/test';

import type {
  AuthUser,
  CreateCommentInput,
  CreateReviewInput,
  DiffFile,
  DiffFileList,
  Paginated,
  Repo,
  RepoMember,
  ReviewComment,
  ReviewDetail,
  ReviewListItem,
  ReviewRevision,
  ReviewerState,
  ReviewStatus,
} from '../../src/api/types';

/**
 * 接口路径匹配：只命中站点根下的 /api/，避免误伤 Vite 的 /src/api/*.ts 模块请求。
 */
export const API_ROUTE = /^https?:\/\/[^/]+\/api\//;

/** 用例内统一使用的固定时间，保证快照稳定 */
const NOW = '2026-10-24T08:00:00.000Z';

/** 评审单主键 */
const REVIEW_ID = 'review-1';

/** 第 1 版主键 */
const REVISION_1_ID = 'rev-1';

/** 第 2 版主键 */
const REVISION_2_ID = 'rev-2';

/** 被评审文件路径 */
const FILE_PATH = 'src/auth/login.ts';

/** 初始 head 提交 */
const HEAD_SHA_1 = 'head-sha-0000001';

/** 新版本 head 提交 */
const HEAD_SHA_2 = 'head-sha-0000002';

/** 基线提交（A 分支锁定值） */
const BASE_SHA = 'base-sha-0000001';

/** 冒烟测试中的两种角色 */
export type E2EUser = 'submitter' | 'reviewer';

/** 两个固定用户 */
const USERS: Record<E2EUser, AuthUser> = {
  submitter: {
    id: 'u-submitter',
    username: 'submitter',
    displayName: '提交人',
    isAdmin: false,
    mustChangePassword: false,
  },
  reviewer: {
    id: 'u-reviewer',
    username: 'reviewer',
    displayName: '审核员',
    isAdmin: false,
    mustChangePassword: false,
  },
};

/** 固定仓库 */
const REPO: Repo = {
  id: 'repo-1',
  name: '示例仓库',
  gitUrl: 'git://gitblit.local/example/demo.git',
  defaultBaseBranch: 'main',
  syncIntervalSeconds: 300,
  lastFetchedAt: NOW,
  lastError: null,
  isActive: true,
  memberCount: 2,
  isMerger: false,
};

/**
 * 打桩状态：两个浏览器上下文共享读写，模拟同一套后端数据。
 */
export interface ReviewApiState {
  /** 是否已提交评审 */
  reviewCreated: boolean;
  /** 评审标题 */
  title: string;
  /** 评审说明 */
  description: string;
  /** 当前状态 */
  status: ReviewStatus;
  /** 当前版本号 */
  currentRevision: number;
  /** 当前 head 提交 */
  headSha: string;
  /** 版本列表 */
  revisions: ReviewRevision[];
  /** 审核人结论状态 */
  reviewerState: ReviewerState;
  /** 审核人附言 */
  reviewerNote: string | null;
  /** 审核人结论对应的版本，用于判断 canDecide */
  reviewerStateRevisionId: string;
  /** 全部评论 */
  comments: ReviewComment[];
  /** 是否有待生成的新版本（要求修改后由下一次刷新触发） */
  revisionBumpPending: boolean;
  /** 合并时间 */
  mergedAt: string | null;
  /** 合并提交 */
  mergeCommitSha: string | null;
  /** 当前文件是否已看过 */
  fileViewed: boolean;
  /** 下一条评论序号 */
  nextCommentId: number;
  /** 最近一次创建评审的请求载荷，供用例断言 */
  lastCreateReview: CreateReviewInput | null;
  /** 最近一次创建评论或回复的请求载荷，供用例断言 */
  lastCreateComment: CreateCommentInput | null;
}

/**
 * 创建一份初始打桩状态。
 */
export function createReviewApiState(): ReviewApiState {
  return {
    reviewCreated: false,
    title: '',
    description: '',
    status: 'OPEN',
    currentRevision: 1,
    headSha: HEAD_SHA_1,
    revisions: [{ id: REVISION_1_ID, seq: 1, headSha: HEAD_SHA_1, createdAt: NOW }],
    reviewerState: 'PENDING',
    reviewerNote: null,
    reviewerStateRevisionId: REVISION_1_ID,
    comments: [],
    revisionBumpPending: false,
    mergedAt: null,
    mergeCommitSha: null,
    fileViewed: false,
    nextCommentId: 1,
    lastCreateReview: null,
    lastCreateComment: null,
  };
}

/**
 * 判断状态是否仍可操作。
 */
function isActive(status: ReviewStatus): boolean {
  return status === 'OPEN' || status === 'CHANGES_REQUESTED' || status === 'APPROVED';
}

/**
 * 按当前用户组装评审单详情，权限与后端规则一致。
 */
function detailFor(state: ReviewApiState, user: E2EUser): ReviewDetail {
  const isReviewer = user === 'reviewer';
  const active = isActive(state.status);
  const currentRevisionId = state.revisions[state.revisions.length - 1].id;

  return {
    id: REVIEW_ID,
    repo: { id: REPO.id, name: REPO.name },
    baseBranch: 'main',
    headBranch: 'feature/login-rate-limit',
    submitter: { id: USERS.submitter.id, displayName: USERS.submitter.displayName },
    title: state.title,
    description: state.description,
    status: state.status,
    baseSha: BASE_SHA,
    headSha: state.headSha,
    currentRevision: state.currentRevision,
    revisions: state.revisions,
    reviewers: [
      {
        userId: USERS.reviewer.id,
        displayName: USERS.reviewer.displayName,
        state: state.reviewerState,
        note: state.reviewerNote,
      },
    ],
    permissions: {
      canDecide: active && isReviewer && state.reviewerStateRevisionId === currentRevisionId,
      canManageReviewers: active && user === 'submitter',
      canClose: user === 'submitter' || isReviewer,
      canMarkMerged: state.status === 'APPROVED' && isReviewer,
      isMerger: isReviewer,
    },
    mergeHint: false,
    mergedAt: state.mergedAt,
    mergeCommitSha: state.mergeCommitSha,
    closeReason: null,
    createdAt: NOW,
    updatedAt: NOW,
  };
}

/**
 * 组装评审列表项。
 */
function listItemFor(state: ReviewApiState): ReviewListItem {
  return {
    id: REVIEW_ID,
    repo: { id: REPO.id, name: REPO.name },
    title: state.title,
    status: state.status,
    baseBranch: 'main',
    headBranch: 'feature/login-rate-limit',
    submitter: { id: USERS.submitter.id, displayName: USERS.submitter.displayName },
    reviewersTotal: 1,
    reviewersApproved: state.reviewerState === 'APPROVED' ? 1 : 0,
    currentRevision: state.currentRevision,
    createdAt: NOW,
    updatedAt: NOW,
  };
}

/**
 * 按会话用户与查询范围返回列表数据。
 */
function listFor(
  state: ReviewApiState,
  user: E2EUser,
  scope: string | null,
): ReviewListItem[] {
  if (!state.reviewCreated) {
    return [];
  }

  if (scope === 'to_review' && user !== 'reviewer') {
    return [];
  }

  if (scope === 'mine' && user !== 'submitter') {
    return [];
  }

  return [listItemFor(state)];
}

/**
 * 组装文件列表，新增行数随版本变化。
 */
function filesFor(state: ReviewApiState): DiffFileList {
  return {
    items: [
      {
        path: FILE_PATH,
        oldPath: null,
        status: 'MODIFIED',
        additions: state.currentRevision === 1 ? 1 : 2,
        deletions: 0,
        isBinary: false,
        isTooLarge: false,
        viewed: state.fileViewed,
      },
    ],
    total: 1,
    viewedCount: state.fileViewed ? 1 : 0,
    fileCount: 1,
    truncated: false,
  };
}

/**
 * 组装单文件 diff：一个上下文行加一到两个新增行。
 */
function diffFor(state: ReviewApiState): DiffFile {
  const lines: DiffFile['hunks'][number]['lines'] = [
    { type: 'CONTEXT', oldLine: 1, newLine: 1, text: '// 登录限流' },
    { type: 'ADD', oldLine: null, newLine: 2, text: 'export const MAX_ATTEMPTS = 5;' },
  ];
  if (state.currentRevision >= 2) {
    lines.push({ type: 'ADD', oldLine: null, newLine: 3, text: 'export const WINDOW_MINUTES = 10;' });
  }

  return {
    path: FILE_PATH,
    oldPath: null,
    status: 'MODIFIED',
    additions: lines.filter((line) => line.type === 'ADD').length,
    deletions: 0,
    isBinary: false,
    isTooLarge: false,
    hunks: [
      {
        oldStart: 1,
        oldLines: 1,
        newStart: 1,
        newLines: lines.length,
        lines,
      },
    ],
  };
}

/**
 * 组装某一侧的全文，供语法高亮取用。
 */
function blobFor(
  state: ReviewApiState,
  side: string | null,
): { path: string; content: string; isBinary: boolean; isTooLarge: boolean } {
  const context = '// 登录限流\n';
  if (side === 'OLD') {
    return { path: FILE_PATH, content: context, isBinary: false, isTooLarge: false };
  }

  const secondLine = 'export const MAX_ATTEMPTS = 5;\n';
  const thirdLine = state.currentRevision >= 2 ? 'export const WINDOW_MINUTES = 10;\n' : '';
  return {
    path: FILE_PATH,
    content: `${context}${secondLine}${thirdLine}`,
    isBinary: false,
    isTooLarge: false,
  };
}

/**
 * 统一输出 JSON 响应。
 */
async function fulfillJson(route: Route, body: unknown, status = 200): Promise<void> {
  await route.fulfill({
    status,
    contentType: 'application/json; charset=utf-8',
    body: JSON.stringify(body),
  });
}

/**
 * 分页包装。
 */
function paginated<T>(items: T[]): Paginated<T> {
  return { items, total: items.length, page: 1, pageSize: 100 };
}

/**
 * 校验创建评审请求，返回错误信息，合法时返回 null。
 */
function validateCreateReview(body: CreateReviewInput): string | null {
  if (typeof body.repoId !== 'string' || body.repoId === '') {
    return 'repoId 必须是非空仓库主键';
  }

  if (typeof body.baseBranch !== 'string' || body.baseBranch === '') {
    return 'baseBranch 必须是非空分支名';
  }

  if (typeof body.headBranch !== 'string' || body.headBranch === '') {
    return 'headBranch 必须是非空分支名';
  }

  if (typeof body.title !== 'string' || body.title === '') {
    return 'title 必须是非空标题';
  }

  if (!Array.isArray(body.reviewerIds) || body.reviewerIds.length !== 1) {
    return 'reviewerIds 必须恰好包含一名审核人';
  }

  if (typeof body.reviewerIds[0] !== 'string' || body.reviewerIds[0] === '') {
    return 'reviewerId 必须是非空用户主键';
  }

  return null;
}

/**
 * 校验创建评论或回复请求，返回错误信息，合法时返回 null。
 */
function validateCreateComment(body: CreateCommentInput): string | null {
  if (typeof body.path !== 'string' || body.path === '') {
    return 'path 必须是非空文件路径';
  }

  if (body.side !== 'OLD' && body.side !== 'NEW') {
    return 'side 必须是 OLD 或 NEW';
  }

  if (typeof body.line !== 'number' || body.line < 1) {
    return 'line 必须是正整数';
  }

  if (typeof body.body !== 'string' || body.body === '') {
    return 'body 必须是非空评论内容';
  }

  if (
    body.parentId !== undefined
    && body.parentId !== null
    && (typeof body.parentId !== 'string' || body.parentId === '')
  ) {
    return 'parentId 必须是非空评论主键';
  }

  return null;
}

/**
 * 处理创建评审请求。
 */
async function handleCreateReview(route: Route, state: ReviewApiState): Promise<void> {
  const body = route.request().postDataJSON() as CreateReviewInput;

  const error = validateCreateReview(body);
  if (error !== null) {
    await fulfillJson(
      route,
      { code: 'E2E_INVALID_CREATE_REVIEW', message: error, traceId: 'e2e' },
      400,
    );
    return;
  }

  state.lastCreateReview = body;
  state.reviewCreated = true;
  state.title = body.title;
  state.description = body.description ?? '';
  state.reviewerState = 'PENDING';
  state.reviewerNote = null;
  state.reviewerStateRevisionId = REVISION_1_ID;
  state.status = 'OPEN';

  await fulfillJson(route, detailFor(state, 'submitter'), 201);
}

/**
 * 处理审核结论请求。
 */
async function handleDecision(
  route: Route,
  state: ReviewApiState,
  user: E2EUser,
): Promise<void> {
  if (!state.reviewCreated || user !== 'reviewer') {
    await fulfillJson(route, { code: 'NOT_FOUND', message: '评审单不存在', traceId: 'e2e' }, 404);
    return;
  }

  const body = route.request().postDataJSON() as {
    state: 'APPROVED' | 'CHANGES_REQUESTED' | 'PENDING';
    note?: string;
  };
  state.reviewerState = body.state;
  state.reviewerNote = body.note ?? null;
  state.reviewerStateRevisionId = state.revisions[state.revisions.length - 1].id;
  state.status = body.state === 'CHANGES_REQUESTED'
    ? 'CHANGES_REQUESTED'
    : body.state === 'APPROVED'
      ? 'APPROVED'
      : 'OPEN';

  // 要求修改后，下一次刷新会模拟“提交人又推了新提交”
  if (body.state === 'CHANGES_REQUESTED') {
    state.revisionBumpPending = true;
  }

  await fulfillJson(route, detailFor(state, user));
}

/**
 * 处理刷新请求：有待生成的新版本时追加 v2，并作废旧结论。
 */
async function handleRefresh(
  route: Route,
  state: ReviewApiState,
  user: E2EUser,
): Promise<void> {
  if (state.revisionBumpPending) {
    state.currentRevision = 2;
    state.headSha = HEAD_SHA_2;
    state.revisions = [
      ...state.revisions,
      { id: REVISION_2_ID, seq: 2, headSha: HEAD_SHA_2, createdAt: NOW },
    ];
    state.reviewerState = 'PENDING';
    state.reviewerNote = null;
    state.reviewerStateRevisionId = REVISION_2_ID;
    state.status = 'OPEN';
    state.revisionBumpPending = false;
    // 旧评论不再能定位到新版本，按后端语义标记为已过期
    state.comments = state.comments.map((comment) => ({ ...comment, status: 'OUTDATED' }));
  }

  await fulfillJson(route, detailFor(state, user));
}

/**
 * 处理标记已合并请求。
 */
async function handleMarkMerged(
  route: Route,
  state: ReviewApiState,
  user: E2EUser,
): Promise<void> {
  const body = route.request().postDataJSON() as { mergeCommitSha?: string };
  state.status = 'MERGED';
  state.mergedAt = NOW;
  state.mergeCommitSha = body.mergeCommitSha ?? 'merge-sha-0000001';
  await fulfillJson(route, detailFor(state, user));
}

/**
 * 处理评论创建请求。
 */
async function handleCreateComment(
  route: Route,
  state: ReviewApiState,
  user: E2EUser,
): Promise<void> {
  const body = route.request().postDataJSON() as CreateCommentInput;

  const error = validateCreateComment(body);
  if (error !== null) {
    await fulfillJson(
      route,
      { code: 'E2E_INVALID_CREATE_COMMENT', message: error, traceId: 'e2e' },
      400,
    );
    return;
  }

  state.lastCreateComment = body;
  const comment: ReviewComment = {
    id: `comment-${state.nextCommentId}`,
    revisionId: state.revisions[state.revisions.length - 1].id,
    authorId: USERS[user].id,
    authorDisplayName: USERS[user].displayName,
    path: body.path,
    side: body.side,
    line: body.line,
    body: body.body,
    parentId: body.parentId ?? null,
    status: 'OPEN',
    resolvedBy: null,
    resolvedAt: null,
    createdAt: NOW,
    updatedAt: NOW,
  };
  state.nextCommentId += 1;
  state.comments = [...state.comments, comment];
  await fulfillJson(route, comment, 201);
}

/**
 * 按路径与方法分发请求；未覆盖的接口返回 404，便于发现遗漏。
 */
async function handleRequest(
  route: Route,
  state: ReviewApiState,
  user: E2EUser,
): Promise<void> {
  const request = route.request();
  const url = new URL(request.url());
  const path = url.pathname;
  const method = request.method();

  if (path === '/api/auth/csrf') {
    await fulfillJson(route, { token: 'e2e-csrf' });
    return;
  }

  if (path === '/api/auth/me') {
    await fulfillJson(route, USERS[user]);
    return;
  }

  if (path === '/api/notifications/unread-count') {
    await fulfillJson(route, { count: 1 });
    return;
  }

  if (path === '/api/repos' && method === 'GET') {
    await fulfillJson(route, paginated([{ ...REPO, isMerger: user === 'reviewer' }]));
    return;
  }

  if (path === '/api/reviews' && method === 'GET') {
    await fulfillJson(route, paginated(listFor(state, user, url.searchParams.get('scope'))));
    return;
  }

  if (path === '/api/reviews' && method === 'POST') {
    await handleCreateReview(route, state);
    return;
  }

  if (path === `/api/repos/${REPO.id}/branches`) {
    const items = [
      { name: 'main', headSha: BASE_SHA },
      { name: 'feature/login-rate-limit', headSha: state.headSha },
    ];
    await fulfillJson(route, { items });
    return;
  }

  if (path === `/api/repos/${REPO.id}/branches/unreviewed`) {
    const items = [
      { name: 'feature/login-rate-limit', headSha: state.headSha, aheadBy: 2 },
    ];
    await fulfillJson(route, { items });
    return;
  }

  if (path === `/api/repos/${REPO.id}/members`) {
    const members: RepoMember[] = [
      {
        userId: USERS.submitter.id,
        username: USERS.submitter.username,
        displayName: USERS.submitter.displayName,
        isMerger: false,
        isActive: true,
      },
      {
        userId: USERS.reviewer.id,
        username: USERS.reviewer.username,
        displayName: USERS.reviewer.displayName,
        isMerger: true,
        isActive: true,
      },
    ];
    await fulfillJson(route, { items: members });
    return;
  }

  if (!state.reviewCreated) {
    await fulfillJson(route, { code: 'NOT_FOUND', message: '评审单不存在', traceId: 'e2e' }, 404);
    return;
  }

  if (path === `/api/reviews/${REVIEW_ID}` && method === 'GET') {
    await fulfillJson(route, detailFor(state, user));
    return;
  }

  if (path === `/api/reviews/${REVIEW_ID}/refresh`) {
    await handleRefresh(route, state, user);
    return;
  }

  if (path === `/api/reviews/${REVIEW_ID}/decisions`) {
    await handleDecision(route, state, user);
    return;
  }

  if (path === `/api/reviews/${REVIEW_ID}/mark-merged`) {
    await handleMarkMerged(route, state, user);
    return;
  }

  if (path === `/api/reviews/${REVIEW_ID}/files` && method === 'GET') {
    await fulfillJson(route, filesFor(state));
    return;
  }

  if (path === `/api/reviews/${REVIEW_ID}/files/viewed` && method === 'PUT') {
    const body = request.postDataJSON() as { path: string; viewed: boolean };
    state.fileViewed = body.viewed;
    await fulfillJson(route, { path: body.path, viewed: body.viewed });
    return;
  }

  if (path === `/api/reviews/${REVIEW_ID}/diff`) {
    await fulfillJson(route, diffFor(state));
    return;
  }

  if (path === `/api/reviews/${REVIEW_ID}/blob`) {
    await fulfillJson(route, blobFor(state, url.searchParams.get('side')));
    return;
  }

  if (path === `/api/reviews/${REVIEW_ID}/comments` && method === 'GET') {
    const revisionId = url.searchParams.get('revisionId');
    const items = revisionId === null
      ? state.comments
      : state.comments.filter((comment) => comment.revisionId === revisionId);
    await fulfillJson(route, { items });
    return;
  }

  if (path === `/api/reviews/${REVIEW_ID}/comments` && method === 'POST') {
    await handleCreateComment(route, state, user);
    return;
  }

  await fulfillJson(
    route,
    { code: 'E2E_NOT_STUBBED', message: `未打桩的接口：${method} ${path}`, traceId: 'e2e' },
    404,
  );
}

/**
 * 为指定会话安装接口打桩。
 * @param context 浏览器上下文
 * @param state 提交人与审核人共享的状态
 * @param user 当前会话对应的用户
 */
export async function installReviewApi(
  context: BrowserContext,
  state: ReviewApiState,
  user: E2EUser,
): Promise<void> {
  await context.route(API_ROUTE, async (route) => {
    try {
      await handleRequest(route, state, user);
    } catch (error) {
      await fulfillJson(
        route,
        { code: 'E2E_STUB_ERROR', message: String(error), traceId: 'e2e' },
        500,
      );
    }
  });
}

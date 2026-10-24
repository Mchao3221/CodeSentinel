/**
 * 评审单接口：列表、创建、详情与操作。
 */
import { api } from './client';
import type {
  CloseReviewInput,
  CreateReviewInput,
  DecideReviewInput,
  MarkMergedInput,
  Paginated,
  ReviewDetail,
  ReviewListItem,
  ReviewListQuery,
  ReviewRevision,
} from './types';

/**
 * 分页查询评审单。
 * @param query 范围、仓库、状态与分页参数
 * @returns 分页评审单列表
 */
export async function fetchReviews(
  query: ReviewListQuery,
): Promise<Paginated<ReviewListItem>> {
  const { data } = await api.get<Paginated<ReviewListItem>>('/api/reviews', {
    params: {
      scope: query.scope,
      repoId: query.repoId,
      status: query.status,
      page: query.page,
      pageSize: query.pageSize,
    },
  });
  return data;
}

/**
 * 创建评审单。
 * @param input 创建请求
 * @returns 创建成功的评审单详情
 */
export async function createReview(input: CreateReviewInput): Promise<ReviewDetail> {
  const { data } = await api.post<ReviewDetail>('/api/reviews', input);
  return data;
}

/**
 * 获取评审单详情。
 * @param reviewId 评审单主键
 * @returns 评审单详情
 */
export async function fetchReviewDetail(reviewId: string): Promise<ReviewDetail> {
  const { data } = await api.get<ReviewDetail>(`/api/reviews/${reviewId}`);
  return data;
}

/**
 * 获取评审单的全部版本，后端按版本号升序返回。
 * @param reviewId 评审单主键
 * @returns 版本列表
 */
export async function fetchReviewRevisions(reviewId: string): Promise<ReviewRevision[]> {
  const { data } = await api.get<{ items: ReviewRevision[] }>(
    `/api/reviews/${reviewId}/revisions`,
  );
  return data.items;
}

/**
 * 提交审核结论，返回更新后的评审单详情。
 * @param reviewId 评审单主键
 * @param input 结论与附言
 * @returns 更新后的评审单详情
 */
export async function decideReview(
  reviewId: string,
  input: DecideReviewInput,
): Promise<ReviewDetail> {
  const { data } = await api.post<ReviewDetail>(`/api/reviews/${reviewId}/decisions`, input);
  return data;
}

/**
 * 关闭评审单，返回更新后的评审单详情。
 * @param reviewId 评审单主键
 * @param input 关闭原因
 * @returns 更新后的评审单详情
 */
export async function closeReview(
  reviewId: string,
  input: CloseReviewInput = {},
): Promise<ReviewDetail> {
  const { data } = await api.post<ReviewDetail>(`/api/reviews/${reviewId}/close`, input);
  return data;
}

/**
 * 手工标记评审单为已合并，返回更新后的评审单详情。
 * @param reviewId 评审单主键
 * @param input 合并提交
 * @returns 更新后的评审单详情
 */
export async function markReviewMerged(
  reviewId: string,
  input: MarkMergedInput = {},
): Promise<ReviewDetail> {
  const { data } = await api.post<ReviewDetail>(
    `/api/reviews/${reviewId}/mark-merged`,
    input,
  );
  return data;
}

/**
 * 刷新评审单：触发一次仓库同步并返回最新详情。
 * @param reviewId 评审单主键
 * @returns 更新后的评审单详情
 */
export async function refreshReview(reviewId: string): Promise<ReviewDetail> {
  const { data } = await api.post<ReviewDetail>(`/api/reviews/${reviewId}/refresh`);
  return data;
}

/**
 * 新增审核人，返回更新后的评审单详情。
 * @param reviewId 评审单主键
 * @param userIds 待新增的审核人主键列表
 * @returns 更新后的评审单详情
 */
export async function addReviewers(
  reviewId: string,
  userIds: string[],
): Promise<ReviewDetail> {
  const { data } = await api.post<ReviewDetail>(`/api/reviews/${reviewId}/reviewers`, {
    userIds,
  });
  return data;
}

/**
 * 移除审核人，返回更新后的评审单详情。
 * @param reviewId 评审单主键
 * @param userId 被移除的审核人主键
 * @returns 更新后的评审单详情
 */
export async function removeReviewer(
  reviewId: string,
  userId: string,
): Promise<ReviewDetail> {
  const { data } = await api.delete<ReviewDetail>(
    `/api/reviews/${reviewId}/reviewers/${userId}`,
  );
  return data;
}

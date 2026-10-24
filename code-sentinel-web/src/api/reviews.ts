/**
 * 评审单接口：列表与创建。
 */
import { api } from './client';
import type {
  CreateReviewInput,
  Paginated,
  ReviewDetail,
  ReviewListItem,
  ReviewListQuery,
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

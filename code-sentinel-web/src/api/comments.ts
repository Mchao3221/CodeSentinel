/**
 * 行级评论接口：列表、创建、编辑、删除与解决状态流转。
 */
import { api } from './client';
import type { CreateCommentInput, ReviewComment } from './types';

/**
 * 读取评审单上的全部评论，后端按创建时间升序返回。
 * @param reviewId 评审单主键
 * @returns 评论列表
 */
export async function listComments(reviewId: string): Promise<ReviewComment[]> {
  const { data } = await api.get<{ items: ReviewComment[] }>(
    `/api/reviews/${reviewId}/comments`,
  );
  return data.items;
}

/**
 * 创建评论或回复。
 * @param reviewId 评审单主键
 * @param input 行位置与评论内容
 * @returns 创建的评论
 */
export async function createComment(
  reviewId: string,
  input: CreateCommentInput,
): Promise<ReviewComment> {
  const { data } = await api.post<ReviewComment>(
    `/api/reviews/${reviewId}/comments`,
    input,
  );
  return data;
}

/**
 * 修改评论正文，仅作者可操作。
 * @param commentId 评论主键
 * @param body 新评论内容
 * @returns 更新后的评论
 */
export async function updateComment(
  commentId: string,
  body: string,
): Promise<ReviewComment> {
  const { data } = await api.patch<ReviewComment>(`/api/comments/${commentId}`, { body });
  return data;
}

/**
 * 软删除评论，作者或管理员可操作。
 * @param commentId 评论主键
 * @returns 更新后的评论
 */
export async function deleteComment(commentId: string): Promise<ReviewComment> {
  const { data } = await api.delete<ReviewComment>(`/api/comments/${commentId}`);
  return data;
}

/**
 * 把评论标记为已解决。
 * @param commentId 评论主键
 * @returns 更新后的评论
 */
export async function resolveComment(commentId: string): Promise<ReviewComment> {
  const { data } = await api.post<ReviewComment>(`/api/comments/${commentId}/resolve`);
  return data;
}

/**
 * 取消评论的已解决标记。
 * @param commentId 评论主键
 * @returns 更新后的评论
 */
export async function unresolveComment(commentId: string): Promise<ReviewComment> {
  const { data } = await api.post<ReviewComment>(`/api/comments/${commentId}/unresolve`);
  return data;
}

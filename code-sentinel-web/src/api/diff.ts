/**
 * diff 相关接口：文件列表、单文件 diff、全文、已看过标记与评论列表。
 */
import { api } from './client';
import type {
  BlobContent,
  DiffFile,
  DiffFileList,
  DiffSide,
  ReviewComment,
} from './types';

/**
 * 构造历史版本查询参数；空版本主键按“当前版本”处理。
 * @param revisionId 历史版本主键，可空
 * @returns 查询参数对象
 */
function revisionQuery(revisionId?: string): { revisionId?: string } {
  return revisionId ? { revisionId } : {};
}

/**
 * 读取版本文件列表。
 * @param reviewId 评审单主键
 * @param revisionId 历史版本主键，可空；省略时取当前版本
 * @returns 文件列表
 */
export async function getFiles(
  reviewId: string,
  revisionId?: string,
): Promise<DiffFileList> {
  const { data } = await api.get<DiffFileList>(`/api/reviews/${reviewId}/files`, {
    params: revisionQuery(revisionId),
  });
  return data;
}

/**
 * 读取单个文件的 diff。
 * @param reviewId 评审单主键
 * @param path 文件路径
 * @param revisionId 历史版本主键，可空
 * @returns 文件 diff
 */
export async function getFileDiff(
  reviewId: string,
  path: string,
  revisionId?: string,
): Promise<DiffFile> {
  const { data } = await api.get<DiffFile>(`/api/reviews/${reviewId}/diff`, {
    params: { path, ...revisionQuery(revisionId) },
  });
  return data;
}

/**
 * 读取文件在 OLD（合并基）或 NEW（head）一侧的全文。
 * @param reviewId 评审单主键
 * @param path 文件路径
 * @param side OLD 或 NEW
 * @param revisionId 历史版本主键，可空
 * @returns 文件全文
 */
export async function getBlob(
  reviewId: string,
  path: string,
  side: DiffSide,
  revisionId?: string,
): Promise<BlobContent> {
  const { data } = await api.get<BlobContent>(`/api/reviews/${reviewId}/blob`, {
    params: { path, side, ...revisionQuery(revisionId) },
  });
  return data;
}

/**
 * 标记或取消标记文件已看过。
 * @param reviewId 评审单主键
 * @param path 文件路径
 * @param viewed true 标记已看过，false 取消标记
 * @returns 标记后的状态
 */
export async function setViewed(
  reviewId: string,
  path: string,
  viewed: boolean,
): Promise<{ path: string; viewed: boolean }> {
  const { data } = await api.put<{ path: string; viewed: boolean }>(
    `/api/reviews/${reviewId}/files/viewed`,
    { path, viewed },
  );
  return data;
}

/**
 * 读取评审单上的评论；传入 revisionId 时只返回该版本创建的评论。
 * @param reviewId 评审单主键
 * @param revisionId 历史版本主键，可空
 * @returns 评论列表
 */
export async function getComments(
  reviewId: string,
  revisionId?: string,
): Promise<ReviewComment[]> {
  const { data } = await api.get<{ items: ReviewComment[] }>(
    `/api/reviews/${reviewId}/comments`,
    { params: revisionQuery(revisionId) },
  );
  return data.items;
}

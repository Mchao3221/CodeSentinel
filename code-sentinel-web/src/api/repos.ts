/**
 * 仓库、分支与成员接口。
 */
import { api } from './client';
import type { Branch, Paginated, Repo, RepoMember, UnreviewedBranch } from './types';

/** 仓库下拉一次拉取的最大条数，与后端分页上限一致 */
const MAX_PAGE_SIZE = 100;

/**
 * 分页获取当前用户可访问的仓库。
 * @param page 页码，从 1 开始
 * @param pageSize 每页条数
 * @returns 分页仓库列表
 */
export async function fetchRepos(
  page = 1,
  pageSize = MAX_PAGE_SIZE,
): Promise<Paginated<Repo>> {
  const { data } = await api.get<Paginated<Repo>>('/api/repos', {
    params: { page, pageSize },
  });
  return data;
}

/**
 * 获取仓库的远端分支列表。
 * @param repoId 仓库主键
 * @param q 分支名前缀，可选
 * @returns 分支列表
 */
export async function fetchBranches(repoId: string, q?: string): Promise<Branch[]> {
  const { data } = await api.get<{ items: Branch[] }>(
    `/api/repos/${repoId}/branches`,
    { params: { q } },
  );
  return data.items;
}

/**
 * 获取未开单分支，后端按领先提交数降序返回。
 * @param repoId 仓库主键
 * @returns 未开单分支列表
 */
export async function fetchUnreviewedBranches(repoId: string): Promise<UnreviewedBranch[]> {
  const { data } = await api.get<{ items: UnreviewedBranch[] }>(
    `/api/repos/${repoId}/branches/unreviewed`,
  );
  return data.items;
}

/**
 * 获取仓库成员，可用于选择审核人。
 * @param repoId 仓库主键
 * @returns 成员列表
 */
export async function fetchRepoMembers(repoId: string): Promise<RepoMember[]> {
  const { data } = await api.get<{ items: RepoMember[] }>(`/api/repos/${repoId}/members`);
  return data.items;
}

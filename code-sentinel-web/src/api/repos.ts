/**
 * 仓库、分支与成员接口。
 */
import { api } from './client';
import type {
  Branch,
  CreateRepoInput,
  Paginated,
  Repo,
  RepoMember,
  RepoMemberInput,
  SyncResult,
  TestConnectionResult,
  UnreviewedBranch,
  UpdateRepoInput,
} from './types';

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
 * @param options includeInactive 为 true 时管理员可同时读取已停用成员
 * @returns 成员列表
 */
export async function fetchRepoMembers(
  repoId: string,
  options: { includeInactive?: boolean } = {},
): Promise<RepoMember[]> {
  const { data } = await api.get<{ items: RepoMember[] }>(`/api/repos/${repoId}/members`, {
    params: { includeInactive: options.includeInactive === true ? true : undefined },
  });
  return data.items;
}

/**
 * 新建仓库，后端会先验证远端可连接。
 * @param input 新建仓库请求
 * @returns 新建的仓库
 */
export async function createRepo(input: CreateRepoInput): Promise<Repo> {
  const { data } = await api.post<Repo>('/api/repos', input);
  return data;
}

/**
 * 局部更新仓库，未提供的字段保持不变。
 * @param repoId 仓库主键
 * @param input 更新请求
 * @returns 更新后的仓库
 */
export async function updateRepo(repoId: string, input: UpdateRepoInput): Promise<Repo> {
  const { data } = await api.patch<Repo>(`/api/repos/${repoId}`, input);
  return data;
}

/**
 * 使用已保存凭据测试仓库连通性。
 * @param repoId 仓库主键
 * @returns 连接测试结果
 */
export async function testRepoConnection(repoId: string): Promise<TestConnectionResult> {
  const { data } = await api.post<TestConnectionResult>(
    `/api/repos/${repoId}/test-connection`,
  );
  return data;
}

/**
 * 手动触发一次镜像同步。
 * @param repoId 仓库主键
 * @returns 同步结果
 */
export async function syncRepo(repoId: string): Promise<SyncResult> {
  const { data } = await api.post<SyncResult>(`/api/repos/${repoId}/sync`);
  return data;
}

/**
 * 整体覆盖仓库成员与合并人标记。
 * @param repoId 仓库主键
 * @param members 目标成员列表
 * @returns 覆盖后的成员列表
 */
export async function replaceRepoMembers(
  repoId: string,
  members: RepoMemberInput[],
): Promise<RepoMember[]> {
  const { data } = await api.put<{ items: RepoMember[] }>(
    `/api/repos/${repoId}/members`,
    { members },
  );
  return data.items;
}

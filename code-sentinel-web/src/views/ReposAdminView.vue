<script setup lang="ts">
/**
 * 仓库管理页：仓库列表、新建/编辑抽屉、连接测试、成员合并人配置与手动同步。
 */
import { computed, onMounted, reactive, ref, watch } from 'vue';
import axios from 'axios';
import { ElMessage, type FormInstance, type FormRules } from 'element-plus';

import {
  createRepo,
  fetchRepoMembers,
  fetchRepos,
  replaceRepoMembers,
  syncRepo,
  testRepoConnection,
  updateRepo,
} from '../api/repos';
import { fetchUsers } from '../api/users';
import type {
  AdminUser,
  CreateRepoInput,
  Repo,
  RepoCredentialKind,
  UpdateRepoInput,
} from '../api/types';
import { apiErrorMessage } from '../utils/apiError';
import { formatDateTime } from '../utils/datetime';

/** 仓库列表每页条数 */
const PAGE_SIZE = 20;

/** 成员弹窗一次拉取的用户上限，与后端分页上限一致 */
const MAX_USERS = 100;

/** 同步间隔默认值（秒） */
const DEFAULT_SYNC_INTERVAL = 300;

/** 默认目标分支 */
const DEFAULT_BASE_BRANCH = 'main';

/** 连接测试成功文案 */
const CONNECTION_OK_TEXT = '连接成功';

/**
 * 仓库表单状态。
 */
interface RepoForm {
  /** 仓库名称 */
  name: string;
  /** git 地址 */
  gitUrl: string;
  /** 默认目标分支 */
  defaultBaseBranch: string;
  /** 只读凭据明文，编辑时留空表示不修改 */
  credential: string;
  /** 凭据类型 */
  credentialKind: RepoCredentialKind;
  /** 同步间隔秒数 */
  syncIntervalSeconds: number;
}

/** 仓库列表 */
const repos = ref<Repo[]>([]);
/** 总条数 */
const total = ref(0);
/** 当前页码 */
const page = ref(1);
/** 列表加载状态 */
const loading = ref(false);
/** 正在切换启用状态的仓库主键 */
const activeUpdatingId = ref<string | null>(null);
/** 正在同步的仓库主键 */
const syncingId = ref<string | null>(null);

/** 抽屉是否可见 */
const drawerVisible = ref(false);
/** 正在编辑的仓库，null 表示新建 */
const editingRepo = ref<Repo | null>(null);
/** 保存中状态 */
const saving = ref(false);
/** 表单引用 */
const formRef = ref<FormInstance>();
/** 凭据是否为只读（编辑时未填写表示沿用已保存凭据） */
const editing = computed(() => editingRepo.value !== null);
/** 表单状态 */
const form = reactive<RepoForm>({
  name: '',
  gitUrl: '',
  defaultBaseBranch: DEFAULT_BASE_BRANCH,
  credential: '',
  credentialKind: 'PASSWORD',
  syncIntervalSeconds: DEFAULT_SYNC_INTERVAL,
});

/** 连接测试状态 */
const connectionStatus = ref<'idle' | 'testing' | 'ok' | 'failed'>('idle');
/** 连接测试提示 */
const connectionMessage = ref('');
/** 最近一次连接测试是否失败，失败后未修改地址/凭据前禁止保存 */
const testFailed = ref(false);

/** 成员弹窗是否可见 */
const membersVisible = ref(false);
/** 成员加载状态 */
const membersLoading = ref(false);
/** 成员保存中状态 */
const memberSaving = ref(false);
/** 成员弹窗对应的仓库 */
const activeRepo = ref<Repo | null>(null);
/** 可选用户 */
const users = ref<AdminUser[]>([]);
/** 每个用户是否被选为成员 */
const memberSelected = reactive<Record<string, boolean>>({});
/** 每个用户是否标记为合并人 */
const memberMerger = reactive<Record<string, boolean>>({});

/** 表单校验规则 */
const rules: FormRules<RepoForm> = {
  name: [{ required: true, message: '请输入仓库名称', trigger: 'blur' }],
  gitUrl: [{ required: true, message: '请输入 git 地址', trigger: 'blur' }],
  defaultBaseBranch: [{ required: true, message: '请输入默认目标分支', trigger: 'blur' }],
  syncIntervalSeconds: [{ required: true, message: '请输入同步间隔', trigger: 'blur' }],
};

/** 已选成员主键 */
const selectedUserIds = computed(() =>
  users.value
    .filter((user) => memberSelected[user.id] === true)
    .map((user) => user.id),
);

/** 已选中成员里是否至少有一名合并人 */
const hasMerger = computed(() =>
  selectedUserIds.value.some((userId) => memberMerger[userId] === true),
);

/** 成员保存是否可用 */
const canSaveMembers = computed(
  () => hasMerger.value && !memberSaving.value && !membersLoading.value,
);

/** 保存按钮是否禁用 */
const saveDisabled = computed(
  () => saving.value || (editing.value && testFailed.value),
);

/** 表单标题 */
const drawerTitle = computed(() => (editing.value ? '编辑仓库' : '新建仓库'));

/**
 * 拉取仓库列表。
 */
async function loadRepos(): Promise<void> {
  loading.value = true;
  try {
    const result = await fetchRepos(page.value, PAGE_SIZE);
    repos.value = result.items;
    total.value = result.total;
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '仓库列表加载失败，请稍后重试'));
  } finally {
    loading.value = false;
  }
}

/**
 * 重置连接测试状态。
 */
function resetConnectionState(): void {
  connectionStatus.value = 'idle';
  connectionMessage.value = '';
  testFailed.value = false;
}

/**
 * 用仓库或默认值填充表单。
 * @param repo 编辑的仓库，新建时为 null
 */
function fillForm(repo: Repo | null): void {
  form.name = repo?.name ?? '';
  form.gitUrl = repo?.gitUrl ?? '';
  form.defaultBaseBranch = repo?.defaultBaseBranch ?? DEFAULT_BASE_BRANCH;
  form.credential = '';
  form.credentialKind = 'PASSWORD';
  form.syncIntervalSeconds = repo?.syncIntervalSeconds ?? DEFAULT_SYNC_INTERVAL;
}

/**
 * 打开新建抽屉。
 */
function openCreate(): void {
  editingRepo.value = null;
  fillForm(null);
  resetConnectionState();
  drawerVisible.value = true;
  formRef.value?.clearValidate();
}

/**
 * 打开编辑抽屉。
 * @param repo 待编辑的仓库
 */
function openEdit(repo: Repo): void {
  editingRepo.value = repo;
  fillForm(repo);
  resetConnectionState();
  drawerVisible.value = true;
  formRef.value?.clearValidate();
}

/**
 * 组装新建仓库请求。
 * @returns 新建请求
 */
function buildCreateInput(): CreateRepoInput {
  const input: CreateRepoInput = {
    name: form.name.trim(),
    gitUrl: form.gitUrl.trim(),
    defaultBaseBranch: form.defaultBaseBranch.trim(),
    syncIntervalSeconds: form.syncIntervalSeconds,
  };

  // 未填写凭据时保持匿名的只读访问
  if (form.credential.trim() !== '') {
    input.credential = form.credential.trim();
    input.credentialKind = form.credentialKind;
  }

  return input;
}

/**
 * 组装仓库更新请求，凭据留空时不改动已保存凭据。
 * @returns 更新请求
 */
function buildUpdateInput(): UpdateRepoInput {
  const input: UpdateRepoInput = {
    name: form.name.trim(),
    gitUrl: form.gitUrl.trim(),
    defaultBaseBranch: form.defaultBaseBranch.trim(),
    syncIntervalSeconds: form.syncIntervalSeconds,
  };

  if (form.credential.trim() !== '') {
    input.credential = form.credential.trim();
    input.credentialKind = form.credentialKind;
  }

  return input;
}

/**
 * 保存仓库：新建走 POST，编辑走 PATCH。
 */
async function saveRepo(): Promise<void> {
  const valid = await formRef.value?.validate().catch(() => false);
  if (valid !== true) {
    return;
  }

  saving.value = true;
  try {
    if (editingRepo.value === null) {
      await createRepo(buildCreateInput());
      ElMessage.success('仓库已创建');
    } else {
      await updateRepo(editingRepo.value.id, buildUpdateInput());
      ElMessage.success('仓库已保存');
    }

    drawerVisible.value = false;
    await loadRepos();
  } catch (error) {
    // 保留表单内容，管理员修正后可再次提交
    ElMessage.error(apiErrorMessage(error, '仓库保存失败，请稍后重试'));
  } finally {
    saving.value = false;
  }
}

/**
 * 使用已保存凭据测试连通性。
 */
async function testConnection(): Promise<void> {
  if (editingRepo.value === null) {
    return;
  }

  connectionStatus.value = 'testing';
  connectionMessage.value = '';
  try {
    await testRepoConnection(editingRepo.value.id);
    connectionStatus.value = 'ok';
    connectionMessage.value = CONNECTION_OK_TEXT;
    testFailed.value = false;
  } catch (error) {
    connectionStatus.value = 'failed';
    connectionMessage.value = apiErrorMessage(error, '连接测试失败，请检查地址与凭据');
    testFailed.value = true;
  }
}

/**
 * 手动同步仓库，随后刷新列表展示最近同步时间与错误。
 * @param repo 目标仓库
 */
async function handleSync(repo: Repo): Promise<void> {
  syncingId.value = repo.id;
  try {
    await syncRepo(repo.id);
    ElMessage.success('同步完成');
  } catch (error) {
    ElMessage.error(syncErrorMessage(error));
  } finally {
    syncingId.value = null;
    await loadRepos();
  }
}

/**
 * 把同步异常转换为可展示文案，429 统一提示稍后重试。
 * @param error 捕获到的异常
 * @returns 可展示的错误信息
 */
function syncErrorMessage(error: unknown): string {
  if (axios.isAxiosError(error) && error.response?.status === 429) {
    return '同步过于频繁，请稍后重试';
  }

  return apiErrorMessage(error, '同步失败，请稍后重试');
}

/**
 * 切换仓库启用状态。
 * @param repo 目标仓库
 * @param value 开关的新值
 */
async function handleActiveChange(repo: Repo, value: string | number | boolean): Promise<void> {
  activeUpdatingId.value = repo.id;
  try {
    await updateRepo(repo.id, { isActive: Boolean(value) });
    repo.isActive = Boolean(value);
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '仓库状态更新失败，请稍后重试'));
    await loadRepos();
  } finally {
    activeUpdatingId.value = null;
  }
}

/**
 * 清空成员勾选状态。
 */
function clearMemberSelection(): void {
  Object.keys(memberSelected).forEach((key) => {
    delete memberSelected[key];
  });
  Object.keys(memberMerger).forEach((key) => {
    delete memberMerger[key];
  });
}

/**
 * 用已有成员回填勾选状态。
 * @param members 已有成员列表
 */
function applyMemberSelection(members: { userId: string; isMerger: boolean }[]): void {
  members.forEach((member) => {
    memberSelected[member.userId] = true;
    memberMerger[member.userId] = member.isMerger;
  });
}

/**
 * 打开成员弹窗并加载全部用户与已有成员。
 * @param repo 目标仓库
 */
async function openMembers(repo: Repo): Promise<void> {
  activeRepo.value = repo;
  membersVisible.value = true;
  membersLoading.value = true;
  users.value = [];
  clearMemberSelection();
  try {
    const [userPage, existing] = await Promise.all([
      fetchUsers(1, MAX_USERS),
      fetchRepoMembers(repo.id),
    ]);
    users.value = userPage.items;
    applyMemberSelection(existing);
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '成员数据加载失败，请稍后重试'));
  } finally {
    membersLoading.value = false;
  }
}

/**
 * 保存成员整体列表，至少保留一名合并人。
 */
async function saveMembers(): Promise<void> {
  const repo = activeRepo.value;
  if (repo === null || !hasMerger.value) {
    return;
  }

  memberSaving.value = true;
  try {
    const members = selectedUserIds.value.map((userId) => ({
      userId,
      isMerger: memberMerger[userId] === true,
    }));
    await replaceRepoMembers(repo.id, members);
    ElMessage.success('成员已保存');
    membersVisible.value = false;
    await loadRepos();
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '成员保存失败，请稍后重试'));
  } finally {
    memberSaving.value = false;
  }
}

// 地址或凭据变化后，之前的连接测试结果作废，需要重新测试
watch([() => form.gitUrl, () => form.credential, () => form.credentialKind], () => {
  if (connectionStatus.value !== 'idle') {
    resetConnectionState();
  }
});

// 页码变化时加载对应页
watch(page, () => {
  void loadRepos();
});

onMounted(() => {
  void loadRepos();
});
</script>

<template>
  <section class="repo-admin">
    <header class="repo-admin-header">
      <h2 class="repo-admin-title">仓库管理</h2>
      <el-button type="primary" class="create-repo-button" @click="openCreate">
        <svg
          class="repo-admin-create-icon"
          viewBox="0 0 24 24"
          width="14"
          height="14"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          aria-hidden="true"
        >
          <path d="M12 5v14M5 12h14" />
        </svg>
        新建仓库
      </el-button>
    </header>

    <el-table
      v-loading="loading"
      :data="repos"
      border
      stripe
      empty-text="暂无仓库"
    >
      <el-table-column prop="name" label="名称" min-width="150" show-overflow-tooltip />

      <el-table-column prop="gitUrl" label="git 地址" min-width="240" show-overflow-tooltip />

      <el-table-column
        prop="defaultBaseBranch"
        label="默认分支"
        width="120"
        align="center"
        show-overflow-tooltip
      />

      <el-table-column label="最近同步" width="160" align="center">
        <template #default="{ row }">
          {{ row.lastFetchedAt === null ? '从未同步' : formatDateTime(row.lastFetchedAt) }}
        </template>
      </el-table-column>

      <el-table-column label="同步状态" width="110" align="center">
        <template #default="{ row }">
          <el-tooltip v-if="row.lastError !== null" :content="row.lastError" placement="top">
            <el-tag type="danger" size="small">同步失败</el-tag>
          </el-tooltip>
          <el-tag v-else type="success" size="small">正常</el-tag>
        </template>
      </el-table-column>

      <el-table-column label="启用" width="90" align="center">
        <template #default="{ row }">
          <el-switch
            :model-value="row.isActive"
            :loading="activeUpdatingId === row.id"
            @change="handleActiveChange(row, $event)"
          />
        </template>
      </el-table-column>

      <el-table-column prop="memberCount" label="成员数" width="90" align="center" />

      <el-table-column label="操作" width="220" align="center">
        <template #default="{ row }">
          <el-button link type="primary" size="small" class="edit-repo-button" @click="openEdit(row)">
            编辑
          </el-button>
          <el-button link type="primary" size="small" class="members-repo-button" @click="openMembers(row)">
            成员
          </el-button>
          <el-button
            link
            type="primary"
            size="small"
            class="sync-repo-button"
            :loading="syncingId === row.id"
            @click="handleSync(row)"
          >
            同步
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-pagination
      v-model:current-page="page"
      class="repo-admin-pagination"
      background
      layout="total, prev, pager, next"
      :page-size="PAGE_SIZE"
      :total="total"
    />

    <el-drawer v-model="drawerVisible" :title="drawerTitle" size="480px" class="repo-drawer">
      <el-form ref="formRef" :model="form" :rules="rules" label-width="120px">
        <el-form-item label="仓库名称" prop="name">
          <el-input v-model="form.name" placeholder="请输入仓库名称" />
        </el-form-item>

        <el-form-item label="git 地址" prop="gitUrl">
          <el-input v-model="form.gitUrl" placeholder="请输入 git 地址" />
        </el-form-item>

        <el-form-item label="默认目标分支" prop="defaultBaseBranch">
          <el-input v-model="form.defaultBaseBranch" placeholder="请输入默认目标分支" />
        </el-form-item>

        <el-form-item label="凭据类型">
          <el-radio-group v-model="form.credentialKind">
            <el-radio value="PASSWORD">密码</el-radio>
            <el-radio value="TOKEN">访问令牌</el-radio>
          </el-radio-group>
        </el-form-item>

        <el-form-item label="只读凭据">
          <el-input
            v-model="form.credential"
            placeholder="username:secret"
            autocomplete="off"
          />
          <p class="repo-drawer-hint">
            格式为 username:secret；{{ editing ? '留空表示不修改已保存凭据。' : '留空表示匿名访问。' }}
          </p>
        </el-form-item>

        <el-form-item label="同步间隔" prop="syncIntervalSeconds">
          <el-input-number v-model="form.syncIntervalSeconds" :min="1" :step="30" />
          <span class="repo-drawer-unit">秒</span>
        </el-form-item>

        <el-form-item v-if="editing" label="连接测试">
          <div class="repo-drawer-connection">
            <el-button
              class="test-connection-button"
              :loading="connectionStatus === 'testing'"
              :disabled="connectionStatus === 'testing'"
              @click="testConnection"
            >
              测试连接
            </el-button>
            <span
              v-if="connectionMessage !== ''"
              class="connection-result"
              :class="connectionStatus === 'ok' ? 'connection-ok' : 'connection-failed'"
            >
              {{ connectionMessage }}
            </span>
          </div>
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="drawerVisible = false">取消</el-button>
        <el-button
          type="primary"
          class="repo-drawer-save"
          :loading="saving"
          :disabled="saveDisabled"
          @click="saveRepo"
        >
          保存
        </el-button>
      </template>
    </el-drawer>

    <el-dialog v-model="membersVisible" title="仓库成员" width="560px" class="members-dialog">
      <div v-loading="membersLoading">
        <el-table :data="users" border stripe empty-text="暂无可用用户">
          <el-table-column label="成员" width="80" align="center">
            <template #default="{ row }">
              <el-checkbox v-model="memberSelected[row.id]" class="member-select" />
            </template>
          </el-table-column>

          <el-table-column prop="username" label="用户名" min-width="130" show-overflow-tooltip />

          <el-table-column prop="displayName" label="显示名" min-width="130" show-overflow-tooltip />

          <el-table-column label="合并人" width="90" align="center">
            <template #default="{ row }">
              <el-checkbox
                v-model="memberMerger[row.id]"
                class="member-merger"
                :disabled="memberSelected[row.id] !== true"
              />
            </template>
          </el-table-column>
        </el-table>
        <p class="members-hint">至少需要一名合并人，合并人可标记评审单已合并。</p>
      </div>

      <template #footer>
        <el-button @click="membersVisible = false">取消</el-button>
        <el-button
          type="primary"
          class="members-save"
          :loading="memberSaving"
          :disabled="!canSaveMembers"
          @click="saveMembers"
        >
          保存
        </el-button>
      </template>
    </el-dialog>
  </section>
</template>

<style scoped>
.repo-admin-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 16px;
}

.repo-admin-title {
  margin: 0;
  font-size: 18px;
}

.repo-admin-create-icon {
  margin-right: 4px;
}

.repo-admin-pagination {
  margin-top: 16px;
  justify-content: flex-end;
}

.repo-drawer-hint {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.repo-drawer-unit {
  margin-left: 8px;
  color: var(--el-text-color-secondary);
}

.repo-drawer-connection {
  display: flex;
  align-items: center;
  gap: 12px;
}

.connection-ok {
  color: var(--el-color-success);
}

.connection-failed {
  color: var(--el-color-danger);
}

.members-hint {
  margin: 12px 0 0;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}
</style>

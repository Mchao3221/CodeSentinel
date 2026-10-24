<script setup lang="ts">
/**
 * 新建评审页：仓库 → 目标分支 → 我的分支 → 审核人 → 标题与说明。
 */
import { computed, onMounted, reactive, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import axios from 'axios';
import { ElMessage, type FormInstance, type FormRules } from 'element-plus';

import {
  fetchBranches,
  fetchRepoMembers,
  fetchRepos,
  fetchUnreviewedBranches,
} from '../api/repos';
import { createReview } from '../api/reviews';
import type {
  ActiveReviewConflict,
  Branch,
  Repo,
  RepoMember,
  UnreviewedBranch,
} from '../api/types';
import { useAuthStore } from '../stores/auth';

/** 标题最大长度，与后端字段一致 */
const TITLE_MAX_LENGTH = 200;

/**
 * 新建评审表单状态。
 */
interface CreateReviewForm {
  /** 仓库主键 */
  repoId: string;
  /** 目标分支 */
  baseBranch: string;
  /** 提交分支 */
  headBranch: string;
  /** 审核人主键列表 */
  reviewerIds: string[];
  /** 标题 */
  title: string;
  /** 评审说明 */
  description: string;
}

/**
 * 我的分支下拉分组。
 */
interface BranchGroup {
  /** 分组标题 */
  label: string;
  /** 分组内的分支选项 */
  options: { value: string; label: string }[];
}

const authStore = useAuthStore();
const router = useRouter();
const formRef = ref<FormInstance>();
const form = reactive<CreateReviewForm>({
  repoId: '',
  baseBranch: '',
  headBranch: '',
  reviewerIds: [],
  title: '',
  description: '',
});

/** 可访问的仓库 */
const repos = ref<Repo[]>([]);
/** 当前仓库的全部分支 */
const branches = ref<Branch[]>([]);
/** 当前仓库的未开单分支 */
const unreviewedBranches = ref<UnreviewedBranch[]>([]);
/** 当前仓库的成员 */
const members = ref<RepoMember[]>([]);
/** 仓库下拉加载状态 */
const loadingRepos = ref(false);
/** 分支与成员加载状态 */
const loadingRepoData = ref(false);
/** 分支接口是否失败，失败时展示页内错误与重试入口 */
const branchesFailed = ref(false);
/** 成员接口是否失败，失败时审核人列表为空并展示重试提示 */
const membersFailed = ref(false);
/** 提交中状态 */
const submitting = ref(false);
/** 冲突的已有评审单主键，非空时展示跳转提示 */
const conflictReviewId = ref<string | null>(null);

/** 表单校验规则 */
const rules: FormRules<CreateReviewForm> = {
  title: [
    { required: true, message: '请输入标题', trigger: 'blur' },
    { max: TITLE_MAX_LENGTH, message: `标题不能超过 ${TITLE_MAX_LENGTH} 个字符`, trigger: 'blur' },
  ],
};

/** 当前选中的仓库 */
const selectedRepo = computed(
  () => repos.value.find((repo) => repo.id === form.repoId) ?? null,
);

/** 目标分支候选；镜像未同步时仍保留仓库默认分支 */
const baseBranchOptions = computed<string[]>(() => {
  const names = branches.value.map((branch) => branch.name);
  const repo = selectedRepo.value;
  if (repo !== null && !names.includes(repo.defaultBaseBranch)) {
    return [repo.defaultBaseBranch, ...names];
  }

  return names;
});

/** 我的分支候选：未开单分支置顶并标注领先提交数 */
const headBranchGroups = computed<BranchGroup[]>(() => {
  const base = form.baseBranch;
  const unreviewedNames = new Set(unreviewedBranches.value.map((branch) => branch.name));
  const pending = unreviewedBranches.value
    .filter((branch) => branch.name !== base)
    .map((branch) => ({
      value: branch.name,
      label: `${branch.name}（领先 ${branch.aheadBy} 个提交）`,
    }));
  const others = branches.value
    .filter((branch) => branch.name !== base && !unreviewedNames.has(branch.name))
    .map((branch) => ({ value: branch.name, label: branch.name }));

  const groups: BranchGroup[] = [];
  if (pending.length > 0) {
    groups.push({ label: '未开单分支', options: pending });
  }
  if (others.length > 0) {
    groups.push({ label: '其他分支', options: others });
  }
  return groups;
});

/** 审核人候选，排除当前用户 */
const reviewerOptions = computed(() =>
  members.value.filter((member) => member.userId !== authStore.user?.id),
);

/** 仓库没有可选分支时展示空状态提示 */
const showEmptyBranchHint = computed(
  () =>
    form.repoId !== ''
    && !loadingRepoData.value
    && !branchesFailed.value
    && headBranchGroups.value.length === 0,
);

/** 表单是否满足提交条件 */
const formReady = computed(
  () =>
    form.repoId !== ''
    && form.headBranch !== ''
    && form.reviewerIds.length > 0
    && form.title.trim().length > 0,
);

/**
 * 加载当前仓库的分支与成员；各接口独立降级，任一失败不阻塞提交。
 * @param repoId 仓库主键
 */
async function loadRepoData(repoId: string): Promise<void> {
  loadingRepoData.value = true;
  branchesFailed.value = false;
  membersFailed.value = false;
  try {
    const [branchResult, unreviewedResult, memberResult] = await Promise.allSettled([
      fetchBranches(repoId),
      fetchUnreviewedBranches(repoId),
      fetchRepoMembers(repoId),
    ] as const);

    // 用户已切换仓库时丢弃过期响应
    if (form.repoId !== repoId) {
      return;
    }

    branches.value = branchResult.status === 'fulfilled' ? branchResult.value : [];
    branchesFailed.value = branchResult.status === 'rejected';

    // 未开单分支计算失败时降级为普通分支列表，不阻塞提交
    unreviewedBranches.value =
      unreviewedResult.status === 'fulfilled' ? unreviewedResult.value : [];

    members.value = memberResult.status === 'fulfilled' ? memberResult.value : [];
    membersFailed.value = memberResult.status === 'rejected';
  } finally {
    loadingRepoData.value = false;
  }
}

/**
 * 重新加载当前仓库的分支与成员。
 */
async function retryLoadRepoData(): Promise<void> {
  if (form.repoId === '') {
    return;
  }

  await loadRepoData(form.repoId);
}

/**
 * 切换仓库后重置分支与审核人，并加载仓库的分支和成员。
 * @param repoId 新选中的仓库主键
 */
async function handleRepoChange(repoId: string): Promise<void> {
  form.baseBranch = '';
  form.headBranch = '';
  form.reviewerIds = [];
  branches.value = [];
  unreviewedBranches.value = [];
  members.value = [];
  conflictReviewId.value = null;

  const repo = repos.value.find((item) => item.id === repoId);
  if (repo === undefined) {
    branchesFailed.value = false;
    membersFailed.value = false;
    return;
  }

  form.baseBranch = repo.defaultBaseBranch;
  await loadRepoData(repoId);
}

/**
 * 拉取当前用户可访问的仓库。
 */
async function loadRepos(): Promise<void> {
  loadingRepos.value = true;
  try {
    const result = await fetchRepos();
    repos.value = result.items;
  } catch {
    ElMessage.error('仓库列表加载失败，请稍后重试');
  } finally {
    loadingRepos.value = false;
  }
}

/**
 * 判断是否为“该分支已有活跃评审单”的冲突响应。
 * @param error 捕获到的异常
 * @returns 冲突响应体，非冲突时为 null
 */
function activeReviewConflict(error: unknown): ActiveReviewConflict | null {
  if (!axios.isAxiosError<ActiveReviewConflict>(error)) {
    return null;
  }

  if (error.response?.status !== 409) {
    return null;
  }

  const body = error.response.data;
  return body?.code === 'ACTIVE_REVIEW_EXISTS' ? body : null;
}

/**
 * 统一处理创建失败：区分活跃评审冲突与其他错误。
 * @param error 捕获到的异常
 */
function handleSubmitError(error: unknown): void {
  const conflict = activeReviewConflict(error);
  if (conflict !== null) {
    conflictReviewId.value = conflict.reviewId;
    ElMessage.warning('该分支已有评审单');
    return;
  }

  ElMessage.error('创建评审单失败，请稍后重试');
}

/**
 * 提交评审单；409 时保留表单并展示已有评审单跳转入口。
 */
async function handleSubmit(): Promise<void> {
  if (!formReady.value || submitting.value) {
    return;
  }

  const valid = await formRef.value?.validate().catch(() => false);
  if (valid === false) {
    return;
  }

  submitting.value = true;
  conflictReviewId.value = null;
  try {
    const created = await createReview({
      repoId: form.repoId,
      baseBranch: form.baseBranch,
      headBranch: form.headBranch,
      title: form.title.trim(),
      description: form.description.trim() === '' ? undefined : form.description.trim(),
      reviewerIds: [...form.reviewerIds],
    });
    ElMessage.success('评审单已创建');
    await router.push(`/reviews/${created.id}`);
  } catch (error) {
    handleSubmitError(error);
  } finally {
    submitting.value = false;
  }
}

/**
 * 打开冲突提示中已有的评审单。
 */
async function openExistingReview(): Promise<void> {
  if (conflictReviewId.value === null) {
    return;
  }

  await router.push(`/reviews/${conflictReviewId.value}`);
}

/**
 * 返回评审列表。
 */
async function goBack(): Promise<void> {
  await router.push('/reviews');
}

// 切换仓库时加载分支与成员
watch(
  () => form.repoId,
  (repoId) => {
    void handleRepoChange(repoId);
  },
);

// 目标分支变化时，清除与新目标分支相同的提交分支
watch(
  () => form.baseBranch,
  (baseBranch) => {
    if (form.headBranch === baseBranch) {
      form.headBranch = '';
    }
  },
);

onMounted(() => {
  void loadRepos();
});
</script>

<template>
  <section class="review-create">
    <header class="review-create-header">
      <h2 class="review-create-title">新建评审</h2>
      <el-button @click="goBack">返回列表</el-button>
    </header>

    <el-alert
      v-if="conflictReviewId !== null"
      class="review-create-conflict"
      type="warning"
      title="该分支已有评审单"
      show-icon
      :closable="false"
    >
      <el-button link type="primary" @click="openExistingReview">查看已有评审单</el-button>
    </el-alert>

    <el-alert
      v-if="branchesFailed"
      class="review-create-branches-error review-create-load-error"
      type="error"
      title="分支加载失败，请重新加载"
      show-icon
      :closable="false"
    >
      <el-button link type="primary" @click="retryLoadRepoData">重新加载</el-button>
    </el-alert>

    <el-alert
      v-if="membersFailed"
      class="review-create-members-error review-create-load-error"
      type="warning"
      title="审核人加载失败，请重新加载"
      show-icon
      :closable="false"
    >
      <el-button link type="primary" @click="retryLoadRepoData">重新加载</el-button>
    </el-alert>

    <el-form
      ref="formRef"
      class="review-create-form"
      :model="form"
      :rules="rules"
      label-width="96px"
    >
      <el-form-item label="仓库" prop="repoId">
        <el-select
          v-model="form.repoId"
          class="review-create-select"
          placeholder="请选择仓库"
          :loading="loadingRepos"
        >
          <el-option
            v-for="repo in repos"
            :key="repo.id"
            :label="repo.name"
            :value="repo.id"
          />
        </el-select>
      </el-form-item>

      <el-form-item label="目标分支" prop="baseBranch">
        <el-select
          v-model="form.baseBranch"
          class="review-create-select"
          placeholder="请选择目标分支"
          :loading="loadingRepoData"
        >
          <el-option
            v-for="branchName in baseBranchOptions"
            :key="branchName"
            :label="branchName"
            :value="branchName"
          />
        </el-select>
      </el-form-item>

      <el-form-item label="我的分支" prop="headBranch">
        <el-select
          v-model="form.headBranch"
          class="review-create-select"
          placeholder="请选择我的分支"
          :loading="loadingRepoData"
        >
          <el-option-group
            v-for="group in headBranchGroups"
            :key="group.label"
            :label="group.label"
          >
            <el-option
              v-for="option in group.options"
              :key="option.value"
              :label="option.label"
              :value="option.value"
            />
          </el-option-group>
        </el-select>
        <p v-if="showEmptyBranchHint" class="review-create-empty-hint">
          该仓库暂无可选分支
        </p>
      </el-form-item>

      <el-form-item label="审核人" prop="reviewerIds">
        <el-select
          v-model="form.reviewerIds"
          class="review-create-select"
          placeholder="请选择审核人"
          multiple
          collapse-tags
          :loading="loadingRepoData"
        >
          <el-option
            v-for="member in reviewerOptions"
            :key="member.userId"
            :label="`${member.displayName}（${member.username}）`"
            :value="member.userId"
          />
        </el-select>
      </el-form-item>

      <el-form-item label="标题" prop="title">
        <el-input
          v-model="form.title"
          class="review-create-title-input"
          placeholder="请输入标题"
          :maxlength="TITLE_MAX_LENGTH"
          show-word-limit
        />
      </el-form-item>

      <el-form-item label="说明" prop="description">
        <el-input
          v-model="form.description"
          type="textarea"
          :rows="5"
          placeholder="请输入评审说明（可选）"
        />
      </el-form-item>

      <el-form-item>
        <el-button
          class="submit-button"
          type="primary"
          :loading="submitting"
          :disabled="!formReady || submitting"
          @click="handleSubmit"
        >
          提交评审
        </el-button>
      </el-form-item>
    </el-form>
  </section>
</template>

<style scoped>
.review-create {
  max-width: 720px;
}

.review-create-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 16px;
}

.review-create-title {
  margin: 0;
  font-size: 20px;
}

.review-create-conflict {
  margin-bottom: 16px;
}

.review-create-load-error {
  margin-bottom: 16px;
}

.review-create-empty-hint {
  margin: 4px 0 0;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.review-create-select {
  width: 100%;
}
</style>

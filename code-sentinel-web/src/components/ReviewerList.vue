<script setup lang="ts">
/**
 * 审核人列表：展示结论状态与附言，提交人或管理员可追加、移除审核人。
 */
import { computed, ref } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';

import { fetchRepoMembers } from '../api/repos';
import { addReviewers, removeReviewer } from '../api/reviews';
import type { RepoMember, ReviewerState, ReviewReviewer } from '../api/types';
import { apiErrorMessage } from '../utils/apiError';

/**
 * 审核人状态元数据。
 */
interface StateMeta {
  /** 中文标签 */
  label: string;
  /** 标签类型 */
  type: 'info' | 'success' | 'warning' | 'danger';
}

/** 四种审核人状态的展示映射 */
const STATE_META: Record<ReviewerState, StateMeta> = {
  PENDING: { label: '待评审', type: 'info' },
  APPROVED: { label: '通过', type: 'success' },
  CHANGES_REQUESTED: { label: '要求修改', type: 'warning' },
  REMOVED: { label: '已移除', type: 'info' },
};

const props = defineProps<{
  /** 评审单主键 */
  reviewId: string;
  /** 所属仓库主键 */
  repoId: string;
  /** 提交人主键 */
  submitterId: string;
  /** 当前用户是否管理员 */
  isAdmin: boolean;
  /** 能否管理审核人 */
  canManageReviewers: boolean;
  /** 审核人列表 */
  reviewers: ReviewReviewer[];
}>();

const emit = defineEmits<{
  /** 审核人发生变化，需要刷新详情 */
  changed: [];
}>();

/** 添加审核人弹窗是否可见 */
const dialogVisible = ref(false);
/** 仓库成员 */
const members = ref<RepoMember[]>([]);
/** 选中的待新增成员 */
const selectedUserIds = ref<string[]>([]);
/** 成员加载状态 */
const loadingMembers = ref(false);
/** 新增提交状态 */
const adding = ref(false);
/** 正在移除的审核人主键 */
const removingUserId = ref<string | null>(null);

/** 已在评审单上的用户主键，包含已移除的人 */
const existingUserIds = computed(() => new Set(props.reviewers.map((item) => item.userId)));

/** 可新增的成员：排除提交人与已有审核人 */
const availableMembers = computed(() =>
  members.value.filter(
    (member) => member.userId !== props.submitterId && !existingUserIds.value.has(member.userId),
  ),
);

/** 是否可以提交新增 */
const canSubmitAdd = computed(
  () => selectedUserIds.value.length > 0 && !adding.value && !loadingMembers.value,
);

/**
 * 读取审核人状态对应的标签信息。
 * @param state 审核人状态
 * @returns 标签元数据
 */
function stateMeta(state: ReviewerState): StateMeta {
  return STATE_META[state];
}

/**
 * 判断当前用户能否移除某位审核人：管理员可移除任何人，提交人只能移除待评审的人。
 * @param reviewer 审核人
 * @returns 是否显示移除入口
 */
function canRemove(reviewer: ReviewReviewer): boolean {
  if (!props.canManageReviewers || reviewer.state === 'REMOVED') {
    return false;
  }

  return props.isAdmin || reviewer.state === 'PENDING';
}

/**
 * 打开添加审核人弹窗并加载仓库成员。
 */
async function openDialog(): Promise<void> {
  selectedUserIds.value = [];
  dialogVisible.value = true;
  loadingMembers.value = true;
  try {
    members.value = await fetchRepoMembers(props.repoId);
  } catch (error) {
    members.value = [];
    ElMessage.error(apiErrorMessage(error, '仓库成员加载失败，请稍后重试'));
  } finally {
    loadingMembers.value = false;
  }
}

/**
 * 提交新增审核人，成功后通知父级刷新详情。
 */
async function submitAdd(): Promise<void> {
  if (!canSubmitAdd.value) {
    return;
  }

  adding.value = true;
  try {
    await addReviewers(props.reviewId, [...selectedUserIds.value]);
    ElMessage.success('审核人已添加');
    dialogVisible.value = false;
    emit('changed');
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '添加审核人失败，请稍后重试'));
  } finally {
    adding.value = false;
  }
}

/**
 * 二次确认后移除审核人，成功后通知父级刷新详情。
 * @param reviewer 待移除的审核人
 */
async function remove(reviewer: ReviewReviewer): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确认移除审核人 ${reviewer.displayName}？`,
      '移除审核人',
      { type: 'warning', confirmButtonText: '移除', cancelButtonText: '取消' },
    );
  } catch {
    return;
  }

  removingUserId.value = reviewer.userId;
  try {
    await removeReviewer(props.reviewId, reviewer.userId);
    ElMessage.success('审核人已移除');
    emit('changed');
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '移除审核人失败，请稍后重试'));
  } finally {
    removingUserId.value = null;
  }
}
</script>

<template>
  <section class="reviewer-list">
    <header class="reviewer-list-header">
      <h3 class="reviewer-list-title">审核人</h3>
      <el-button
        v-if="canManageReviewers"
        class="add-reviewers-button"
        @click="openDialog"
      >
        添加审核人
      </el-button>
    </header>

    <ul class="reviewer-list-items">
      <li
        v-for="reviewer in reviewers"
        :key="reviewer.userId"
        class="reviewer-row"
      >
        <span class="reviewer-name">{{ reviewer.displayName }}</span>
        <el-tag class="reviewer-state-tag" :type="stateMeta(reviewer.state).type">
          {{ stateMeta(reviewer.state).label }}
        </el-tag>
        <el-tooltip
          v-if="reviewer.note !== null"
          class="reviewer-note"
          :content="reviewer.note"
          placement="top"
        >
          <span class="reviewer-note-label">附言</span>
        </el-tooltip>
        <el-button
          v-if="canRemove(reviewer)"
          class="remove-reviewer-button"
          link
          type="danger"
          :loading="removingUserId === reviewer.userId"
          :disabled="removingUserId !== null"
          @click="remove(reviewer)"
        >
          移除
        </el-button>
      </li>
    </ul>

    <el-dialog
      v-model="dialogVisible"
      class="add-reviewer-dialog"
      title="添加审核人"
      width="480px"
    >
      <el-select
        v-model="selectedUserIds"
        class="add-reviewer-select"
        placeholder="请选择审核人"
        multiple
        collapse-tags
        :loading="loadingMembers"
      >
        <el-option
          v-for="member in availableMembers"
          :key="member.userId"
          :label="`${member.displayName}（${member.username}）`"
          :value="member.userId"
        />
      </el-select>
      <p v-if="!loadingMembers && availableMembers.length === 0" class="add-reviewer-empty">
        暂无可添加的成员
      </p>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button
          class="confirm-add-reviewers-button"
          type="primary"
          :disabled="!canSubmitAdd"
          :loading="adding"
          @click="submitAdd"
        >
          确定
        </el-button>
      </template>
    </el-dialog>
  </section>
</template>

<style scoped>
.reviewer-list-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.reviewer-list-title {
  margin: 0;
  font-size: 16px;
}

.reviewer-list-items {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 12px 0 0;
  padding: 0;
  list-style: none;
}

.reviewer-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.reviewer-name {
  min-width: 96px;
}

.reviewer-note-label {
  color: var(--el-color-primary);
  cursor: help;
  font-size: 12px;
}

.add-reviewer-select {
  width: 100%;
}

.add-reviewer-empty {
  margin: 8px 0 0;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
</style>

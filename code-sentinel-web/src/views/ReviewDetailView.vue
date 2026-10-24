<script setup lang="ts">
/**
 * 评审详情页：组合头部、操作区、合并指引与审核人列表，并为后续 diff 与评论预留出口。
 */
import { computed, onMounted, reactive, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ElMessage, type FormInstance, type FormRules } from 'element-plus';

import { closeReview, decideReview, fetchReviewDetail, refreshReview } from '../api/reviews';
import type { ReviewDetail, ReviewRevision, ReviewStatus } from '../api/types';
import DiffFileList from '../components/diff/DiffFileList.vue';
import MergeGuide from '../components/MergeGuide.vue';
import ReviewerList from '../components/ReviewerList.vue';
import ReviewHeader from '../components/ReviewHeader.vue';
import { useAuthStore } from '../stores/auth';
import { apiErrorMessage, refreshErrorMessage } from '../utils/apiError';

/** 仍可操作的状态集合 */
const ACTIVE_STATUSES: ReviewStatus[] = ['OPEN', 'CHANGES_REQUESTED', 'APPROVED'];

/** 结论附言最大长度，与后端一致 */
const NOTE_MAX_LENGTH = 2000;

/** 关闭原因最大长度，与后端一致 */
const CLOSE_REASON_MAX_LENGTH = 500;

/**
 * 要求修改表单。
 */
interface ChangesRequestForm {
  /** 修改说明 */
  note: string;
}

/**
 * 关闭评审单表单。
 */
interface CloseForm {
  /** 关闭原因，可选 */
  reason: string;
}

const props = defineProps<{
  /** 评审单主键 */
  id: string;
}>();

const route = useRoute();
const router = useRouter();
const authStore = useAuthStore();

/** 评审单详情 */
const detail = ref<ReviewDetail | null>(null);
/** 详情加载状态 */
const loading = ref(false);
/** 操作进行中状态，统一禁用操作入口 */
const busy = ref(false);

/** 要求修改表单实例 */
const changesFormRef = ref<FormInstance>();
/** 要求修改表单 */
const changesForm = reactive<ChangesRequestForm>({ note: '' });
/** 要求修改弹窗是否可见 */
const changesDialogVisible = ref(false);

/** 要求修改校验规则：说明必填且不超长 */
const changesRules: FormRules<ChangesRequestForm> = {
  note: [
    { required: true, message: '请填写修改说明', trigger: 'blur' },
    {
      max: NOTE_MAX_LENGTH,
      message: `修改说明不能超过 ${NOTE_MAX_LENGTH} 个字符`,
      trigger: 'blur',
    },
  ],
};

/** 关闭表单实例 */
const closeFormRef = ref<FormInstance>();
/** 关闭表单 */
const closeForm = reactive<CloseForm>({ reason: '' });
/** 关闭弹窗是否可见 */
const closeDialogVisible = ref(false);

/** 关闭原因校验规则：只限制长度 */
const closeRules: FormRules<CloseForm> = {
  reason: [
    {
      max: CLOSE_REASON_MAX_LENGTH,
      message: `关闭原因不能超过 ${CLOSE_REASON_MAX_LENGTH} 个字符`,
      trigger: 'blur',
    },
  ],
};

/** 详情中的版本列表 */
const revisions = computed<ReviewRevision[]>(() => detail.value?.revisions ?? []);

/** 当前选中的版本；路由参数非法或缺失时回退到最新版本 */
const selectedRevision = computed<ReviewRevision | null>(() => {
  if (revisions.value.length === 0) {
    return null;
  }

  const requested = typeof route.query.revision === 'string' ? route.query.revision : '';
  return revisions.value.find((item) => item.id === requested)
    ?? revisions.value[revisions.value.length - 1];
});

/** 当前选中版本主键 */
const selectedRevisionId = computed(() => selectedRevision.value?.id ?? '');

/** 当前版本主键（最新版本） */
const currentRevisionId = computed(() =>
  revisions.value.length === 0 ? '' : revisions.value[revisions.value.length - 1].id,
);

/** 是否正在查看历史版本 */
const isHistorical = computed(
  () => selectedRevisionId.value !== '' && selectedRevisionId.value !== currentRevisionId.value,
);

/** 当前状态是否仍可操作 */
const isActive = computed(
  () => detail.value !== null && ACTIVE_STATUSES.includes(detail.value.status),
);

/** 当前用户在评审单上的审核人行 */
const currentUserReviewer = computed(
  () =>
    detail.value?.reviewers.find(
      (item) => item.userId === authStore.user?.id && item.state !== 'REMOVED',
    ) ?? null,
);

/** 是否展示通过与要求修改 */
const showDecisionActions = computed(
  () => isActive.value && detail.value?.permissions.canDecide === true,
);

/** 是否展示撤回结论 */
const canWithdraw = computed(
  () =>
    showDecisionActions.value
    && currentUserReviewer.value !== null
    && (currentUserReviewer.value.state === 'APPROVED'
      || currentUserReviewer.value.state === 'CHANGES_REQUESTED'),
);

/**
 * 拉取评审单详情。
 */
async function loadDetail(): Promise<void> {
  loading.value = true;
  try {
    detail.value = await fetchReviewDetail(props.id);
  } catch {
    ElMessage.error('评审详情加载失败，请稍后重试');
  } finally {
    loading.value = false;
  }
}

/**
 * 执行一次会改动评审单的操作，成功后重新拉取详情。
 * @param action 接口调用
 * @param successText 成功提示
 * @returns 操作是否成功
 */
async function runAction(
  action: () => Promise<unknown>,
  successText: string,
): Promise<boolean> {
  if (busy.value) {
    return false;
  }

  busy.value = true;
  try {
    await action();
    ElMessage.success(successText);
    await loadDetail();
    return true;
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '操作失败，请稍后重试'));
    return false;
  } finally {
    busy.value = false;
  }
}

/**
 * 通过评审。
 */
function approve(): void {
  void runAction(() => decideReview(props.id, { state: 'APPROVED' }), '已通过');
}

/**
 * 打开要求修改弹窗并清空上次输入。
 */
function openChangesDialog(): void {
  changesForm.note = '';
  changesDialogVisible.value = true;
}

/**
 * 提交要求修改，附言必填。
 */
async function submitChangesRequest(): Promise<void> {
  const valid = await changesFormRef.value?.validate().catch(() => false);
  if (valid === false) {
    return;
  }

  const note = changesForm.note.trim();
  if (note === '') {
    ElMessage.warning('请填写修改说明');
    return;
  }

  const done = await runAction(
    () => decideReview(props.id, { state: 'CHANGES_REQUESTED', note }),
    '已提交修改要求',
  );
  if (done) {
    changesDialogVisible.value = false;
  }
}

/**
 * 撤回当前用户此前的结论。
 */
function withdraw(): void {
  void runAction(() => decideReview(props.id, { state: 'PENDING' }), '已撤回结论');
}

/**
 * 打开关闭评审弹窗并清空上次输入。
 */
function openCloseDialog(): void {
  closeForm.reason = '';
  closeDialogVisible.value = true;
}

/**
 * 提交关闭评审，原因可选。
 */
async function submitClose(): Promise<void> {
  const valid = await closeFormRef.value?.validate().catch(() => false);
  if (valid === false) {
    return;
  }

  const reason = closeForm.reason.trim();
  const done = await runAction(
    () => closeReview(props.id, { reason: reason === '' ? undefined : reason }),
    '评审单已关闭',
  );
  if (done) {
    closeDialogVisible.value = false;
  }
}

/**
 * 刷新评审单：触发仓库同步并重新拉取详情。
 */
async function handleRefresh(): Promise<void> {
  if (busy.value) {
    return;
  }

  busy.value = true;
  try {
    await refreshReview(props.id);
    ElMessage.success('已刷新');
    await loadDetail();
  } catch (error) {
    ElMessage.error(refreshErrorMessage(error));
  } finally {
    busy.value = false;
  }
}

/**
 * 切换版本：只写入 revision 查询参数，diff 由后续任务消费。
 * @param revisionId 选中的版本主键
 */
function selectRevision(revisionId: string): void {
  void router.replace({ query: { ...route.query, revision: revisionId } });
}

// 路由复用同一组件时按新主键重新加载
watch(
  () => props.id,
  () => {
    void loadDetail();
  },
);

onMounted(() => {
  void loadDetail();
});
</script>

<template>
  <section v-loading="loading" class="review-detail">
    <template v-if="detail !== null && selectedRevision !== null">
      <ReviewHeader
        :detail="detail"
        :revision="selectedRevision"
        :revision-id="selectedRevisionId"
        :refreshing="busy"
        @refresh="handleRefresh"
        @close="openCloseDialog"
        @change-revision="selectRevision"
      />

      <p v-if="detail.description !== null" class="review-detail-description">
        {{ detail.description }}
      </p>

      <div v-if="showDecisionActions || canWithdraw" class="review-detail-actions">
        <el-button
          v-if="showDecisionActions"
          class="approve-button"
          type="success"
          :loading="busy"
          :disabled="busy"
          @click="approve"
        >
          通过
        </el-button>
        <el-button
          v-if="showDecisionActions"
          class="request-changes-button"
          type="warning"
          :disabled="busy"
          @click="openChangesDialog"
        >
          要求修改
        </el-button>
        <el-button
          v-if="canWithdraw"
          class="withdraw-button"
          :disabled="busy"
          @click="withdraw"
        >
          撤回结论
        </el-button>
      </div>

      <MergeGuide :detail="detail" @changed="loadDetail" />

      <ReviewerList
        :review-id="detail.id"
        :repo-id="detail.repo.id"
        :submitter-id="detail.submitter.id"
        :is-admin="authStore.isAdmin"
        :can-manage-reviewers="detail.permissions.canManageReviewers"
        :reviewers="detail.reviewers"
        @changed="loadDetail"
      />

      <div class="review-detail-diff">
        <DiffFileList
          :review-id="detail.id"
          :revision-id="selectedRevisionId"
          :is-historical="isHistorical"
          :current-user-id="authStore.user?.id ?? ''"
          :is-admin="authStore.isAdmin"
          :is-merger="detail.permissions.isMerger"
        />
      </div>
    </template>

    <el-empty v-else-if="!loading" description="评审不存在或无权访问" />

    <el-dialog
      v-model="changesDialogVisible"
      class="request-changes-dialog"
      title="要求修改"
      width="520px"
    >
      <el-form
        ref="changesFormRef"
        :model="changesForm"
        :rules="changesRules"
        label-width="80px"
      >
        <el-form-item label="修改说明" prop="note">
          <el-input
            v-model="changesForm.note"
            type="textarea"
            :rows="5"
            :maxlength="NOTE_MAX_LENGTH"
            show-word-limit
            placeholder="请说明需要修改的内容"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="changesDialogVisible = false">取消</el-button>
        <el-button
          class="confirm-request-changes-button"
          type="warning"
          :loading="busy"
          :disabled="busy"
          @click="submitChangesRequest"
        >
          确定
        </el-button>
      </template>
    </el-dialog>

    <el-dialog
      v-model="closeDialogVisible"
      class="close-review-dialog"
      title="关闭评审"
      width="520px"
    >
      <el-form ref="closeFormRef" :model="closeForm" :rules="closeRules" label-width="80px">
        <el-form-item label="关闭原因" prop="reason">
          <el-input
            v-model="closeForm.reason"
            type="textarea"
            :rows="3"
            :maxlength="CLOSE_REASON_MAX_LENGTH"
            show-word-limit
            placeholder="可选"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="closeDialogVisible = false">取消</el-button>
        <el-button
          class="confirm-close-button"
          type="danger"
          :loading="busy"
          :disabled="busy"
          @click="submitClose"
        >
          关闭评审
        </el-button>
      </template>
    </el-dialog>
  </section>
</template>

<style scoped>
.review-detail {
  min-height: 240px;
}

.review-detail-description {
  margin: 0 0 16px;
  color: var(--el-text-color-regular);
  white-space: pre-wrap;
}

.review-detail-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-bottom: 16px;
}

.review-detail-diff {
  margin-top: 16px;
}
</style>

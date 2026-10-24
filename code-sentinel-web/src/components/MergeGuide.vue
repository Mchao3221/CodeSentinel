<script setup lang="ts">
/**
 * 合并指引：评审通过后提示在 Gitblit 完成合并，并支持有权限者手工标记已合并。
 */
import { ref } from 'vue';
import { ElMessage } from 'element-plus';

import { markReviewMerged } from '../api/reviews';
import type { ReviewDetail } from '../api/types';
import { apiErrorMessage } from '../utils/apiError';

const props = defineProps<{
  /** 评审单详情 */
  detail: ReviewDetail;
}>();

const emit = defineEmits<{
  /** 合并状态变化，需要刷新详情 */
  changed: [];
}>();

/** 标记已合并弹窗是否可见 */
const dialogVisible = ref(false);
/** 合并提交输入 */
const mergeCommitSha = ref('');
/** 提交状态 */
const submitting = ref(false);

/**
 * 打开标记已合并弹窗。
 */
function openDialog(): void {
  mergeCommitSha.value = '';
  dialogVisible.value = true;
}

/**
 * 提交标记已合并，成功后关闭弹窗并通知父级刷新详情。
 */
async function submit(): Promise<void> {
  if (submitting.value) {
    return;
  }

  submitting.value = true;
  try {
    const trimmed = mergeCommitSha.value.trim();
    await markReviewMerged(props.detail.id, {
      mergeCommitSha: trimmed === '' ? undefined : trimmed,
    });
    ElMessage.success('已标记为合并');
    dialogVisible.value = false;
    emit('changed');
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '标记已合并失败，请稍后重试'));
  } finally {
    submitting.value = false;
  }
}
</script>

<template>
  <section v-if="detail.status === 'APPROVED'" class="merge-guide">
    <el-alert type="info" :closable="false" show-icon>
      <template #title>
        合并请在 Gitblit 中完成：
        <strong>{{ detail.headBranch }}</strong>
        →
        <strong>{{ detail.baseBranch }}</strong>
      </template>
      <p class="merge-guide-head">
        当前评审提交：
        <code class="merge-guide-head-sha">{{ detail.headSha }}</code>
      </p>
      <p class="merge-guide-tip">
        合并完成后可点击“标记为已合并”；若使用 squash 合并，未自动检测到合并属正常现象。
      </p>
    </el-alert>

    <div v-if="detail.permissions.canMarkMerged" class="merge-guide-actions">
      <el-button class="mark-merged-button" type="primary" @click="openDialog">
        标记为已合并
      </el-button>
    </div>

    <el-dialog
      v-model="dialogVisible"
      class="mark-merged-dialog"
      title="标记为已合并"
      width="480px"
    >
      <el-form label-width="96px">
        <el-form-item label="合并提交">
          <el-input
            v-model="mergeCommitSha"
            placeholder="可选，留空则使用目标分支 head"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button
          class="confirm-mark-merged-button"
          type="primary"
          :loading="submitting"
          :disabled="submitting"
          @click="submit"
        >
          确定
        </el-button>
      </template>
    </el-dialog>
  </section>
</template>

<style scoped>
.merge-guide {
  margin-bottom: 16px;
}

.merge-guide-head {
  margin: 4px 0;
}

.merge-guide-tip {
  margin: 4px 0 0;
}

.merge-guide-actions {
  display: flex;
  justify-content: flex-end;
  margin-top: 8px;
}
</style>

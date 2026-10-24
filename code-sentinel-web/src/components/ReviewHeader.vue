<script setup lang="ts">
/**
 * 评审详情头部：标题、仓库与分支、提交人、状态、版本切换、刷新与关闭入口。
 */
import { computed } from 'vue';

import type { ReviewDetail, ReviewRevision, ReviewStatus } from '../api/types';
import { formatDateTime } from '../utils/datetime';
import ReviewStatusTag from './ReviewStatusTag.vue';

/** 仍可操作的状态集合 */
const ACTIVE_STATUSES: ReviewStatus[] = ['OPEN', 'CHANGES_REQUESTED', 'APPROVED'];

/** 短提交号长度 */
const SHORT_SHA_LENGTH = 7;

const props = defineProps<{
  /** 评审单详情 */
  detail: ReviewDetail;
  /** 当前选中的版本 */
  revision: ReviewRevision;
  /** 当前选中的版本主键 */
  revisionId: string;
  /** 刷新是否进行中 */
  refreshing: boolean;
}>();

const emit = defineEmits<{
  /** 切换版本 */
  changeRevision: [revisionId: string];
  /** 请求刷新 */
  refresh: [];
  /** 请求关闭评审单 */
  close: [];
}>();

/** 当前状态是否仍可操作 */
const isActive = computed(() => ACTIVE_STATUSES.includes(props.detail.status));

/** 版本下拉：最新版本在前 */
const revisionOptions = computed(() => [...props.detail.revisions].reverse());

/** 合并完成后的提示文案 */
const mergedTitle = computed(() => {
  const mergedAt = props.detail.mergedAt;
  const timeText = mergedAt === null ? '' : `于 ${formatDateTime(mergedAt)}`;
  const shaText = props.detail.mergeCommitSha === null
    ? ''
    : `，合并提交 ${shortSha(props.detail.mergeCommitSha)}`;
  return `已合并${timeText}${shaText}`;
});

/**
 * 截取提交号前 7 位。
 * @param sha 完整提交号
 * @returns 短提交号
 */
function shortSha(sha: string): string {
  return sha.slice(0, SHORT_SHA_LENGTH);
}

/**
 * 处理版本下拉变化，只把合法的字符串主键向上传递。
 * @param value 下拉组件回传的值
 */
function handleRevisionChange(value: unknown): void {
  if (typeof value === 'string') {
    emit('changeRevision', value);
  }
}
</script>

<template>
  <header class="review-header">
    <div class="review-header-top">
      <router-link class="review-back-link" to="/reviews">返回列表</router-link>

      <div class="review-header-actions">
        <el-button
          class="refresh-button"
          :loading="refreshing"
          :disabled="refreshing"
          @click="emit('refresh')"
        >
          刷新
        </el-button>
        <el-button
          v-if="detail.permissions.canClose && isActive"
          class="close-button"
          type="danger"
          plain
          @click="emit('close')"
        >
          关闭评审
        </el-button>
      </div>
    </div>

    <div class="review-header-title-row">
      <h2 class="review-header-title">{{ detail.title }}</h2>
      <ReviewStatusTag :status="detail.status" />
    </div>

    <div class="review-header-meta">
      <span class="review-header-repo">{{ detail.repo.name }}</span>
      <span class="review-header-branches">
        {{ detail.headBranch }} → {{ detail.baseBranch }}
      </span>
      <span class="review-header-submitter">
        提交人：{{ detail.submitter.displayName }}
      </span>
      <span class="review-header-revision">
        v{{ revision.seq }} · {{ shortSha(revision.headSha) }} ·
        {{ formatDateTime(revision.createdAt) }}
      </span>
    </div>

    <div class="review-header-revision-row">
      <span class="review-header-revision-label">版本</span>
      <el-select
        class="revision-select"
        :model-value="revisionId"
        @update:model-value="handleRevisionChange"
      >
        <el-option
          v-for="item in revisionOptions"
          :key="item.id"
          :label="`v${item.seq} · ${shortSha(item.headSha)}`"
          :value="item.id"
        />
      </el-select>
    </div>

    <el-alert
      v-if="detail.mergeHint"
      class="review-merge-hint"
      type="warning"
      title="未检测到合并，可能使用了 squash"
      show-icon
      :closable="false"
    />

    <el-alert
      v-if="detail.status === 'MERGED'"
      class="review-merged-info"
      type="success"
      :title="mergedTitle"
      show-icon
      :closable="false"
    />

    <el-alert
      v-if="detail.status === 'CLOSED' && detail.closeReason !== null"
      class="review-close-reason"
      type="info"
      :title="`关闭原因：${detail.closeReason}`"
      show-icon
      :closable="false"
    />
  </header>
</template>

<style scoped>
.review-header {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-bottom: 16px;
}

.review-header-top {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.review-back-link {
  color: var(--el-color-primary);
  text-decoration: none;
}

.review-header-title-row {
  display: flex;
  align-items: center;
  gap: 12px;
}

.review-header-title {
  margin: 0;
  font-size: 20px;
}

.review-header-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.review-header-revision-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.review-header-revision-label {
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.revision-select {
  width: 220px;
}
</style>

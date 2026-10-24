<script setup lang="ts">
/**
 * 变更文件列表：加载文件与评论，展示已看过进度、截断提示，并对每个文件渲染可折叠的 diff 卡片。
 */
import { computed, onMounted, ref, watch } from 'vue';
import { ElMessage } from 'element-plus';

import { getComments, getFiles, setViewed } from '../../api/diff';
import type { DiffFileListItem, ReviewComment } from '../../api/types';
import { apiErrorMessage } from '../../utils/apiError';
import DiffFile from './DiffFile.vue';

const props = defineProps<{
  /** 评审单主键 */
  reviewId: string;
  /** 历史版本主键，空串表示当前版本 */
  revisionId: string;
  /** 是否查看历史版本 */
  isHistorical: boolean;
}>();

/** 文件列表 */
const files = ref<DiffFileListItem[]>([]);
/** 已看过文件数 */
const viewedCount = ref(0);
/** 文件总数 */
const fileCount = ref(0);
/** 是否因超过上限被截断 */
const truncated = ref(false);
/** 全部评论 */
const comments = ref<ReviewComment[]>([]);
/** 列表加载状态 */
const loading = ref(false);
/** 正在提交已看过标记的文件路径集合 */
const pendingPaths = ref<Set<string>>(new Set());

/**
 * 读取文件列表与评论。
 */
async function load(): Promise<void> {
  loading.value = true;
  try {
    const [fileList, commentList] = await Promise.all([
      getFiles(props.reviewId, props.revisionId || undefined),
      getComments(props.reviewId, props.revisionId || undefined),
    ]);
    files.value = fileList.items;
    viewedCount.value = fileList.viewedCount;
    fileCount.value = fileList.fileCount;
    truncated.value = fileList.truncated;
    comments.value = commentList;
  } catch (error) {
    files.value = [];
    comments.value = [];
    ElMessage.error(apiErrorMessage(error, '文件列表加载失败，请稍后重试'));
  } finally {
    loading.value = false;
  }
}

/**
 * 获取某个文件上的评论，同时匹配当前路径与重命名前的路径。
 * @param file 文件摘要
 * @returns 该文件评论列表
 */
function commentsFor(file: DiffFileListItem): ReviewComment[] {
  return comments.value.filter(
    (comment) =>
      comment.path === file.path || (file.oldPath !== null && comment.path === file.oldPath),
  );
}

/**
 * 切换已看过状态：乐观更新文件与计数，失败时回滚。
 * @param path 文件路径
 * @param next 目标状态
 */
async function toggleViewed(path: string, next: boolean): Promise<void> {
  if (props.isHistorical) {
    return;
  }

  const file = files.value.find((item) => item.path === path);
  if (file === undefined || pendingPaths.value.has(path)) {
    return;
  }

  const previous = file.viewed;
  file.viewed = next;
  viewedCount.value += next ? 1 : -1;
  pendingPaths.value = new Set(pendingPaths.value).add(path);

  try {
    await setViewed(props.reviewId, path, next);
  } catch (error) {
    file.viewed = previous;
    viewedCount.value += previous ? 1 : -1;
    ElMessage.error(apiErrorMessage(error, '标记已看过失败，请稍后重试'));
  } finally {
    pendingPaths.value = new Set([...pendingPaths.value].filter((item) => item !== path));
  }
}

/** 已看过进度文案 */
const progressText = computed(() => `已看过 ${viewedCount.value}/${fileCount.value}`);

/** 切换版本时重新加载文件与评论 */
watch(
  () => props.revisionId,
  () => {
    void load();
  },
);

onMounted(() => {
  void load();
});
</script>

<template>
  <section v-loading="loading" class="diff-file-list">
    <el-alert
      v-if="isHistorical"
      class="diff-historical-banner"
      type="info"
      title="正在查看历史版本，仅可查看"
      show-icon
      :closable="false"
    />

    <header class="diff-file-list-header">
      <span class="diff-file-progress">{{ progressText }}</span>
      <span v-if="truncated" class="diff-file-truncated">文件过多，仅展示前 {{ files.length }} 个</span>
    </header>

    <div v-if="files.length === 0 && !loading" class="diff-file-empty">暂无文件变更</div>

    <DiffFile
      v-for="item in files"
      :key="`${revisionId}:${item.path}`"
      class="diff-file-item"
      :review-id="reviewId"
      :revision-id="revisionId"
      :file="item"
      :comments="commentsFor(item)"
      :is-historical="isHistorical"
      :viewed-pending="pendingPaths.has(item.path)"
      @toggle-viewed="toggleViewed"
    />
  </section>
</template>

<style scoped>
.diff-file-list {
  min-height: 120px;
}

.diff-historical-banner {
  margin-bottom: 8px;
}

.diff-file-list-header {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 8px;
}

.diff-file-progress {
  color: var(--el-text-color-regular);
  font-size: 13px;
}

.diff-file-truncated {
  color: var(--el-color-warning);
  font-size: 12px;
}

.diff-file-empty {
  padding: 24px 0;
  text-align: center;
  color: var(--el-text-color-secondary);
}
</style>

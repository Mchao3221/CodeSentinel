<script setup lang="ts">
/**
 * 单个文件的 diff 卡片：默认折叠，展开时按需拉取 diff 与高亮并缓存，展示行内评论与已过期评论。
 */
import { computed, ref } from 'vue';

import { getFileDiff } from '../../api/diff';
import type {
  DiffFile as DiffFileData,
  DiffFileListItem,
  FileChangeStatus,
  ReviewComment,
} from '../../api/types';
import { useHighlight, type LineTokens } from '../../composables/useHighlight';
import DiffHunk from './DiffHunk.vue';
import ViewedCheckbox from './ViewedCheckbox.vue';

const props = defineProps<{
  /** 评审单主键 */
  reviewId: string;
  /** 历史版本主键，空串表示当前版本 */
  revisionId: string;
  /** 文件摘要 */
  file: DiffFileListItem;
  /** 该文件上的评论（含已过期与已删除） */
  comments: ReviewComment[];
  /** 是否查看历史版本，历史版本只读 */
  isHistorical: boolean;
  /** 已看过标记是否正在提交 */
  viewedPending: boolean;
}>();

const emit = defineEmits<{
  /** 请求切换已看过状态 */
  'toggle-viewed': [path: string, next: boolean];
}>();

/** 变更状态展示元数据 */
const STATUS_META: Record<FileChangeStatus, { label: string; className: string }> = {
  ADDED: { label: '新增', className: 'diff-status-added' },
  MODIFIED: { label: '修改', className: 'diff-status-modified' },
  DELETED: { label: '删除', className: 'diff-status-deleted' },
  RENAMED: { label: '重命名', className: 'diff-status-renamed' },
};

/** 是否已展开 */
const expanded = ref(false);
/** 已拉取的单文件 diff，首次展开后缓存 */
const diff = ref<DiffFileData | null>(null);
/** diff 加载状态 */
const loading = ref(false);
/** diff 加载错误文案，为空表示无错误 */
const error = ref('');
/** OLD 侧高亮，按行号索引 */
const oldTokens = ref<LineTokens | null>(null);
/** NEW 侧高亮，按行号索引 */
const newTokens = ref<LineTokens | null>(null);
/** 已过期评论区域是否展开 */
const showOutdated = ref(false);

const { highlightSide } = useHighlight();

/** 变更状态元数据 */
const statusMeta = computed(() => STATUS_META[props.file.status]);

/** 已过期评论列表 */
const outdatedComments = computed(() =>
  props.comments.filter((comment) => comment.status === 'OUTDATED'),
);

/** 行内评论索引，键为 side:line */
const commentsByKey = computed(() => {
  const map = new Map<string, ReviewComment[]>();
  for (const comment of props.comments) {
    if (comment.status === 'OUTDATED') {
      continue;
    }

    const key = `${comment.side}:${comment.line}`;
    const list = map.get(key);
    if (list) {
      list.push(comment);
    } else {
      map.set(key, [comment]);
    }
  }

  return map;
});

/** 重命名时的路径展示，非重命名时仅显示当前路径 */
const pathText = computed(() =>
  props.file.oldPath === null
    ? props.file.path
    : `${props.file.oldPath} → ${props.file.path}`,
);

/**
 * 展开或折叠文件；首次展开时按需拉取 diff。
 */
function toggleExpand(): void {
  expanded.value = !expanded.value;
  if (expanded.value && diff.value === null && !loading.value) {
    void loadDiff();
  }
}

/**
 * 拉取单文件 diff，成功后加载两侧高亮；失败进入可重试的错误态。
 */
async function loadDiff(): Promise<void> {
  loading.value = true;
  error.value = '';
  try {
    diff.value = await getFileDiff(
      props.reviewId,
      props.file.path,
      props.revisionId || undefined,
    );
  } catch {
    error.value = 'diff 加载失败';
  } finally {
    loading.value = false;
  }

  if (diff.value !== null && !diff.value.isBinary && !diff.value.isTooLarge) {
    await loadHighlight();
  }
}

/**
 * 拉取并高亮 OLD / NEW 两侧全文，失败时由组合式函数静默回退纯文本。
 */
async function loadHighlight(): Promise<void> {
  const [oldSide, newSide] = await Promise.all([
    highlightSide(props.reviewId, props.revisionId, props.file.path, 'OLD'),
    highlightSide(props.reviewId, props.revisionId, props.file.path, 'NEW'),
  ]);
  oldTokens.value = oldSide;
  newTokens.value = newSide;
}

/**
 * 切换已看过状态并向上传递，由父级执行乐观更新与接口调用。
 * @param next 目标状态
 */
function onToggleViewed(next: boolean): void {
  emit('toggle-viewed', props.file.path, next);
}
</script>

<template>
  <div class="diff-file">
    <div class="diff-file-header">
      <button
        class="diff-file-toggle"
        type="button"
        :aria-expanded="expanded"
        @click="toggleExpand"
      >
        <span class="diff-file-toggle-icon">{{ expanded ? '▾' : '▸' }}</span>
      </button>
      <span class="diff-file-status" :class="statusMeta.className">{{ statusMeta.label }}</span>
      <span class="diff-file-path">{{ pathText }}</span>
      <span class="diff-file-stats">
        <span class="diff-file-additions">+{{ file.additions }}</span>
        <span class="diff-file-deletions">-{{ file.deletions }}</span>
      </span>
      <ViewedCheckbox
        class="diff-file-viewed"
        :model-value="file.viewed"
        :disabled="isHistorical"
        :pending="viewedPending"
        @update:model-value="onToggleViewed"
      />
    </div>

    <div v-if="expanded" class="diff-file-body">
      <div v-if="outdatedComments.length > 0" class="diff-file-outdated">
        <button class="diff-outdated-toggle" type="button" @click="showOutdated = !showOutdated">
          <span class="diff-outdated-toggle-icon">{{ showOutdated ? '▾' : '▸' }}</span>
          已过期（原位置已变更）
          <span class="diff-outdated-count">{{ outdatedComments.length }}</span>
        </button>
        <ul v-if="showOutdated" class="diff-outdated-list">
          <li v-for="comment in outdatedComments" :key="comment.id" class="diff-outdated-item">
            <div class="diff-outdated-meta">
              {{ comment.path }}:{{ comment.line }} · {{ comment.authorDisplayName }}
            </div>
            <div class="diff-outdated-body">{{ comment.body }}</div>
          </li>
        </ul>
      </div>

      <div v-if="loading" class="diff-file-state">加载中…</div>
      <div v-else-if="error !== ''" class="diff-file-state diff-file-error">
        <span>{{ error }}</span>
        <el-button class="diff-file-retry" link type="primary" @click="loadDiff">重试</el-button>
      </div>
      <div v-else-if="diff !== null && diff.isBinary" class="diff-file-state">不支持预览</div>
      <div v-else-if="diff !== null && diff.isTooLarge" class="diff-file-state">文件过大，未展示 diff</div>
      <DiffHunk
        v-for="(hunk, index) in diff?.hunks ?? []"
        :key="index"
        :hunk="hunk"
        :old-tokens="oldTokens"
        :new-tokens="newTokens"
        :comments-by-key="commentsByKey"
      />
    </div>
  </div>
</template>

<style scoped>
.diff-file {
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 4px;
  margin-bottom: 8px;
}

.diff-file-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 8px;
}

.diff-file-toggle {
  padding: 0;
  border: none;
  background: transparent;
  cursor: pointer;
  color: var(--el-text-color-secondary);
}

.diff-file-toggle-icon {
  display: inline-block;
  width: 12px;
}

.diff-file-status {
  padding: 0 8px;
  border-radius: 10px;
  font-size: 12px;
  line-height: 20px;
  white-space: nowrap;
}

.diff-status-added {
  color: var(--el-color-success);
  background-color: var(--el-color-success-light-9);
}

.diff-status-modified {
  color: var(--el-color-primary);
  background-color: var(--el-color-primary-light-9);
}

.diff-status-deleted {
  color: var(--el-color-danger);
  background-color: var(--el-color-danger-light-9);
}

.diff-status-renamed {
  color: var(--el-color-warning);
  background-color: var(--el-color-warning-light-9);
}

.diff-file-path {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-family: monospace;
  font-size: 13px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.diff-file-stats {
  display: flex;
  gap: 8px;
  font-family: monospace;
  font-size: 12px;
  white-space: nowrap;
}

.diff-file-additions {
  color: var(--el-color-success);
}

.diff-file-deletions {
  color: var(--el-color-danger);
}

.diff-file-body {
  padding: 4px 8px 8px;
}

.diff-file-state {
  padding: 8px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.diff-file-error {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--el-color-danger);
}

.diff-file-outdated {
  margin-bottom: 8px;
}

.diff-outdated-toggle {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 0;
  border: none;
  background: transparent;
  cursor: pointer;
  color: var(--el-text-color-regular);
  font-size: 13px;
}

.diff-outdated-toggle-icon {
  display: inline-block;
  width: 12px;
}

.diff-outdated-count {
  color: var(--el-text-color-secondary);
}

.diff-outdated-list {
  margin: 4px 0 0;
  padding: 0;
  list-style: none;
}

.diff-outdated-item {
  margin-top: 4px;
  padding: 6px 8px;
  border-left: 3px solid var(--el-color-warning);
  background-color: var(--el-fill-color-light);
}

.diff-outdated-meta {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.diff-outdated-body {
  margin-top: 2px;
  color: var(--el-text-color-regular);
  font-size: 13px;
  white-space: pre-wrap;
  word-break: break-word;
}
</style>

<script setup lang="ts">
/**
 * diff 行：三列（旧行号、新行号、带前缀标记的内容）与评论操作列，并在其下渲染行内评论线程与输入框。
 */
import { computed, nextTick, ref } from 'vue';

import type { CommentSide, DiffLine, ReviewComment } from '../../api/types';
import type { HighlightToken } from '../../composables/useHighlight';
import CommentComposer from './CommentComposer.vue';
import CommentThread from './CommentThread.vue';

/**
 * 评论锚点：评论所在侧与该侧文件中的行号。
 */
interface CommentAnchor {
  /** 评论所在侧 */
  side: CommentSide;
  /** 该侧文件中的行号 */
  line: number;
}

const props = withDefaults(
  defineProps<{
    /** diff 行数据 */
    line: DiffLine;
    /** 高亮 token，null 表示回退纯文本 */
    tokens: HighlightToken[] | null;
    /** 挂在该行上的评论 */
    comments: ReviewComment[];
    /** 评审单主键 */
    reviewId?: string;
    /** 文件路径 */
    path?: string;
    /** 是否历史版本，历史版本只读 */
    isHistorical?: boolean;
    /** 当前登录用户主键 */
    currentUserId?: string;
    /** 当前用户是否管理员 */
    isAdmin?: boolean;
    /** 当前用户是否为仓库合并人 */
    isMerger?: boolean;
  }>(),
  {
    reviewId: '',
    path: '',
    isHistorical: false,
    currentUserId: '',
    isAdmin: false,
    isMerger: false,
  },
);

const emit = defineEmits<{
  /** 新建评论成功，交由列表插入本地状态 */
  created: [comment: ReviewComment];
  /** 评论被编辑、删除或解决状态变化 */
  updated: [comment: ReviewComment];
}>();

/** 行类型到前缀标记的映射 */
const MARKERS: Record<DiffLine['type'], string> = {
  CONTEXT: ' ',
  ADD: '+',
  DELETE: '-',
};

/** 是否正在为当前行编写评论 */
const composing = ref(false);

/** 行前缀标记 */
const marker = computed(() => MARKERS[props.line.type]);

/** 行样式类 */
const lineClass = computed(() => `diff-line diff-line-${props.line.type.toLowerCase()}`);

/** 高亮 token 列表，无高亮时为空数组 */
const lineTokens = computed(() => props.tokens ?? []);

/** 是否渲染高亮 token */
const hasTokens = computed(() => lineTokens.value.length > 0);

/** 当前行的评论锚点：删除行取 OLD，上下文行与新增行取 NEW */
const anchor = computed<CommentAnchor | null>(() => {
  if (props.line.type === 'DELETE') {
    return props.line.oldLine === null ? null : { side: 'OLD', line: props.line.oldLine };
  }

  return props.line.newLine === null ? null : { side: 'NEW', line: props.line.newLine };
});

/** 顶层评论线程，回复由线程组件按 parentId 递归渲染 */
const threads = computed(() => props.comments.filter((comment) => comment.parentId === null));

/** 是否显示评论按钮 */
const canComment = computed(() => anchor.value !== null && !props.isHistorical);

/**
 * 把 token 语义类别映射为固定 class。
 * @param token 高亮 token
 * @returns 语义 class
 */
function tokenClass(token: HighlightToken): string {
  return `diff-token-${token.className}`;
}

/**
 * 打开或关闭当前行的评论输入框。
 */
function toggleComposer(): void {
  composing.value = !composing.value;
}

/**
 * 关闭当前行的评论输入框。
 */
function cancelComposer(): void {
  composing.value = false;
}

/**
 * 向上转发线程创建的事件。
 * @param comment 新建的评论
 */
function forwardCreated(comment: ReviewComment): void {
  emit('created', comment);
}

/**
 * 向上转发线程更新的事件。
 * @param comment 更新后的评论
 */
function forwardUpdated(comment: ReviewComment): void {
  emit('updated', comment);
}

/**
 * 新建评论成功：关闭输入框、向上传递并滚动到该评论。
 * @param comment 新建的评论
 */
async function onCreated(comment: ReviewComment): Promise<void> {
  composing.value = false;
  emit('created', comment);

  await nextTick();
  const element = document.querySelector(`[data-comment-id="${comment.id}"]`);
  (element as HTMLElement | null)?.scrollIntoView?.({ block: 'center' });
}
</script>

<template>
  <tr :class="lineClass">
    <td class="diff-line-num diff-line-num-old">{{ line.oldLine ?? '' }}</td>
    <td class="diff-line-num diff-line-num-new">{{ line.newLine ?? '' }}</td>
    <td class="diff-line-content">
      <span class="diff-line-marker">{{ marker }}</span>
      <span class="diff-line-text">
        <template v-if="hasTokens">
          <span
            v-for="(token, tokenIndex) in lineTokens"
            :key="tokenIndex"
            class="diff-token"
            :class="tokenClass(token)"
          >{{ token.content }}</span>
        </template>
        <template v-else>{{ line.text }}</template>
      </span>
    </td>
    <td class="diff-line-actions">
      <button
        v-if="canComment"
        class="diff-comment-add"
        type="button"
        title="添加评论"
        @click="toggleComposer"
      >
        +
      </button>
    </td>
  </tr>
  <tr v-if="composing && anchor !== null" class="diff-composer-row">
    <td colspan="4">
      <CommentComposer
        :review-id="reviewId"
        :path="path"
        :side="anchor.side"
        :line="anchor.line"
        @created="onCreated"
        @cancel="cancelComposer"
      />
    </td>
  </tr>
  <tr v-for="comment in threads" :key="comment.id" class="diff-comment-row">
    <td colspan="4">
      <CommentThread
        :review-id="reviewId"
        :comment="comment"
        :comments="comments"
        :current-user-id="currentUserId"
        :is-admin="isAdmin"
        :is-merger="isMerger"
        :is-historical="isHistorical"
        @created="forwardCreated"
        @updated="forwardUpdated"
      />
    </td>
  </tr>
</template>

<style scoped>
.diff-line-num {
  width: 48px;
  min-width: 48px;
  padding: 0 8px;
  text-align: right;
  color: var(--el-text-color-secondary);
  background-color: var(--el-fill-color-light);
  user-select: none;
  vertical-align: top;
}

.diff-line-marker {
  display: inline-block;
  width: 16px;
  user-select: none;
  color: var(--el-text-color-secondary);
}

.diff-line-content {
  padding: 0 8px;
  white-space: pre;
  vertical-align: top;
}

.diff-line-actions {
  width: 28px;
  min-width: 28px;
  padding: 0 4px;
  text-align: center;
  vertical-align: top;
}

.diff-comment-add {
  width: 20px;
  height: 20px;
  padding: 0;
  border: none;
  border-radius: 3px;
  color: var(--el-color-primary);
  background-color: var(--el-color-primary-light-9);
  cursor: pointer;
  visibility: hidden;
}

.diff-line:hover .diff-comment-add,
.diff-comment-add:focus {
  visibility: visible;
}

.diff-line-add .diff-line-marker {
  color: var(--el-color-success);
}

.diff-line-add .diff-line-content {
  background-color: var(--el-color-success-light-9);
}

.diff-line-delete .diff-line-marker {
  color: var(--el-color-danger);
}

.diff-line-delete .diff-line-content {
  background-color: var(--el-color-danger-light-9);
}

.diff-token {
  white-space: pre;
}

/* github-light 对齐的语义配色，未映射的 token 使用默认前景色 */
.diff-token-comment {
  color: #6a737d;
}

.diff-token-string {
  color: #032f62;
}

.diff-token-keyword {
  color: #d73a49;
}

.diff-token-number {
  color: #005cc5;
}

.diff-token-function {
  color: #6f42c1;
}

.diff-token-type {
  color: #6f42c1;
}

.diff-token-variable {
  color: #e36209;
}

.diff-token-tag {
  color: #22863a;
}

.diff-token-attribute {
  color: #6f42c1;
}

.diff-token-property {
  color: #005cc5;
}

.diff-token-operator {
  color: #d73a49;
}

.diff-token-punctuation {
  color: #24292e;
}

.diff-token-default {
  color: #24292e;
}

.diff-composer-row {
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.diff-composer-row td {
  padding: 4px 8px;
}

.diff-comment-row {
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.diff-comment-row td {
  padding: 4px 8px;
}
</style>

<script setup lang="ts">
/**
 * diff 行：三列（旧行号、新行号、带前缀标记的内容）并在其下渲染行内评论。
 */
import { computed } from 'vue';

import type { DiffLine, ReviewComment } from '../../api/types';
import type { HighlightToken } from '../../composables/useHighlight';
import { formatDateTime } from '../../utils/datetime';

const props = defineProps<{
  /** diff 行数据 */
  line: DiffLine;
  /** 高亮 token，null 表示回退纯文本 */
  tokens: HighlightToken[] | null;
  /** 挂在该行上的评论 */
  comments: ReviewComment[];
}>();

/** 行类型到前缀标记的映射 */
const MARKERS: Record<DiffLine['type'], string> = {
  CONTEXT: ' ',
  ADD: '+',
  DELETE: '-',
};

/** 行前缀标记 */
const marker = computed(() => MARKERS[props.line.type]);

/** 行样式类 */
const lineClass = computed(() => `diff-line diff-line-${props.line.type.toLowerCase()}`);

/** 高亮 token 列表，无高亮时为空数组 */
const lineTokens = computed(() => props.tokens ?? []);

/** 是否渲染高亮 token */
const hasTokens = computed(() => lineTokens.value.length > 0);

/**
 * 把 token 语义类别映射为固定 class。
 * @param token 高亮 token
 * @returns 语义 class
 */
function tokenClass(token: HighlightToken): string {
  return `diff-token-${token.className}`;
}

/**
 * 评论展示样式类，已解决与已删除单独区分。
 * @param comment 评论
 * @returns 样式类
 */
function commentClass(comment: ReviewComment): string {
  return comment.status === 'RESOLVED' ? 'diff-comment diff-comment-resolved' : 'diff-comment';
}

/**
 * 评论正文，已删除的评论替换为占位文案。
 * @param comment 评论
 * @returns 可展示正文
 */
function commentBody(comment: ReviewComment): string {
  return comment.status === 'DELETED' ? '该评论已删除' : comment.body;
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
  </tr>
  <tr v-for="comment in comments" :key="comment.id" class="diff-comment-row">
    <td colspan="3">
      <div :class="commentClass(comment)">
        <div class="diff-comment-meta">
          <strong class="diff-comment-author">{{ comment.authorDisplayName }}</strong>
          <span class="diff-comment-time">{{ formatDateTime(comment.createdAt) }}</span>
          <span v-if="comment.status === 'RESOLVED'" class="diff-comment-resolved-label">已解决</span>
        </div>
        <div class="diff-comment-body">{{ commentBody(comment) }}</div>
      </div>
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

.diff-comment-row {
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.diff-comment {
  margin: 4px 8px;
  padding: 8px 12px;
  border-left: 3px solid var(--el-color-primary);
  background-color: var(--el-fill-color-light);
}

.diff-comment-resolved {
  border-left-color: var(--el-color-success);
  opacity: 0.75;
}

.diff-comment-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.diff-comment-author {
  color: var(--el-text-color-primary);
}

.diff-comment-resolved-label {
  color: var(--el-color-success);
}

.diff-comment-body {
  margin-top: 4px;
  color: var(--el-text-color-regular);
  white-space: pre-wrap;
  word-break: break-word;
}
</style>

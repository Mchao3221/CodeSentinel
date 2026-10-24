<script setup lang="ts">
/**
 * diff 块：渲染 hunk 头部与块内各行，并把高亮与行内评论按行分发。
 */
import type { DiffHunk, DiffLine, ReviewComment } from '../../api/types';
import type { HighlightToken, LineTokens } from '../../composables/useHighlight';
import DiffLineView from './DiffLine.vue';

const props = defineProps<{
  /** diff 块 */
  hunk: DiffHunk;
  /** OLD 侧高亮，按行号索引 */
  oldTokens: LineTokens | null;
  /** NEW 侧高亮，按行号索引 */
  newTokens: LineTokens | null;
  /** 行内评论索引，键为 side:line */
  commentsByKey: ReadonlyMap<string, ReviewComment[]>;
}>();

/**
 * 计算某一行应取用的高亮 token：删除行取 OLD 侧，其余取 NEW 侧。
 * @param line diff 行
 * @returns 该行 token 列表，无高亮时为 null
 */
function tokensFor(line: DiffLine): HighlightToken[] | null {
  if (line.type === 'DELETE') {
    return line.oldLine === null ? null : (props.oldTokens?.get(line.oldLine) ?? null);
  }

  return line.newLine === null ? null : (props.newTokens?.get(line.newLine) ?? null);
}

/**
 * 收集挂在该行上的评论，OLD 侧与 NEW 侧各自按行号取用。
 * @param line diff 行
 * @returns 该行评论列表
 */
function commentsFor(line: DiffLine): ReviewComment[] {
  const result: ReviewComment[] = [];
  if (line.oldLine !== null) {
    result.push(...(props.commentsByKey.get(`OLD:${line.oldLine}`) ?? []));
  }

  if (line.newLine !== null) {
    result.push(...(props.commentsByKey.get(`NEW:${line.newLine}`) ?? []));
  }

  return result;
}
</script>

<template>
  <div class="diff-hunk">
    <div class="diff-hunk-header">
      @@ -{{ hunk.oldStart }},{{ hunk.oldLines }} +{{ hunk.newStart }},{{ hunk.newLines }} @@
    </div>
    <table class="diff-table">
      <tbody>
        <DiffLineView
          v-for="line in hunk.lines"
          :key="`${line.type}-${line.oldLine}-${line.newLine}`"
          :line="line"
          :tokens="tokensFor(line)"
          :comments="commentsFor(line)"
        />
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.diff-hunk {
  overflow: hidden;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 4px;
}

.diff-hunk-header {
  padding: 4px 8px;
  font-family: monospace;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  background-color: var(--el-fill-color-light);
}

.diff-table {
  width: 100%;
  table-layout: fixed;
  border-collapse: collapse;
}

.diff-table td {
  overflow-x: auto;
  font-family: monospace;
  font-size: 12px;
  line-height: 1.5;
}
</style>

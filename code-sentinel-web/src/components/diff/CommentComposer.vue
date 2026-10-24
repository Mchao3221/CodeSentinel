<script setup lang="ts">
/**
 * 评论输入框：创建行内评论或回复，正文长度限制 1 到 5000 个字符。
 */
import { ref } from 'vue';
import { ElMessage } from 'element-plus';

import { createComment } from '../../api/comments';
import type { CommentSide, ReviewComment } from '../../api/types';
import { apiErrorMessage } from '../../utils/apiError';

/** 评论正文最大长度，与后端一致 */
const MAX_LENGTH = 5000;

/** 错误提示文案 */
const INVALID_LENGTH_TEXT = `评论内容需为 1 到 ${MAX_LENGTH} 个字符`;

const props = defineProps<{
  /** 评审单主键 */
  reviewId: string;
  /** 文件路径 */
  path: string;
  /** 评论所在侧 */
  side: CommentSide;
  /** 该侧文件中的行号 */
  line: number;
  /** 回复的父评论主键，空表示顶层评论 */
  parentId?: string;
  /** 输入框占位文案 */
  placeholder?: string;
}>();

const emit = defineEmits<{
  /** 创建成功后返回新评论 */
  created: [comment: ReviewComment];
  /** 取消输入 */
  cancel: [];
}>();

/** 评论正文 */
const body = ref('');
/** 是否正在提交 */
const submitting = ref(false);
/** 提交错误文案，空串表示无错误 */
const errorText = ref('');

/**
 * 提交评论；校验失败或接口失败时保持输入框打开。
 */
async function submit(): Promise<void> {
  if (submitting.value) {
    return;
  }

  const text = body.value.trim();
  if (text.length === 0 || text.length > MAX_LENGTH) {
    errorText.value = INVALID_LENGTH_TEXT;
    return;
  }

  submitting.value = true;
  errorText.value = '';
  try {
    const comment = await createComment(props.reviewId, {
      path: props.path,
      side: props.side,
      line: props.line,
      body: text,
      ...(props.parentId === undefined ? {} : { parentId: props.parentId }),
    });
    body.value = '';
    emit('created', comment);
  } catch (error) {
    const message = apiErrorMessage(error, '评论提交失败，请稍后重试');
    errorText.value = message;
    ElMessage.error(message);
  } finally {
    submitting.value = false;
  }
}

/**
 * 取消输入并通知父级关闭输入框。
 */
function cancel(): void {
  emit('cancel');
}
</script>

<template>
  <div class="comment-composer">
    <el-input
      v-model="body"
      class="comment-composer-input"
      type="textarea"
      :rows="3"
      :maxlength="MAX_LENGTH"
      show-word-limit
      :disabled="submitting"
      :placeholder="placeholder ?? '写下你的评论'"
    />
    <div class="comment-composer-actions">
      <span v-if="errorText !== ''" class="comment-composer-error">{{ errorText }}</span>
      <el-button
        class="comment-composer-cancel"
        size="small"
        :disabled="submitting"
        @click="cancel"
      >
        取消
      </el-button>
      <el-button
        class="comment-composer-submit"
        size="small"
        type="primary"
        :loading="submitting"
        :disabled="submitting"
        @click="submit"
      >
        提交
      </el-button>
    </div>
  </div>
</template>

<style scoped>
.comment-composer {
  padding: 8px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 4px;
  background-color: var(--el-bg-color);
}

.comment-composer-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 6px;
}

.comment-composer-error {
  flex: 1;
  color: var(--el-color-danger);
  font-size: 12px;
}
</style>

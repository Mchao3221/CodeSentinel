<script setup lang="ts">
/**
 * 单条行内评论及其回复线程：展示 Markdown 正文，提供回复、编辑、删除与解决操作。
 */
import { computed, ref, watch } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';
import DOMPurify from 'dompurify';
import { marked } from 'marked';

import {
  deleteComment,
  resolveComment,
  unresolveComment,
  updateComment,
} from '../../api/comments';
import type { ReviewComment } from '../../api/types';
import { apiErrorMessage } from '../../utils/apiError';
import { formatDateTime } from '../../utils/datetime';
import CommentComposer from './CommentComposer.vue';

/** 评论正文最大长度，与后端一致 */
const MAX_LENGTH = 5000;

/** 已删除评论的占位文案 */
const DELETED_TEXT = '该评论已删除';

const props = withDefaults(
  defineProps<{
    /** 评审单主键 */
    reviewId: string;
    /** 当前评论 */
    comment: ReviewComment;
    /** 同一行上的全部评论，用于按 parentId 组装回复 */
    comments: ReviewComment[];
    /** 当前登录用户主键 */
    currentUserId: string;
    /** 当前用户是否管理员 */
    isAdmin: boolean;
    /** 当前用户是否为仓库合并人 */
    isMerger: boolean;
    /** 是否历史版本，历史版本只读 */
    isHistorical: boolean;
    /** 是否允许回复，已过期区域关闭 */
    allowReply?: boolean;
    /** 是否允许编辑，已过期区域关闭 */
    allowEdit?: boolean;
    /** 是否允许解决，已过期区域关闭 */
    allowResolve?: boolean;
  }>(),
  {
    allowReply: true,
    allowEdit: true,
    allowResolve: true,
  },
);

const emit = defineEmits<{
  /** 回复创建成功 */
  created: [comment: ReviewComment];
  /** 评论被编辑、删除或解决状态变化 */
  updated: [comment: ReviewComment];
}>();

/** 本地评论副本，用于乐观更新与回滚 */
const local = ref<ReviewComment>({ ...props.comment });
/** 是否正在编辑正文 */
const editing = ref(false);
/** 编辑框正文 */
const editBody = ref('');
/** 是否正在回复 */
const replying = ref(false);
/** 是否有操作正在提交 */
const pending = ref(false);

/** 父级替换评论对象时同步本地副本 */
watch(
  () => props.comment,
  (next) => {
    local.value = { ...next };
  },
);

/** 是否为已删除评论 */
const isDeleted = computed(() => local.value.status === 'DELETED');
/** 是否为已解决评论 */
const isResolved = computed(() => local.value.status === 'RESOLVED');
/** 是否本人评论 */
const isOwn = computed(() => local.value.authorId === props.currentUserId);
/** 是否可编辑：仅作者、允许编辑、未删除且非历史版本 */
const canEdit = computed(
  () => props.allowEdit && isOwn.value && !isDeleted.value && !props.isHistorical,
);
/** 是否可删除：作者或管理员，且未删除、非历史版本 */
const canDelete = computed(
  () => (isOwn.value || props.isAdmin) && !isDeleted.value && !props.isHistorical,
);
/** 是否可解决：作者或仓库合并人、允许解决、未删除且非历史版本 */
const canResolve = computed(
  () =>
    props.allowResolve
    && (isOwn.value || props.isMerger)
    && !isDeleted.value
    && !props.isHistorical,
);
/** 是否可回复：允许回复、未删除且非历史版本 */
const canReply = computed(
  () => props.allowReply && !isDeleted.value && !props.isHistorical,
);

/** 当前评论的回复列表，按接口返回顺序展示 */
const replies = computed(() =>
  props.comments.filter((item) => item.parentId === local.value.id),
);

/** 评论正文的展示文案，已删除时使用占位 */
const displayBody = computed(() => (isDeleted.value ? DELETED_TEXT : local.value.body));

/** Markdown 渲染并净化后的正文 HTML */
const renderedBody = computed(() => {
  if (isDeleted.value) {
    return '';
  }

  const html = marked.parse(local.value.body, { async: false }) as string;
  return DOMPurify.sanitize(html, { USE_PROFILES: { html: true } });
});

/** 评论根节点样式类 */
const threadClass = computed(() =>
  isResolved.value ? 'comment-thread comment-thread-resolved' : 'comment-thread',
);

/**
 * 打开编辑框并填入当前正文。
 */
function startEdit(): void {
  editBody.value = local.value.body;
  editing.value = true;
}

/**
 * 关闭编辑框，不保存修改。
 */
function cancelEdit(): void {
  editing.value = false;
}

/**
 * 保存编辑：先乐观更新正文，失败时回滚并提示。
 */
async function saveEdit(): Promise<void> {
  if (pending.value) {
    return;
  }

  const body = editBody.value.trim();
  if (body.length === 0 || body.length > MAX_LENGTH) {
    ElMessage.warning(`评论内容需为 1 到 ${MAX_LENGTH} 个字符`);
    return;
  }

  const previous = { ...local.value };
  pending.value = true;
  editing.value = false;
  local.value = { ...local.value, body };

  try {
    const updated = await updateComment(local.value.id, body);
    local.value = { ...updated };
    emit('updated', updated);
  } catch (error) {
    local.value = previous;
    ElMessage.error(apiErrorMessage(error, '编辑评论失败，请稍后重试'));
  } finally {
    pending.value = false;
  }
}

/**
 * 确认后删除评论：先乐观替换为已删除状态，失败时回滚。
 */
async function remove(): Promise<void> {
  if (pending.value) {
    return;
  }

  try {
    await ElMessageBox.confirm('确定删除这条评论吗？', '删除评论', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消',
    });
  } catch {
    return;
  }

  const previous = { ...local.value };
  pending.value = true;
  local.value = { ...local.value, status: 'DELETED', body: '' };

  try {
    const updated = await deleteComment(local.value.id);
    local.value = { ...updated };
    emit('updated', updated);
  } catch (error) {
    local.value = previous;
    ElMessage.error(apiErrorMessage(error, '删除评论失败，请稍后重试'));
  } finally {
    pending.value = false;
  }
}

/**
 * 切换已解决状态：先乐观更新，失败时回滚并提示。
 */
async function toggleResolve(): Promise<void> {
  if (pending.value) {
    return;
  }

  const previous = { ...local.value };
  const nextResolved = !isResolved.value;
  pending.value = true;
  local.value = {
    ...local.value,
    status: nextResolved ? 'RESOLVED' : 'OPEN',
    resolvedAt: nextResolved ? new Date().toISOString() : null,
  };

  try {
    const updated = nextResolved
      ? await resolveComment(local.value.id)
      : await unresolveComment(local.value.id);
    local.value = { ...updated };
    emit('updated', updated);
  } catch (error) {
    local.value = previous;
    ElMessage.error(
      apiErrorMessage(error, nextResolved ? '解决评论失败，请稍后重试' : '取消解决失败，请稍后重试'),
    );
  } finally {
    pending.value = false;
  }
}

/**
 * 打开回复输入框。
 */
function startReply(): void {
  replying.value = true;
}

/**
 * 回复创建成功：关闭输入框并向上传递新评论。
 * @param comment 新建的回复
 */
function onReplyCreated(comment: ReviewComment): void {
  replying.value = false;
  emit('created', comment);
}

/**
 * 关闭回复输入框。
 */
function cancelReply(): void {
  replying.value = false;
}

/**
 * 向上转发子线程创建的事件。
 * @param comment 新建的评论
 */
function forwardCreated(comment: ReviewComment): void {
  emit('created', comment);
}

/**
 * 向上转发子线程更新的事件。
 * @param comment 更新后的评论
 */
function forwardUpdated(comment: ReviewComment): void {
  emit('updated', comment);
}
</script>

<template>
  <div :class="threadClass" :data-comment-id="local.id">
    <div class="comment-thread-main">
      <div class="comment-meta">
        <strong class="comment-author">{{ local.authorDisplayName }}</strong>
        <span class="comment-time">{{ formatDateTime(local.createdAt) }}</span>
        <span v-if="isResolved" class="comment-resolved-badge">已解决</span>
      </div>

      <template v-if="editing">
        <div class="comment-edit">
          <el-input
            v-model="editBody"
            type="textarea"
            :rows="3"
            :maxlength="MAX_LENGTH"
            show-word-limit
            :disabled="pending"
          />
          <div class="comment-edit-actions">
            <el-button class="comment-edit-cancel" size="small" :disabled="pending" @click="cancelEdit">
              取消
            </el-button>
            <el-button
              class="comment-edit-save"
              size="small"
              type="primary"
              :loading="pending"
              :disabled="pending"
              @click="saveEdit"
            >
              保存
            </el-button>
          </div>
        </div>
      </template>
      <div v-else-if="isDeleted" class="comment-body comment-body-deleted">{{ displayBody }}</div>
      <!-- eslint-disable-next-line vue/no-v-html -->
      <div v-else class="comment-body" v-html="renderedBody" />

      <div v-if="!editing" class="comment-actions">
        <el-button
          v-if="canReply"
          class="comment-action comment-action-reply"
          link
          type="primary"
          size="small"
          @click="startReply"
        >
          回复
        </el-button>
        <el-button
          v-if="canEdit"
          class="comment-action comment-action-edit"
          link
          type="primary"
          size="small"
          :disabled="pending"
          @click="startEdit"
        >
          编辑
        </el-button>
        <el-button
          v-if="canDelete"
          class="comment-action comment-action-delete"
          link
          type="danger"
          size="small"
          :disabled="pending"
          @click="remove"
        >
          删除
        </el-button>
        <el-button
          v-if="canResolve"
          class="comment-action comment-action-resolve"
          link
          type="success"
          size="small"
          :disabled="pending"
          @click="toggleResolve"
        >
          {{ isResolved ? '取消解决' : '解决' }}
        </el-button>
      </div>

      <CommentComposer
        v-if="replying"
        class="comment-reply-composer"
        :review-id="reviewId"
        :path="local.path"
        :side="local.side"
        :line="local.line"
        :parent-id="local.id"
        placeholder="回复这条评论"
        @created="onReplyCreated"
        @cancel="cancelReply"
      />
    </div>

    <div v-if="replies.length > 0" class="comment-replies">
      <CommentThread
        v-for="reply in replies"
        :key="reply.id"
        :review-id="reviewId"
        :comment="reply"
        :comments="comments"
        :current-user-id="currentUserId"
        :is-admin="isAdmin"
        :is-merger="isMerger"
        :is-historical="isHistorical"
        :allow-reply="allowReply"
        :allow-edit="allowEdit"
        :allow-resolve="allowResolve"
        @created="forwardCreated"
        @updated="forwardUpdated"
      />
    </div>
  </div>
</template>

<style scoped>
.comment-thread {
  padding: 8px 12px;
  border-left: 3px solid var(--el-color-primary);
  background-color: var(--el-fill-color-light);
}

.comment-thread-resolved {
  border-left-color: var(--el-color-success);
  opacity: 0.8;
}

.comment-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.comment-author {
  color: var(--el-text-color-primary);
}

.comment-resolved-badge {
  padding: 0 6px;
  border-radius: 8px;
  color: var(--el-color-success);
  background-color: var(--el-color-success-light-9);
}

.comment-body {
  margin-top: 4px;
  color: var(--el-text-color-regular);
  font-size: 13px;
  overflow-wrap: break-word;
}

.comment-body-deleted {
  color: var(--el-text-color-placeholder);
  font-style: italic;
}

.comment-actions {
  display: flex;
  gap: 4px;
  margin-top: 4px;
}

.comment-edit-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 6px;
}

.comment-reply-composer {
  margin-top: 8px;
}

.comment-replies {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 8px;
  margin-left: 16px;
}
</style>

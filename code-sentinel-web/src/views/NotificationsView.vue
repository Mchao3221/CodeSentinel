<script setup lang="ts">
/**
 * 通知中心：未读/全部切换、单条与全部已读，点击通知跳转评审详情。
 */
import { onMounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { ElMessage } from 'element-plus';

import { fetchNotifications, markNotificationsRead } from '../api/notifications';
import type { Notification, NotificationType } from '../api/types';
import { apiErrorMessage } from '../utils/apiError';
import { formatDateTime } from '../utils/datetime';

/** 每页条数 */
const PAGE_SIZE = 20;

/** 通知类型中文标签 */
const TYPE_LABELS: Record<NotificationType, string> = {
  REVIEW_SUBMITTED: '新评审',
  CHANGES_REQUESTED: '要求修改',
  NEW_COMMITS: '新提交',
  APPROVED: '已通过',
  MERGED: '已合并',
  CLOSED: '已关闭',
  COMMENT_REPLY: '评论回复',
  REVIEWER_REMOVED: '审核人移除',
  DECISION_WITHDRAWN: '结论撤回',
};

const router = useRouter();

/** 通知列表 */
const notifications = ref<Notification[]>([]);
/** 总条数 */
const total = ref(0);
/** 当前页码 */
const page = ref(1);
/** 是否只看未读 */
const unreadOnly = ref(false);
/** 列表加载状态 */
const loading = ref(false);
/** 全部已读请求进行中 */
const markingAll = ref(false);

/**
 * 读取 payload 中的非空字符串字段。
 * @param payload 通知附加数据
 * @param key 字段名
 * @returns 字段值，缺失或非字符串时为 null
 */
function payloadText(payload: Record<string, unknown>, key: string): string | null {
  const value = payload[key];
  return typeof value === 'string' && value.length > 0 ? value : null;
}

/**
 * 构造通知摘要：优先标题与仓库名，缺失时退回评审单标识。
 * @param notification 通知
 * @returns 摘要文本
 */
function notificationSummary(notification: Notification): string {
  const parts: string[] = [];
  const title = payloadText(notification.payload, 'title');
  const repoName = payloadText(notification.payload, 'repoName');
  if (title !== null) {
    parts.push(title);
  }
  if (repoName !== null) {
    parts.push(repoName);
  }

  if (parts.length > 0) {
    return parts.join(' · ');
  }

  return notification.reviewId === null ? '通知' : `评审 ${notification.reviewId}`;
}

/**
 * 读取通知类型标签，未知类型兜底为“通知”。
 * @param type 通知类型
 * @returns 中文标签
 */
function typeLabel(type: NotificationType): string {
  return TYPE_LABELS[type] ?? '通知';
}

/**
 * 给每行加上固定 class，便于样式与测试定位。
 * @returns 行 class
 */
function rowClassName(): string {
  return 'notification-row';
}

/**
 * 拉取通知列表。
 */
async function loadNotifications(): Promise<void> {
  loading.value = true;
  try {
    const result = await fetchNotifications({
      unreadOnly: unreadOnly.value,
      page: page.value,
      pageSize: PAGE_SIZE,
    });
    notifications.value = result.items;
    total.value = result.total;
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '通知加载失败，请稍后重试'));
  } finally {
    loading.value = false;
  }
}

/**
 * 标记单条通知已读并刷新列表。
 * @param notification 目标通知
 */
async function markOne(notification: Notification): Promise<void> {
  if (notification.readAt !== null) {
    return;
  }

  try {
    await markNotificationsRead({ ids: [notification.id] });
    await loadNotifications();
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '标记已读失败，请稍后重试'));
  }
}

/**
 * 标记全部通知已读并刷新列表。
 */
async function markAll(): Promise<void> {
  markingAll.value = true;
  try {
    await markNotificationsRead({ all: true });
    await loadNotifications();
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '标记已读失败，请稍后重试'));
  } finally {
    markingAll.value = false;
  }
}

/**
 * 点击通知：有关联评审单时标记已读并跳转详情。
 * @param notification 被点击的通知
 */
async function openNotification(notification: Notification): Promise<void> {
  if (notification.reviewId === null) {
    return;
  }

  if (notification.readAt === null) {
    try {
      await markNotificationsRead({ ids: [notification.id] });
    } catch {
      // 标记失败不阻止跳转，用户仍可查看评审单
    }
  }

  await router.push(`/reviews/${notification.reviewId}`);
}

// 切换未读筛选时回到第一页；页码已是 1 时直接重新加载
watch(unreadOnly, () => {
  if (page.value !== 1) {
    page.value = 1;
    return;
  }

  void loadNotifications();
});

// 页码变化时加载对应页
watch(page, () => {
  void loadNotifications();
});

onMounted(() => {
  void loadNotifications();
});
</script>

<template>
  <section class="notification-center">
    <header class="notification-center-header">
      <h2 class="notification-center-title">通知中心</h2>
      <div class="notification-center-actions">
        <el-switch v-model="unreadOnly" active-text="仅看未读" />
        <el-button class="mark-all-read" :loading="markingAll" @click="markAll">
          全部已读
        </el-button>
      </div>
    </header>

    <el-table
      v-loading="loading"
      :data="notifications"
      border
      stripe
      empty-text="暂无通知"
      :row-class-name="rowClassName"
      @row-click="openNotification"
    >
      <el-table-column label="类型" width="120" align="center">
        <template #default="{ row }">
          <el-tag size="small">{{ typeLabel(row.type) }}</el-tag>
        </template>
      </el-table-column>

      <el-table-column label="内容" min-width="260" show-overflow-tooltip>
        <template #default="{ row }">
          {{ notificationSummary(row) }}
        </template>
      </el-table-column>

      <el-table-column label="时间" width="160" align="center">
        <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
      </el-table-column>

      <el-table-column label="状态" width="90" align="center">
        <template #default="{ row }">
          <el-tag v-if="row.readAt === null" type="warning" size="small">未读</el-tag>
          <span v-else>已读</span>
        </template>
      </el-table-column>

      <el-table-column label="操作" width="120" align="center">
        <template #default="{ row }">
          <el-button
            link
            type="primary"
            size="small"
            class="mark-read-button"
            :disabled="row.readAt !== null"
            @click.stop="markOne(row)"
          >
            标记已读
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-pagination
      v-model:current-page="page"
      class="notification-center-pagination"
      background
      layout="total, prev, pager, next"
      :page-size="PAGE_SIZE"
      :total="total"
    />
  </section>
</template>

<style scoped>
.notification-center-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 16px;
}

.notification-center-title {
  margin: 0;
  font-size: 18px;
}

.notification-center-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

.notification-center-pagination {
  margin-top: 16px;
  justify-content: flex-end;
}
</style>

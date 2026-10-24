<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { useRouter } from 'vue-router';

import { fetchUnreadCount } from '../api/notifications';

/** 轮询间隔：30 秒 */
const POLL_INTERVAL_MS = 30_000;

const router = useRouter();
const unreadCount = ref(0);
let timer: ReturnType<typeof setInterval> | null = null;

/**
 * 拉取未读通知数；轮询失败时保留上次结果，等待下一轮自动重试。
 */
async function refreshUnreadCount(): Promise<void> {
  try {
    const result = await fetchUnreadCount();
    unreadCount.value = result.count;
  } catch {
    // 轮询失败不打断当前操作
  }
}

/**
 * 打开通知中心。
 */
async function openNotifications(): Promise<void> {
  await router.push('/notifications');
}

onMounted(() => {
  void refreshUnreadCount();
  timer = setInterval(() => {
    void refreshUnreadCount();
  }, POLL_INTERVAL_MS);
});

onBeforeUnmount(() => {
  if (timer !== null) {
    clearInterval(timer);
    timer = null;
  }
});
</script>

<template>
  <el-badge :value="unreadCount" :max="99" :hidden="unreadCount === 0">
    <el-button
      text
      circle
      class="notification-bell"
      aria-label="通知中心"
      @click="openNotifications"
    >
      <svg
        viewBox="0 0 24 24"
        width="20"
        height="20"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        stroke-linejoin="round"
        aria-hidden="true"
      >
        <path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9" />
        <path d="M13.73 21a2 2 0 0 1-3.46 0" />
      </svg>
    </el-button>
  </el-badge>
</template>

<style scoped>
.notification-bell {
  color: var(--el-text-color-primary);
}
</style>

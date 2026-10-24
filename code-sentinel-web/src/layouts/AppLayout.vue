<script setup lang="ts">
import { useRoute, useRouter } from 'vue-router';
import { ElMessage } from 'element-plus';

import NotificationBell from '../components/NotificationBell.vue';
import { useAuthStore } from '../stores/auth';

const route = useRoute();
const router = useRouter();
const authStore = useAuthStore();

/**
 * 处理用户菜单命令。
 */
async function handleCommand(command: string): Promise<void> {
  if (command === 'logout') {
    await handleLogout();
  }
}

/**
 * 退出登录并回到登录页；退出失败也要清理本地状态。
 */
async function handleLogout(): Promise<void> {
  try {
    await authStore.logout();
  } catch {
    ElMessage.error('退出登录失败，请稍后重试');
  }
  await router.replace('/login');
}
</script>

<template>
  <el-container class="app-shell">
    <el-header class="app-header">
      <div class="app-brand">CodeSentinel</div>

      <el-menu
        mode="horizontal"
        :ellipsis="false"
        :default-active="route.path"
        router
        class="app-nav"
      >
        <el-menu-item index="/reviews">评审</el-menu-item>
        <el-menu-item v-if="authStore.isAdmin" index="/repos">仓库</el-menu-item>
        <el-menu-item v-if="authStore.isAdmin" index="/users">用户</el-menu-item>
      </el-menu>

      <div class="app-actions">
        <NotificationBell />
        <el-dropdown @command="handleCommand">
          <span class="user-menu">{{ authStore.user?.displayName ?? '未登录' }}</span>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item command="logout">退出</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      </div>
    </el-header>

    <el-main class="app-main">
      <router-view />
    </el-main>
  </el-container>
</template>

<style scoped>
.app-shell {
  min-height: 100%;
}

.app-header {
  display: flex;
  align-items: center;
  gap: 16px;
  border-bottom: 1px solid var(--el-border-color);
  background-color: var(--el-bg-color);
}

.app-brand {
  font-size: 18px;
  font-weight: 600;
}

.app-nav {
  flex: 1;
  border-bottom: none;
}

.app-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

.user-menu {
  cursor: pointer;
  color: var(--el-text-color-primary);
}

.app-main {
  background-color: var(--el-bg-color-page);
}
</style>

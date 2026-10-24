import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router';

import AppLayout from './layouts/AppLayout.vue';
import LoginView from './views/LoginView.vue';
import NotificationListView from './views/NotificationListView.vue';
import RepoListView from './views/RepoListView.vue';
import ReviewCreateView from './views/ReviewCreateView.vue';
import ReviewDetailView from './views/ReviewDetailView.vue';
import ReviewListView from './views/ReviewListView.vue';
import UserListView from './views/UserListView.vue';
import { useAuthStore } from './stores/auth';

declare module 'vue-router' {
  interface RouteMeta {
    /** 是否需要登录 */
    requiresAuth?: boolean;
    /** 是否仅管理员可访问 */
    requiresAdmin?: boolean;
  }
}

/** 应用路由表：/login 公开，其余页面挂在登录后的布局下 */
const routes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'login',
    component: LoginView,
  },
  {
    path: '/',
    component: AppLayout,
    meta: { requiresAuth: true },
    children: [
      { path: '', redirect: '/reviews' },
      {
        path: 'reviews',
        name: 'reviews',
        component: ReviewListView,
      },
      {
        // 必须排在 reviews/:id 之前，避免 new 被当作评审单主键
        path: 'reviews/new',
        name: 'review-create',
        component: ReviewCreateView,
      },
      {
        path: 'reviews/:id',
        name: 'review-detail',
        component: ReviewDetailView,
        props: true,
      },
      {
        path: 'repos',
        name: 'repos',
        component: RepoListView,
        meta: { requiresAdmin: true },
      },
      {
        path: 'users',
        name: 'users',
        component: UserListView,
        meta: { requiresAdmin: true },
      },
      {
        path: 'notifications',
        name: 'notifications',
        component: NotificationListView,
      },
    ],
  },
  { path: '/:pathMatch(.*)*', redirect: '/reviews' },
];

const router = createRouter({
  history: createWebHistory(),
  routes,
});

/**
 * 全局守卫：先确认登录状态，再校验管理员权限。
 */
router.beforeEach(async (to) => {
  if (!to.matched.some((record) => record.meta.requiresAuth === true)) {
    return true;
  }

  const authStore = useAuthStore();
  const authenticated = await authStore.ensureLoaded();
  if (!authenticated) {
    return { path: '/login', query: { redirect: to.fullPath } };
  }

  // 待改密用户只能停留在登录页完成强制改密
  if (authStore.mustChangePassword && to.path !== '/login') {
    return { path: '/login', query: { redirect: to.fullPath } };
  }

  if (to.matched.some((record) => record.meta.requiresAdmin === true) && !authStore.isAdmin) {
    return { path: '/reviews' };
  }

  return true;
});

export default router;

<script setup lang="ts">
import { reactive, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { AxiosError } from 'axios';
import { ElMessage, type FormInstance, type FormRules } from 'element-plus';

import type { ApiErrorBody } from '../api/types';
import { useAuthStore } from '../stores/auth';

/** 登录表单 */
interface LoginForm {
  /** 用户名 */
  username: string;
  /** 密码 */
  password: string;
}

/** 强制改密表单 */
interface ChangePasswordForm {
  /** 新密码 */
  newPassword: string;
  /** 确认密码 */
  confirmPassword: string;
}

/** 密码最小长度，与后端保持一致 */
const MIN_PASSWORD_LENGTH = 8;

const route = useRoute();
const router = useRouter();
const authStore = useAuthStore();

const loginFormRef = ref<FormInstance>();
const changeFormRef = ref<FormInstance>();
const loginForm = reactive<LoginForm>({ username: '', password: '' });
const changeForm = reactive<ChangePasswordForm>({ newPassword: '', confirmPassword: '' });
const submitting = ref(false);
const changingPassword = ref(false);
const changeDialogVisible = ref(false);

/** 登录表单校验规则 */
const loginRules: FormRules = {
  username: [{ required: true, message: '请输入用户名', trigger: 'blur' }],
  password: [{ required: true, message: '请输入密码', trigger: 'blur' }],
};

/** 改密表单校验规则：至少 8 位且两次输入一致 */
const changeRules: FormRules = {
  newPassword: [
    { required: true, message: '请输入新密码', trigger: 'blur' },
    {
      min: MIN_PASSWORD_LENGTH,
      message: `密码长度至少为 ${MIN_PASSWORD_LENGTH} 位`,
      trigger: 'blur',
    },
  ],
  confirmPassword: [
    { required: true, message: '请再次输入新密码', trigger: 'blur' },
    {
      validator: (_rule, value, callback) => {
        if (value !== changeForm.newPassword) {
          callback(new Error('两次输入的密码不一致'));
          return;
        }
        callback();
      },
      trigger: 'blur',
    },
  ],
};

/**
 * 从后端错误响应中取出可展示的信息。
 */
function getErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof AxiosError) {
    const body = error.response?.data as ApiErrorBody | undefined;
    if (body?.message) {
      return body.message;
    }
  }
  return fallback;
}

/**
 * 读取安全的站内跳转地址，拒绝协议相对地址与站外地址。
 */
function readRedirect(value: unknown): string {
  const raw = Array.isArray(value) ? value[0] : value;
  if (typeof raw !== 'string' || !raw.startsWith('/') || raw.startsWith('//') || raw.includes('\\')) {
    return '/reviews';
  }
  return raw;
}

/**
 * 登录成功后的跳转。
 */
async function goAfterLogin(): Promise<void> {
  await router.replace(readRedirect(route.query.redirect));
}

/**
 * 提交登录表单；需要强制改密时弹出不可关闭的改密弹窗。
 */
async function handleLogin(): Promise<void> {
  if (submitting.value || !loginFormRef.value) {
    return;
  }

  const valid = await loginFormRef.value.validate().catch(() => false);
  if (!valid) {
    return;
  }

  submitting.value = true;
  try {
    const current = await authStore.login(loginForm.username.trim(), loginForm.password);
    if (current.mustChangePassword) {
      changeDialogVisible.value = true;
      return;
    }
    await goAfterLogin();
  } catch (error) {
    ElMessage.error(getErrorMessage(error, '登录失败，请稍后重试'));
  } finally {
    submitting.value = false;
  }
}

/**
 * 提交强制改密表单，成功后进入系统。
 */
async function handleChangePassword(): Promise<void> {
  if (changingPassword.value || !changeFormRef.value) {
    return;
  }

  const valid = await changeFormRef.value.validate().catch(() => false);
  if (!valid) {
    return;
  }

  changingPassword.value = true;
  try {
    await authStore.changePassword(loginForm.password, changeForm.newPassword);
    changeDialogVisible.value = false;
    ElMessage.success('密码修改成功');
    await goAfterLogin();
  } catch (error) {
    ElMessage.error(getErrorMessage(error, '密码修改失败，请稍后重试'));
  } finally {
    changingPassword.value = false;
  }
}
</script>

<template>
  <div class="login-page">
    <el-card class="login-card">
      <template #header>
        <div class="login-title">CodeSentinel</div>
      </template>

      <el-form
        ref="loginFormRef"
        :model="loginForm"
        :rules="loginRules"
        label-position="top"
        @submit.prevent="handleLogin"
      >
        <el-form-item label="用户名" prop="username">
          <el-input v-model="loginForm.username" autocomplete="username" />
        </el-form-item>
        <el-form-item label="密码" prop="password">
          <el-input
            v-model="loginForm.password"
            type="password"
            autocomplete="current-password"
            show-password
          />
        </el-form-item>
        <el-button
          type="primary"
          native-type="submit"
          :loading="submitting"
          class="login-submit"
        >
          登录
        </el-button>
      </el-form>
    </el-card>

    <el-dialog
      v-model="changeDialogVisible"
      title="首次登录请修改密码"
      width="420px"
      :close-on-click-modal="false"
      :close-on-press-escape="false"
      :show-close="false"
    >
      <el-form
        ref="changeFormRef"
        :model="changeForm"
        :rules="changeRules"
        label-position="top"
      >
        <el-form-item label="新密码" prop="newPassword">
          <el-input
            v-model="changeForm.newPassword"
            type="password"
            autocomplete="new-password"
            show-password
          />
        </el-form-item>
        <el-form-item label="确认新密码" prop="confirmPassword">
          <el-input
            v-model="changeForm.confirmPassword"
            type="password"
            autocomplete="new-password"
            show-password
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button type="primary" :loading="changingPassword" @click="handleChangePassword">
          保存并进入
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.login-page {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 100%;
  background-color: var(--el-bg-color-page);
}

.login-card {
  width: 380px;
}

.login-title {
  font-size: 20px;
  font-weight: 600;
  text-align: center;
}

.login-submit {
  width: 100%;
}
</style>

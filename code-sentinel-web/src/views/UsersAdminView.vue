<script setup lang="ts">
/**
 * 用户管理页：用户列表、新建、编辑、启用/停用与重置密码。
 */
import { onMounted, reactive, ref, watch } from 'vue';
import { ElMessage, ElMessageBox, type FormInstance, type FormRules } from 'element-plus';

import {
  createUser,
  fetchUsers,
  resetUserPassword,
  updateUser,
} from '../api/users';
import type { AdminUser, CreateUserInput, UpdateUserInput } from '../api/types';
import { apiErrorMessage } from '../utils/apiError';

/** 用户列表每页条数 */
const PAGE_SIZE = 20;

/**
 * 新建用户表单状态。
 */
interface CreateUserForm {
  /** 登录名 */
  username: string;
  /** 显示名 */
  displayName: string;
  /** 邮箱，可选 */
  email: string;
  /** 初始密码 */
  password: string;
  /** 是否管理员 */
  isAdmin: boolean;
}

/**
 * 编辑用户表单状态。
 */
interface EditUserForm {
  /** 显示名 */
  displayName: string;
  /** 是否管理员 */
  isAdmin: boolean;
}

/** 用户列表 */
const users = ref<AdminUser[]>([]);
/** 总条数 */
const total = ref(0);
/** 当前页码 */
const page = ref(1);
/** 列表加载状态 */
const loading = ref(false);
/** 正在切换状态的用户主键 */
const updatingId = ref<string | null>(null);

/** 新建弹窗是否可见 */
const createVisible = ref(false);
/** 新建表单引用 */
const createFormRef = ref<FormInstance>();
/** 新建提交中状态 */
const creating = ref(false);
/** 新建表单状态 */
const createForm = reactive<CreateUserForm>({
  username: '',
  displayName: '',
  email: '',
  password: '',
  isAdmin: false,
});

/** 编辑弹窗是否可见 */
const editVisible = ref(false);
/** 正在编辑的用户 */
const editingUser = ref<AdminUser | null>(null);
/** 编辑表单引用 */
const editFormRef = ref<FormInstance>();
/** 编辑提交中状态 */
const savingEdit = ref(false);
/** 编辑表单状态 */
const editForm = reactive<EditUserForm>({
  displayName: '',
  isAdmin: false,
});

/** 一次性密码弹窗是否可见 */
const passwordVisible = ref(false);
/** 一次性新密码 */
const oneTimePassword = ref('');

/** 新建表单校验规则 */
const createRules: FormRules<CreateUserForm> = {
  username: [{ required: true, message: '请输入用户名', trigger: 'blur' }],
  displayName: [{ required: true, message: '请输入显示名', trigger: 'blur' }],
  password: [{ required: true, message: '请输入初始密码', trigger: 'blur' }],
};

/** 编辑表单校验规则 */
const editRules: FormRules<EditUserForm> = {
  displayName: [{ required: true, message: '请输入显示名', trigger: 'blur' }],
};

/**
 * 拉取用户列表。
 */
async function loadUsers(): Promise<void> {
  loading.value = true;
  try {
    const result = await fetchUsers(page.value, PAGE_SIZE);
    users.value = result.items;
    total.value = result.total;
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '用户列表加载失败，请稍后重试'));
  } finally {
    loading.value = false;
  }
}

/**
 * 打开新建用户弹窗。
 */
function openCreate(): void {
  createForm.username = '';
  createForm.displayName = '';
  createForm.email = '';
  createForm.password = '';
  createForm.isAdmin = false;
  createVisible.value = true;
  createFormRef.value?.clearValidate();
}

/**
 * 提交新建用户。
 */
async function submitCreate(): Promise<void> {
  const valid = await createFormRef.value?.validate().catch(() => false);
  if (valid !== true) {
    return;
  }

  const input: CreateUserInput = {
    username: createForm.username.trim(),
    displayName: createForm.displayName.trim(),
    password: createForm.password,
    isAdmin: createForm.isAdmin,
  };
  const email = createForm.email.trim();
  if (email !== '') {
    input.email = email;
  }

  creating.value = true;
  try {
    await createUser(input);
    ElMessage.success('用户已创建，首次登录后需修改密码');
    createVisible.value = false;
    await loadUsers();
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '用户创建失败，请稍后重试'));
  } finally {
    creating.value = false;
  }
}

/**
 * 打开编辑用户弹窗。
 * @param user 待编辑的用户
 */
function openEdit(user: AdminUser): void {
  editingUser.value = user;
  editForm.displayName = user.displayName;
  editForm.isAdmin = user.isAdmin;
  editVisible.value = true;
  editFormRef.value?.clearValidate();
}

/**
 * 提交用户编辑。
 */
async function submitEdit(): Promise<void> {
  const user = editingUser.value;
  if (user === null) {
    return;
  }

  const valid = await editFormRef.value?.validate().catch(() => false);
  if (valid !== true) {
    return;
  }

  const input: UpdateUserInput = {
    displayName: editForm.displayName.trim(),
    isAdmin: editForm.isAdmin,
  };

  savingEdit.value = true;
  try {
    await updateUser(user.id, input);
    ElMessage.success('用户已保存');
    editVisible.value = false;
    await loadUsers();
  } catch (error) {
    // 后端 LAST_ADMIN 等业务错误直接展示
    ElMessage.error(apiErrorMessage(error, '用户保存失败，请稍后重试'));
  } finally {
    savingEdit.value = false;
  }
}

/**
 * 启用或停用用户；停用前需要确认，最后一个管理员由后端拦截。
 * @param user 目标用户
 */
async function toggleActive(user: AdminUser): Promise<void> {
  if (user.isActive) {
    try {
      await ElMessageBox.confirm(
        `停用后该用户将无法登录，且会从进行中的评审中移除。确认停用「${user.displayName}」？`,
        '停用用户',
        {
          type: 'warning',
          confirmButtonText: '停用',
          cancelButtonText: '取消',
        },
      );
    } catch {
      return;
    }
  }

  updatingId.value = user.id;
  try {
    await updateUser(user.id, { isActive: !user.isActive });
    ElMessage.success(user.isActive ? '用户已停用' : '用户已启用');
    await loadUsers();
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '用户状态更新失败，请稍后重试'));
  } finally {
    updatingId.value = null;
  }
}

/**
 * 重置密码：确认后展示仅本次返回的一次性新密码。
 * @param user 目标用户
 */
async function resetPassword(user: AdminUser): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `将为「${user.displayName}」生成新的临时密码，原密码立即失效。是否继续？`,
      '重置密码',
      {
        type: 'warning',
        confirmButtonText: '重置',
        cancelButtonText: '取消',
      },
    );
  } catch {
    return;
  }

  updatingId.value = user.id;
  try {
    const result = await resetUserPassword(user.id);
    oneTimePassword.value = result.password;
    passwordVisible.value = true;
    await loadUsers();
  } catch (error) {
    ElMessage.error(apiErrorMessage(error, '密码重置失败，请稍后重试'));
  } finally {
    updatingId.value = null;
  }
}

/**
 * 复制一次性密码到剪贴板。
 */
async function copyPassword(): Promise<void> {
  try {
    await navigator.clipboard.writeText(oneTimePassword.value);
    ElMessage.success('密码已复制');
  } catch {
    ElMessage.error('复制失败，请手动选择密码复制');
  }
}

// 页码变化时加载对应页
watch(page, () => {
  void loadUsers();
});

onMounted(() => {
  void loadUsers();
});
</script>

<template>
  <section class="user-admin">
    <header class="user-admin-header">
      <h2 class="user-admin-title">用户管理</h2>
      <el-button type="primary" class="create-user-button" @click="openCreate">
        <svg
          class="user-admin-create-icon"
          viewBox="0 0 24 24"
          width="14"
          height="14"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          aria-hidden="true"
        >
          <path d="M12 5v14M5 12h14" />
        </svg>
        新建用户
      </el-button>
    </header>

    <el-table
      v-loading="loading"
      :data="users"
      border
      stripe
      empty-text="暂无用户"
    >
      <el-table-column prop="username" label="用户名" min-width="140" show-overflow-tooltip />

      <el-table-column prop="displayName" label="显示名" min-width="140" show-overflow-tooltip />

      <el-table-column label="角色" width="100" align="center">
        <template #default="{ row }">
          <el-tag v-if="row.isAdmin" type="warning" size="small">管理员</el-tag>
          <span v-else>普通用户</span>
        </template>
      </el-table-column>

      <el-table-column label="状态" width="100" align="center">
        <template #default="{ row }">
          <el-tag :type="row.isActive ? 'success' : 'info'" size="small">
            {{ row.isActive ? '启用' : '已停用' }}
          </el-tag>
        </template>
      </el-table-column>

      <el-table-column label="密码状态" width="110" align="center">
        <template #default="{ row }">
          <el-tag v-if="row.mustChangePassword" type="warning" size="small">待改密</el-tag>
          <span v-else>已设置</span>
        </template>
      </el-table-column>

      <el-table-column label="操作" width="240" align="center">
        <template #default="{ row }">
          <el-button link type="primary" size="small" class="edit-user-button" @click="openEdit(row)">
            编辑
          </el-button>
          <el-button
            link
            type="primary"
            size="small"
            class="reset-password-button"
            :loading="updatingId === row.id"
            @click="resetPassword(row)"
          >
            重置密码
          </el-button>
          <el-button
            v-if="row.isActive"
            link
            type="danger"
            size="small"
            class="deactivate-user-button"
            :loading="updatingId === row.id"
            @click="toggleActive(row)"
          >
            停用
          </el-button>
          <el-button
            v-else
            link
            type="primary"
            size="small"
            class="activate-user-button"
            :loading="updatingId === row.id"
            @click="toggleActive(row)"
          >
            启用
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-pagination
      v-model:current-page="page"
      class="user-admin-pagination"
      background
      layout="total, prev, pager, next"
      :page-size="PAGE_SIZE"
      :total="total"
    />

    <el-dialog v-model="createVisible" title="新建用户" width="480px" class="create-user-dialog">
      <el-form ref="createFormRef" :model="createForm" :rules="createRules" label-width="90px">
        <el-form-item label="用户名" prop="username">
          <el-input v-model="createForm.username" placeholder="请输入用户名" autocomplete="off" />
        </el-form-item>

        <el-form-item label="显示名" prop="displayName">
          <el-input v-model="createForm.displayName" placeholder="请输入显示名" />
        </el-form-item>

        <el-form-item label="邮箱">
          <el-input v-model="createForm.email" placeholder="请输入邮箱（可选）" />
        </el-form-item>

        <el-form-item label="初始密码" prop="password">
          <el-input
            v-model="createForm.password"
            type="password"
            placeholder="请输入初始密码"
            autocomplete="new-password"
            show-password
          />
        </el-form-item>

        <el-form-item label="管理员">
          <el-switch v-model="createForm.isAdmin" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="createVisible = false">取消</el-button>
        <el-button
          type="primary"
          class="user-dialog-save"
          :loading="creating"
          @click="submitCreate"
        >
          确定
        </el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="editVisible" title="编辑用户" width="480px" class="edit-user-dialog">
      <el-form ref="editFormRef" :model="editForm" :rules="editRules" label-width="90px">
        <el-form-item label="用户名">
          <el-input :model-value="editingUser?.username ?? ''" disabled />
        </el-form-item>

        <el-form-item label="显示名" prop="displayName">
          <el-input v-model="editForm.displayName" placeholder="请输入显示名" />
        </el-form-item>

        <el-form-item label="管理员">
          <el-switch v-model="editForm.isAdmin" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="editVisible = false">取消</el-button>
        <el-button
          type="primary"
          class="user-edit-save"
          :loading="savingEdit"
          @click="submitEdit"
        >
          确定
        </el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="passwordVisible" title="一次性密码" width="460px" class="password-dialog">
      <p class="password-warning">
        该密码仅显示一次，请立即转告用户并妥善保存；用户首次登录后必须修改密码。
      </p>
      <div class="password-value">{{ oneTimePassword }}</div>

      <template #footer>
        <el-button @click="copyPassword">复制密码</el-button>
        <el-button type="primary" @click="passwordVisible = false">关闭</el-button>
      </template>
    </el-dialog>
  </section>
</template>

<style scoped>
.user-admin-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 16px;
}

.user-admin-title {
  margin: 0;
  font-size: 18px;
}

.user-admin-create-icon {
  margin-right: 4px;
}

.user-admin-pagination {
  margin-top: 16px;
  justify-content: flex-end;
}

.password-warning {
  margin: 0 0 12px;
  color: var(--el-color-warning);
}

.password-value {
  padding: 12px;
  border: 1px dashed var(--el-border-color);
  border-radius: 4px;
  font-family: monospace;
  font-size: 16px;
  letter-spacing: 1px;
  text-align: center;
  word-break: break-all;
}
</style>

<script setup lang="ts">
/**
 * 评审列表页：三个范围标签、仓库与状态筛选、分页，以及进入新建评审的入口。
 */
import { onMounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { ElMessage } from 'element-plus';

import { fetchRepos } from '../api/repos';
import { fetchReviews } from '../api/reviews';
import type { Repo, ReviewListItem, ReviewScope, ReviewStatus } from '../api/types';
import ReviewStatusTag from '../components/ReviewStatusTag.vue';
import { formatDateTime } from '../utils/datetime';

/** 每页条数 */
const PAGE_SIZE = 20;

/** 状态下拉选项，空串表示不过滤 */
const STATUS_OPTIONS: { value: ReviewStatus; label: string }[] = [
  { value: 'OPEN', label: '评审中' },
  { value: 'CHANGES_REQUESTED', label: '需修改' },
  { value: 'APPROVED', label: '已通过' },
  { value: 'MERGED', label: '已合并' },
  { value: 'CLOSED', label: '已关闭' },
];

const router = useRouter();

/** 当前标签页，与 el-tabs 的 v-model 保持字符串类型 */
const scope = ref('to_review');
/** 仓库筛选，空串表示全部仓库 */
const repoId = ref('');
/** 状态筛选，空串表示全部状态 */
const status = ref('');
/** 当前页码 */
const page = ref(1);
/** 总条数 */
const total = ref(0);
/** 列表数据 */
const reviews = ref<ReviewListItem[]>([]);
/** 仓库下拉数据 */
const repos = ref<Repo[]>([]);
/** 列表加载状态 */
const loading = ref(false);

/**
 * 读取当前标签对应的查询范围。
 * @returns 后端支持的查询范围
 */
function currentScope(): ReviewScope {
  if (scope.value === 'mine' || scope.value === 'repo') {
    return scope.value;
  }

  return 'to_review';
}

/**
 * 读取当前状态筛选值，非法值视为不过滤。
 * @returns 状态枚举，未选择时为 undefined
 */
function currentStatus(): ReviewStatus | undefined {
  return STATUS_OPTIONS.find((item) => item.value === status.value)?.value;
}

/**
 * 拉取评审列表，失败时提示并保留上次数据。
 */
async function loadReviews(): Promise<void> {
  loading.value = true;
  try {
    const result = await fetchReviews({
      scope: currentScope(),
      repoId: repoId.value === '' ? undefined : repoId.value,
      status: currentStatus(),
      page: page.value,
      pageSize: PAGE_SIZE,
    });
    reviews.value = result.items;
    total.value = result.total;
  } catch {
    ElMessage.error('评审列表加载失败，请稍后重试');
  } finally {
    loading.value = false;
  }
}

/**
 * 拉取可筛选的仓库列表；失败不影响主列表展示。
 */
async function loadRepos(): Promise<void> {
  try {
    const result = await fetchRepos();
    repos.value = result.items;
  } catch {
    ElMessage.error('仓库列表加载失败，请稍后重试');
  }
}

/**
 * 进入新建评审页。
 */
async function openCreate(): Promise<void> {
  await router.push('/reviews/new');
}

/**
 * 打开评审详情。
 * @param row 被点击的评审单行
 */
async function openReview(row: ReviewListItem): Promise<void> {
  await router.push(`/reviews/${row.id}`);
}

// 筛选条件变化时回到第一页；页码已是 1 时直接重新加载
watch([scope, repoId, status], () => {
  if (page.value !== 1) {
    page.value = 1;
    return;
  }

  void loadReviews();
});

// 页码变化时加载对应页
watch(page, () => {
  void loadReviews();
});

onMounted(() => {
  void loadRepos();
  void loadReviews();
});
</script>

<template>
  <section class="review-list">
    <header class="review-list-header">
      <h2 class="review-list-title">评审</h2>
      <el-button type="primary" @click="openCreate">
        <svg
          class="review-list-create-icon"
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
        新建评审
      </el-button>
    </header>

    <el-tabs v-model="scope" class="review-list-tabs">
      <el-tab-pane label="待我评审" name="to_review" />
      <el-tab-pane label="我提交的" name="mine" />
      <el-tab-pane label="全部" name="repo" />
    </el-tabs>

    <div class="review-list-filters">
      <el-select
        v-model="repoId"
        class="review-list-repo-filter"
        placeholder="全部仓库"
        clearable
      >
        <el-option
          v-for="repo in repos"
          :key="repo.id"
          :label="repo.name"
          :value="repo.id"
        />
      </el-select>

      <el-select
        v-model="status"
        class="review-list-status-filter"
        placeholder="全部状态"
        clearable
      >
        <el-option
          v-for="item in STATUS_OPTIONS"
          :key="item.value"
          :label="item.label"
          :value="item.value"
        />
      </el-select>
    </div>

    <el-table
      v-loading="loading"
      :data="reviews"
      border
      stripe
      empty-text="暂无评审单"
      @row-click="openReview"
    >
      <el-table-column label="状态" width="100" align="center">
        <template #default="{ row }">
          <ReviewStatusTag :status="row.status" />
        </template>
      </el-table-column>

      <el-table-column prop="title" label="标题" min-width="220" show-overflow-tooltip />

      <el-table-column label="仓库" min-width="140">
        <template #default="{ row }">{{ row.repo.name }}</template>
      </el-table-column>

      <el-table-column label="分支" min-width="220" show-overflow-tooltip>
        <template #default="{ row }">{{ row.headBranch }} → {{ row.baseBranch }}</template>
      </el-table-column>

      <el-table-column label="审核进度" width="100" align="center">
        <template #default="{ row }">{{ row.reviewersApproved }}/{{ row.reviewersTotal }}</template>
      </el-table-column>

      <el-table-column label="提交人" width="120">
        <template #default="{ row }">{{ row.submitter.displayName }}</template>
      </el-table-column>

      <el-table-column label="更新时间" width="160">
        <template #default="{ row }">{{ formatDateTime(row.updatedAt) }}</template>
      </el-table-column>
    </el-table>

    <el-pagination
      v-model:current-page="page"
      class="review-list-pagination"
      background
      layout="total, prev, pager, next"
      :page-size="PAGE_SIZE"
      :total="total"
    />
  </section>
</template>

<style scoped>
.review-list-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
}

.review-list-title {
  margin: 0;
  font-size: 20px;
}

.review-list-create-icon {
  margin-right: 4px;
}

.review-list-filters {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
}

.review-list-repo-filter,
.review-list-status-filter {
  width: 220px;
}

.review-list-pagination {
  justify-content: flex-end;
  margin-top: 16px;
}
</style>

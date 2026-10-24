<script setup lang="ts">
/**
 * 评审状态标签：把后端状态映射为中文标签与语义色。
 */
import { computed } from 'vue';

import type { ReviewStatus } from '../api/types';

/**
 * 状态元数据：中文标签与 Element Plus 标签类型。
 */
interface StatusMeta {
  /** 中文标签 */
  label: string;
  /** 标签类型 */
  type: 'info' | 'danger' | 'success' | 'primary';
}

/**
 * 五种评审状态到标签的映射，状态按后端 SNAKE_CASE_UPPER 取值。
 */
const STATUS_META: Record<ReviewStatus, StatusMeta> = {
  OPEN: { label: '评审中', type: 'info' },
  CHANGES_REQUESTED: { label: '需修改', type: 'danger' },
  APPROVED: { label: '已通过', type: 'success' },
  MERGED: { label: '已合并', type: 'primary' },
  CLOSED: { label: '已关闭', type: 'info' },
};

const props = defineProps<{
  /** 评审单状态 */
  status: ReviewStatus;
}>();

/** 当前状态对应的展示信息 */
const meta = computed(() => STATUS_META[props.status]);
</script>

<template>
  <el-tag :type="meta.type" disable-transitions>{{ meta.label }}</el-tag>
</template>

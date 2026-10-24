<script setup lang="ts">
/**
 * 已看过复选框：封装 Element Plus 复选框，历史版本禁用并给出提示。
 */
const props = defineProps<{
  /** 是否已看过 */
  modelValue: boolean;
  /** 是否禁用（历史版本只读） */
  disabled: boolean;
  /** 是否正在提交，提交期间禁用防止重复点击 */
  pending: boolean;
}>();

const emit = defineEmits<{
  /** 复选框切换 */
  'update:modelValue': [value: boolean];
}>();

/**
 * 切换复选框，禁用或提交中时不处理。
 * @param value 目标状态
 */
function onChange(value: boolean): void {
  if (props.disabled || props.pending) {
    return;
  }

  emit('update:modelValue', value);
}
</script>

<template>
  <el-tooltip
    :disabled="!disabled"
    content="正在查看历史版本，仅可查看"
    placement="top"
  >
    <el-checkbox
      class="viewed-checkbox"
      :model-value="modelValue"
      :disabled="disabled || pending"
      @update:model-value="onChange"
    />
  </el-tooltip>
</template>

<script lang="ts">
import type { Component } from 'vue'

export interface EntityRowAction {
  label: string
  run: () => void
  type?: 'warning' | 'success' | 'danger'
  icon?: Component
  busy?: boolean
  disabled?: boolean
}
</script>

<script setup lang="ts">
import { ref } from 'vue'
import { ArrowDown } from '@element-plus/icons-vue'
import type { ButtonInstance, DropdownInstance } from 'element-plus'

defineProps<{ rowName: string; actions: EntityRowAction[] }>()

const dropdown = ref<DropdownInstance>()
const button = ref<ButtonInstance>()

function dismiss(event: KeyboardEvent) {
  const trigger = button.value?.ref
  if (trigger?.getAttribute('aria-expanded') !== 'true') return
  event.preventDefault()
  event.stopPropagation()
  dropdown.value?.handleClose()
  trigger.focus({ preventScroll: true })
}
</script>

<template>
  <el-dropdown ref="dropdown" trigger="click" placement="bottom-end">
    <el-button ref="button" link type="primary" :aria-label="`${rowName}的操作`" @keydown.esc="dismiss">
      操作 <el-icon aria-hidden="true"><ArrowDown /></el-icon>
    </el-button>
    <template #dropdown>
      <el-dropdown-menu @keydown.esc="dismiss">
        <el-dropdown-item
          v-for="action in actions"
          :key="action.label"
          :icon="action.icon"
          :disabled="action.disabled || action.busy"
          :divided="action.type === 'danger'"
          @click="action.run"
        >
          <span :class="{ 'danger-action': action.type === 'danger' }">{{ action.label }}</span>
        </el-dropdown-item>
      </el-dropdown-menu>
    </template>
  </el-dropdown>
</template>

<style scoped>
.danger-action {
  color: var(--el-color-danger);
}
</style>

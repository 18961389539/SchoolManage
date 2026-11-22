<script lang="ts">
import type { Component } from 'vue'

export interface EntityRowAction {
  label: string
  run: () => void
  type?: 'warning' | 'success' | 'danger'
  icon?: Component
  busy?: boolean
  disabled?: boolean
  reason?: string
}
</script>

<script setup lang="ts">
import { ref } from 'vue'
import { ArrowDown } from '@element-plus/icons-vue'
import type { ButtonInstance, DropdownInstance } from 'element-plus'

defineProps<{ rowName: string; actions: EntityRowAction[] }>()

const dropdown = ref<DropdownInstance>()
const button = ref<ButtonInstance>()

function runAction(action: EntityRowAction) {
  if (action.disabled || action.busy) return
  action.run()
}

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
          :title="action.disabled && action.reason ? action.reason : undefined"
          :class="{ 'has-disabled-reason': action.disabled && action.reason }"
          @click="runAction(action)"
        >
          <el-tooltip :disabled="!(action.disabled && action.reason)" :content="action.reason" placement="left">
            <span :class="{ 'danger-action': action.type === 'danger' }">{{ action.label }}</span>
          </el-tooltip>
        </el-dropdown-item>
      </el-dropdown-menu>
    </template>
  </el-dropdown>
</template>

<style scoped>
.danger-action {
  color: var(--el-color-danger);
}

:deep(.el-dropdown-menu__item.is-disabled.has-disabled-reason) {
  pointer-events: auto;
  cursor: not-allowed;
}
</style>

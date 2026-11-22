<script setup lang="ts">
import { nextTick, useTemplateRef, watch } from 'vue'
import { Close, QuestionFilled, School } from '@element-plus/icons-vue'
import type { NavGroup } from '../navigation'

const props = defineProps<{
  groups: NavGroup[]
  route: string
  homeKey: string
  schoolName: string
  version: string
  open: boolean
}>()
const emit = defineEmits<{ navigate: [key: string]; help: []; close: [] }>()
const sidebar = useTemplateRef<HTMLElement>('sidebar')
const closeButton = useTemplateRef<HTMLButtonElement>('closeButton')

watch(
  () => props.open,
  async open => {
    if (!open) return
    await nextTick()
    closeButton.value?.focus({ preventScroll: true })
  }
)

function onTab(event: KeyboardEvent) {
  if (!props.open) return
  const buttons = sidebar.value?.querySelectorAll<HTMLButtonElement>('button:not([disabled])')
  const first = buttons?.[0]
  const last = buttons?.[buttons.length - 1]
  const boundary = event.shiftKey ? first : last
  if (boundary && document.activeElement === boundary) {
    event.preventDefault()
    const target = event.shiftKey ? last : first
    target?.focus()
  }
}
</script>

<template>
  <div v-if="open" class="sidebar-backdrop" @click="emit('close')" />
  <aside
    id="workspace-navigation"
    ref="sidebar"
    class="sidebar"
    :class="{ 'is-open': open }"
    :role="open ? 'dialog' : undefined"
    :aria-modal="open || undefined"
    aria-label="工作台导航"
    @keydown.esc.stop="open && emit('close')"
    @keydown.tab="onTab"
  >
    <div class="sidebar-brand">
      <button type="button" class="brand-home" aria-label="实训资源平台首页" @click="emit('navigate', homeKey)">
        <span class="brand-mark" aria-hidden="true"><span /><span /><span /></span>
        <strong>实训资源平台</strong>
      </button>
      <button ref="closeButton" type="button" class="mobile-close" aria-label="关闭导航" @click="emit('close')">
        <el-icon><Close /></el-icon>
      </button>
    </div>
    <div class="school-name" :title="schoolName">
      <span class="school-symbol"
        ><el-icon><School /></el-icon></span
      ><span>{{ schoolName }}</span>
    </div>
    <nav class="main-navigation" aria-label="主导航">
      <div v-for="group in groups" :key="group.label" class="nav-group">
        <div class="nav-group-label">{{ group.label }}</div>
        <button
          v-for="item in group.items"
          :key="item.key"
          type="button"
          :class="{ active: route === item.key }"
          :aria-current="route === item.key ? 'page' : undefined"
          @click="emit('navigate', item.key)"
        >
          <el-icon :size="18" aria-hidden="true"><component :is="item.icon" /></el-icon><span>{{ item.label }}</span>
        </button>
      </div>
    </nav>
    <div class="sidebar-bottom">
      <button type="button" @click="emit('help')">
        <el-icon><QuestionFilled /></el-icon><span>使用帮助</span>
      </button>
      <div class="sidebar-version">
        <span>版本</span><span>v{{ version }}</span>
      </div>
    </div>
  </aside>
</template>

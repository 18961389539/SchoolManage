<script setup lang="ts">
import { computed, nextTick, onMounted, ref, useTemplateRef, watch } from 'vue'
import { ArrowDown, Close, QuestionFilled, School, Search } from '@element-plus/icons-vue'
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
const navigation = useTemplateRef<HTMLElement>('navigation')
const closeButton = useTemplateRef<HTMLButtonElement>('closeButton')

const storageKey = 'school-training:navigation-collapsed'
const activeGroup = computed(
  () => props.groups.find(group => group.items.some(item => item.key === props.route))?.label || ''
)
const collapsed = ref<Record<string, boolean>>({})
const filter = ref('')
const filtering = computed(() => !!filter.value.trim())
const filteredGroups = computed(() => {
  const keyword = filter.value.trim().toLowerCase()
  if (!keyword) return props.groups
  return props.groups
    .map(group => ({
      ...group,
      items: group.items.filter(item => item.label.toLowerCase().includes(keyword))
    }))
    .filter(group => group.items.length)
})
try {
  const saved = JSON.parse(localStorage.getItem(storageKey) || '{}') as Record<string, boolean> | null
  if (saved && typeof saved === 'object' && !Array.isArray(saved)) collapsed.value = saved
} catch {
  collapsed.value = {}
}
if (!Object.keys(collapsed.value).length) {
  const openGroup = activeGroup.value || props.groups[0]?.label || ''
  collapsed.value = Object.fromEntries(props.groups.map(group => [group.label, group.label !== openGroup]))
}

function toggleGroup(label: string) {
  collapsed.value = { ...collapsed.value, [label]: !collapsed.value[label] }
  try {
    localStorage.setItem(storageKey, JSON.stringify(collapsed.value))
  } catch {
    // Storage may be unavailable in private browsing; collapsing still works for this session.
  }
}

function revealActiveItem(behavior: ScrollBehavior = 'auto') {
  const nav = navigation.value
  const active = nav?.querySelector<HTMLElement>('.nav-group-items button.active')
  if (!nav || !active || !active.offsetParent) return
  const navBox = nav.getBoundingClientRect()
  const activeBox = active.getBoundingClientRect()
  if (activeBox.top >= navBox.top && activeBox.bottom <= navBox.bottom) return
  const top = nav.scrollTop + activeBox.top - navBox.top - (navBox.height - activeBox.height) / 2
  const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches
  nav.scrollTo({ top, behavior: reduceMotion ? 'auto' : behavior })
}

onMounted(() => revealActiveItem())

watch(
  () => props.open,
  async open => {
    if (!open) return
    await nextTick()
    revealActiveItem()
    closeButton.value?.focus({ preventScroll: true })
  }
)
watch(activeGroup, label => {
  if (label && collapsed.value[label]) collapsed.value = { ...collapsed.value, [label]: false }
})
// A filter is a one-off lookup: leaving the page restores the full navigation.
watch(
  () => props.route,
  () => {
    filter.value = ''
  }
)
watch(
  () => props.route,
  async () => {
    await nextTick()
    revealActiveItem('smooth')
  },
  { flush: 'post' }
)
watch(
  () => props.groups,
  groups => {
    const missing = groups.filter(group => !(group.label in collapsed.value))
    if (!missing.length) return
    collapsed.value = {
      ...collapsed.value,
      ...Object.fromEntries(missing.map(group => [group.label, group.label !== activeGroup.value]))
    }
  }
)

function onTab(event: KeyboardEvent) {
  if (!props.open) return
  const buttons = [...(sidebar.value?.querySelectorAll<HTMLButtonElement>('button:not([disabled])') ?? [])].filter(
    button => button.offsetParent !== null
  )
  const first = buttons[0]
  const last = buttons[buttons.length - 1]
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
      <span class="school-symbol" aria-hidden="true">
        <el-icon><School /></el-icon>
      </span>
      <span>{{ schoolName }}</span>
    </div>
    <div class="sidebar-filter">
      <el-input
        v-model="filter"
        size="small"
        clearable
        :prefix-icon="Search"
        placeholder="搜索功能"
        aria-label="搜索导航功能"
        @keydown.esc="filter = ''"
      />
    </div>
    <nav ref="navigation" class="main-navigation" aria-label="主导航">
      <div v-for="(group, index) in filteredGroups" :key="group.label" class="nav-group">
        <div v-if="filtering || groups.length === 1 || group.items.length === 1" class="nav-group-label">
          {{ group.label }}
        </div>
        <button
          v-else
          type="button"
          class="nav-group-toggle"
          :class="{ 'has-current-page': group.label === activeGroup && collapsed[group.label] }"
          :aria-expanded="!collapsed[group.label]"
          :aria-controls="`nav-group-${index}`"
          @click="toggleGroup(group.label)"
        >
          <span>{{ group.label }}</span>
          <el-icon class="nav-chevron" :class="{ 'is-collapsed': collapsed[group.label] }" aria-hidden="true">
            <ArrowDown />
          </el-icon>
        </button>
        <div
          v-show="filtering || groups.length === 1 || group.items.length === 1 || !collapsed[group.label]"
          :id="`nav-group-${index}`"
          class="nav-group-items"
        >
          <button
            v-for="item in group.items"
            :key="item.key"
            type="button"
            :class="{ active: route === item.key }"
            :aria-current="route === item.key ? 'page' : undefined"
            :title="item.label"
            @click="emit('navigate', item.key)"
          >
            <el-icon :size="18" aria-hidden="true"><component :is="item.icon" /></el-icon><span>{{ item.label }}</span>
          </button>
        </div>
      </div>
      <p v-if="filtering && !filteredGroups.length" class="nav-filter-empty">没有匹配的功能</p>
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

<style scoped>
.sidebar-filter {
  margin: 0 14px 12px;
}

.sidebar-filter :deep(.el-input__wrapper) {
  min-height: 34px;
  border-radius: 9px;
  background: #fff;
}

.nav-filter-empty {
  margin: 12px 18px;
  font-size: 12.5px;
  color: #6a7f90;
}
</style>

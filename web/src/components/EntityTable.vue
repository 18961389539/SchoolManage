<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Connection, Delete, Monitor, Refresh, Search } from '@element-plus/icons-vue'
import type { ButtonInstance, InputInstance, TableInstance } from 'element-plus'
import type { Row } from '../api'
import type { Entity, Field } from '../entities'
import EntityRowMenu, { type EntityRowAction } from './EntityRowMenu.vue'
import TableEmpty from './TableEmpty.vue'

const props = defineProps<{
  entity: Entity
  entityKey: string
  cabinetId?: string
  rows: Row[]
  total: number
  loading: boolean
  error: string
  filterError: string
  filterOptions: Record<string, Row[]>
  loadingFilters: boolean
  isPeople: boolean
  selectedCount: number
  actionLoading: string
}>()
const search = defineModel<string>('search', { required: true })
const filters = defineModel<Record<string, string>>('filters', { required: true })
const page = defineModel<number>('page', { required: true })
const pageSize = defineModel<number>('pageSize', { required: true })
const emit = defineEmits<{
  refresh: []
  reload: []
  'selection-change': [rows: Row[]]
  'batch-delete': []
  'test-cabinets': []
  terminal: [row: Row]
  'web-console': [row: Row]
  action: [row: Row, key: string]
  reset: [row: Row]
  view: [row: Row]
  edit: [row: Row]
  remove: [row: Row]
  navigate: [key: string, params?: Row]
}>()

const actionWidth = computed(() =>
  props.entityKey === 'devices'
    ? 295
    : props.entityKey === 'nodes'
      ? 345
      : props.entityKey === 'cabinets'
        ? 200
        : props.isPeople
          ? 225
          : 160
)

const tableViewport = window.matchMedia('(max-width: 1020px)')
const compact = ref(tableViewport.matches)
function syncViewport(event: MediaQueryListEvent) {
  compact.value = event.matches
}
onMounted(() => tableViewport.addEventListener('change', syncViewport))
onBeforeUnmount(() => tableViewport.removeEventListener('change', syncViewport))

const tableRef = ref<TableInstance>()
const searchInput = ref<InputInstance>()
const reloadButton = ref<ButtonInstance>()
const draftSearch = ref(search.value)
watch([() => props.entityKey, () => props.cabinetId, search], () => {
  draftSearch.value = search.value
})
watch([() => props.entityKey, () => props.cabinetId], () => tableRef.value?.setScrollLeft(0), { flush: 'post' })

defineExpose({ clearSelection: () => tableRef.value?.clearSelection() })

// Selection is reserved across pages (row-key + reserve-selection) so a batch delete can span
// pages; a new search replaces the result set, so it starts from a clean selection.
function searchNow() {
  draftSearch.value = draftSearch.value.trim()
  search.value = draftSearch.value
  page.value = 1
  tableRef.value?.clearSelection()
  emit('refresh')
}

function resetFilters() {
  draftSearch.value = ''
  filters.value = {}
  searchNow()
  searchInput.value?.focus()
}

function clearSelection() {
  tableRef.value?.clearSelection()
  void nextTick(() => reloadButton.value?.ref?.focus())
}

function setFilter(key: string, value: string | undefined) {
  filters.value = { ...filters.value, [key]: value ?? '' }
  searchNow()
}

function optionsFor(field: Field) {
  if (field.options) return field.options
  const rows = field.source ? (props.filterOptions[field.source] ?? []) : []
  return rows.map(row => ({
    value: row.id,
    label: row.professionName ? `${row.name} · ${row.professionName}` : row.name
  }))
}

const filtering = computed(() => !!search.value || Object.values(filters.value).some(Boolean))
const resultRange = computed(() => {
  if (props.loading || !props.rows.length) return ''
  const first = (page.value - 1) * pageSize.value + 1
  const last = first + props.rows.length - 1
  return `第 ${first === last ? first : `${first}–${last}`} 条`
})

function rowActions(row: Row): EntityRowAction[] {
  const actions: EntityRowAction[] = []
  if (props.entityKey === 'devices') {
    actions.push({ label: '终端', icon: Monitor, run: () => emit('terminal', row) })
    if (row.isSimulated) actions.push({ label: 'Web 管理', run: () => emit('web-console', row) })
    actions.push({
      label: '测试',
      busy: props.actionLoading === `${row.id}:test`,
      run: () => emit('action', row, 'test')
    })
  } else if (props.entityKey === 'cabinets') {
    actions.push(
      { label: '设备', run: () => emit('navigate', 'devices', { cabinetId: row.id }) },
      { label: '回收', type: 'warning', run: () => emit('action', row, 'reclaim') }
    )
  } else if (props.entityKey === 'nodes') {
    actions.push(
      { label: '状态', run: () => emit('action', row, 'status') },
      {
        label: row.enabled ? '停用' : '启用',
        type: row.enabled ? 'warning' : 'success',
        run: () => emit('action', row, 'enable')
      }
    )
    if (row.provider === 'simulated')
      actions.push({ label: '系统配置', run: () => emit('action', row, 'configuration') })
    actions.push({ label: '分配账号', run: () => emit('action', row, 'accounts') })
  } else {
    if (props.isPeople) actions.push({ label: '重置密码', type: 'warning', run: () => emit('reset', row) })
    actions.push({ label: '查看', run: () => emit('view', row) })
  }
  actions.push(
    { label: '编辑', run: () => emit('edit', row) },
    { label: '删除', type: 'danger', run: () => emit('remove', row) }
  )
  return actions
}

function usagePercent(value: unknown) {
  return typeof value === 'number' ? `${value}%` : '不可获取'
}

const cellFormatters: Record<string, (row: Row) => string> = {
  accountUsage: row => `${row.accountCount ?? 0} / ${row.accountQuota ?? 0}`,
  virtualMachineUsage: row => `${row.virtualMachineCount ?? 0} / ${row.virtualMachineQuota ?? 0}`,
  healthStatus: row => (row.healthStatus === 'healthy' ? '正常' : '不可获取'),
  cpuUsagePercent: row => usagePercent(row.cpuUsagePercent),
  memoryUsagePercent: row => usagePercent(row.memoryUsagePercent),
  diskUsagePercent: row => usagePercent(row.diskUsagePercent),
  role: row => ({ Admin: '管理员', Teacher: '教师' })[row.role as 'Admin' | 'Teacher'] || row.role,
  connectionType: row =>
    (({ simulated: '模拟设备', ssh: 'SSH', telnet: 'Telnet', tcp: 'TCP' }) as Row)[row.connectionType] ||
    row.connectionType,
  provider: row =>
    (({ simulated: '模拟节点', hyperv: 'Hyper-V', proxmox: 'Proxmox VE' }) as Row)[row.provider] || row.provider
}

function cell(row: Row, key: string) {
  const value = cellFormatters[key]?.(row) ?? row[key]
  return value === undefined || value === null || value === '' ? '—' : value
}
</script>

<template>
  <section class="surface entity-list" :aria-label="`${entity.singular}列表`">
    <div class="entity-filters" role="search" :aria-label="`${entity.singular}列表筛选`">
      <form class="search-box" @submit.prevent="searchNow">
        <el-input
          ref="searchInput"
          v-model="draftSearch"
          :aria-label="`搜索${entity.title}`"
          :prefix-icon="Search"
          clearable
          placeholder="输入关键词搜索"
          @clear="searchNow"
        />
        <el-button type="primary" native-type="submit">搜索</el-button>
      </form>
      <el-select
        v-for="field in entity.filters"
        :key="field.key"
        :model-value="filters[field.key]"
        :aria-label="`按${field.label}筛选`"
        :placeholder="`全部${field.label.replace('所属', '')}`"
        :loading="!!field.source && loadingFilters"
        class="table-filter"
        filterable
        clearable
        @update:model-value="setFilter(field.key, $event)"
      >
        <el-option
          v-for="option in optionsFor(field)"
          :key="option.value"
          :label="option.label"
          :value="option.value"
        />
      </el-select>
      <el-button v-if="filtering || draftSearch" text type="primary" @click="resetFilters">清除筛选</el-button>
    </div>

    <div class="entity-list-bar" :class="{ 'has-selection': selectedCount }">
      <div class="entity-counts">
        <span class="record-count" role="status">
          {{ loading ? '正在加载记录…' : error ? '无法获取记录数' : `共 ${total} 条记录` }}
        </span>
        <span v-if="isPeople || entityKey === 'cabinets'" role="status">
          <span v-if="selectedCount" class="selection-count">已选择 {{ selectedCount }} 项</span>
        </span>
      </div>
      <div class="entity-list-actions">
        <el-button v-if="selectedCount" text type="primary" @click="clearSelection">取消选择</el-button>
        <el-button v-if="isPeople && selectedCount" type="danger" plain :icon="Delete" @click="emit('batch-delete')">
          删除 {{ selectedCount }} 项
        </el-button>
        <el-tooltip
          v-if="entityKey === 'cabinets'"
          :disabled="!!selectedCount"
          content="请先勾选要测试的机柜"
          placement="bottom"
        >
          <span class="disabled-action-wrap">
            <el-button
              :icon="Connection"
              :loading="actionLoading === 'batch-test'"
              :disabled="!selectedCount"
              @click="emit('test-cabinets')"
            >
              测试选中机柜
            </el-button>
          </span>
        </el-tooltip>
        <el-button ref="reloadButton" :icon="Refresh" aria-label="刷新列表" @click="emit('reload')">刷新</el-button>
      </div>
    </div>
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="table-error" />
    <el-alert v-if="filterError" :title="filterError" type="error" show-icon :closable="false" class="table-error" />
    <el-table
      empty-text=""
      ref="tableRef"
      v-loading="loading"
      :data="rows"
      row-key="id"
      class="resource-table"
      :scrollbar-tabindex="rows.length ? 0 : undefined"
      scrollbar-always-on
      @selection-change="emit('selection-change', $event)"
    >
      <template #empty>
        <TableEmpty
          v-if="!loading"
          :title="error ? '数据加载失败' : filtering ? '没有符合条件的记录' : '暂无记录'"
          :hint="
            error ? '请检查服务状态后刷新重试' : filtering ? '调整关键词或筛选条件后再试' : '点击右上角「新增」开始录入'
          "
        >
          <template v-if="!error && filtering" #action>
            <el-button type="primary" plain @click="resetFilters">清除筛选</el-button>
          </template>
        </TableEmpty>
      </template>
      <el-table-column v-if="isPeople || entityKey === 'cabinets'" type="selection" width="48" reserve-selection />
      <el-table-column
        v-for="column in entity.columns"
        :key="column.key"
        :prop="column.key"
        :label="column.label"
        :min-width="column.width || 130"
        show-overflow-tooltip
      >
        <template #default="{ row }">
          <span v-if="column.key === 'name'" class="table-name">
            {{ cell(row, column.key) }}
            <el-tag v-if="row.isSimulated" type="warning" size="small" effect="light">模拟</el-tag>
          </span>
          <el-tag v-else-if="column.key === 'enabled'" :type="row.enabled ? 'success' : 'info'" effect="light">
            {{ row.enabled ? '允许分配' : '已停用' }}
          </el-tag>
          <el-tag
            v-else-if="
              (column.key === 'provider' || column.key === 'connectionType') && row[column.key] === 'simulated'
            "
            type="warning"
            effect="light"
          >
            {{ cell(row, column.key) }}
          </el-tag>
          <span v-else>{{ cell(row, column.key) }}</span>
        </template>
      </el-table-column>
      <el-table-column
        label="操作"
        fixed="right"
        :width="compact ? 86 : actionWidth"
        :align="compact ? 'center' : 'left'"
      >
        <template #default="{ row }">
          <EntityRowMenu v-if="compact" :row-name="row.name || row.userName" :actions="rowActions(row)" />
          <div v-else class="entity-row-actions">
            <el-button
              v-for="action in rowActions(row)"
              :key="action.label"
              link
              :type="action.type ?? 'primary'"
              :icon="action.icon"
              :loading="action.busy"
              @click="action.run"
            >
              {{ action.label }}
            </el-button>
          </div>
        </template>
      </el-table-column>
    </el-table>
    <p v-if="compact && rows.length" class="table-scroll-hint entity-scroll-hint">横向滚动可查看更多列</p>
    <div class="table-footer entity-footer">
      <span>{{ resultRange }}</span>
      <el-pagination
        v-model:current-page="page"
        v-model:page-size="pageSize"
        :total="total"
        :page-sizes="[10, 20, 50, 100]"
        :pager-count="compact ? 3 : 7"
        layout="sizes, prev, pager, next"
        @change="emit('refresh')"
      />
    </div>
  </section>
</template>

<style scoped>
.entity-list {
  --entity-gutter: 20px;
  min-width: 0;
}

.entity-filters,
.entity-list-bar,
.entity-counts,
.entity-list-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px 12px;
}

.entity-filters {
  padding: 18px var(--entity-gutter) 16px;
  background: #f8fbfb;
}

.entity-filters > .search-box {
  width: min(360px, 100%);
}

.entity-filters > .table-filter {
  flex: 0 1 176px;
  min-width: 0;
}

.entity-list-bar {
  justify-content: space-between;
  min-height: 56px;
  padding: 8px var(--entity-gutter);
  background: #f5f9f8;
  border-block: 1px solid var(--surface-border);
  transition:
    background 0.16s ease,
    border-color 0.16s ease;
}

.entity-list-bar.has-selection {
  background: #edf7f3;
  border-color: #d6eae1;
}

.entity-list-actions {
  gap: 6px 8px;
  margin-left: auto;
}

.entity-list-actions > .el-button + .el-button,
.entity-row-actions > .el-button + .el-button {
  margin-left: 0;
}

.selection-count {
  display: inline-block;
  padding: 4px 8px;
  border-radius: 6px;
  background: #dcefe6;
  color: #185b4f;
  font-size: 12px;
  font-weight: 600;
}

.entity-list .table-error {
  margin: 12px var(--entity-gutter);
}

.entity-list :deep(.el-scrollbar__wrap:focus-visible) {
  outline: 2px solid var(--el-color-primary);
  outline-offset: -2px;
}

.entity-list :deep(.el-table-fixed-column--right .cell) {
  padding-inline: 8px;
}

.entity-list :deep(tr:has(.el-table-column--selection .is-checked)) {
  --el-table-tr-bg-color: #f2faf6;
}

.entity-row-actions {
  display: flex;
  justify-content: flex-start;
  flex-wrap: wrap;
  gap: 2px 10px;
  padding: 2px 4px;
}

.entity-scroll-hint {
  margin: 10px var(--entity-gutter) 0;
}

.entity-footer {
  padding-inline: var(--entity-gutter);
  gap: 8px;
  min-height: 58px;
}

@media (max-width: 1020px) {
  .entity-list {
    --entity-gutter: 14px;
  }

  .entity-list :deep(.resource-table .cell) {
    padding-inline: 10px;
  }

  .entity-list :deep(.el-table .el-table-column--selection .cell),
  .entity-list :deep(.el-table-fixed-column--right .cell) {
    padding-inline: 8px;
  }

  .entity-list .table-name {
    white-space: normal;
    overflow-wrap: anywhere;
  }

  .entity-list .table-name .el-tag {
    margin-left: 4px;
  }
}

@media (max-width: 600px) {
  .entity-filters > .search-box {
    width: 100%;
  }

  .entity-filters > .table-filter {
    flex: 1 1 115px;
    max-width: 190px;
  }

  .entity-list-actions > .el-button {
    padding-inline: 8px;
  }
}
</style>

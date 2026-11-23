<script setup lang="ts">
import { computed, defineAsyncComponent, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, del, listItems, messageOf, post, put, type Row } from '../api'
import type { CabinetSummary, ConnectionProbe, ConnectionProbeList, MessageResponse, Paged } from '../types'
import { entities, type Field } from '../entities'
import { readListState, useListPersist } from '../listState'
import { recoverPage } from '../paging'
import { useRequestGeneration } from '../requestGeneration'
import DataRecord from './DataRecord.vue'
import EntityFormDialog from './EntityFormDialog.vue'
import EntityTable from './EntityTable.vue'
import EntityToolbar from './EntityToolbar.vue'
import NodeAccountsDialog from './NodeAccountsDialog.vue'
import NodeConfigurationDialog from './NodeConfigurationDialog.vue'
import ResetPasswordDialog from './ResetPasswordDialog.vue'
const ImportDialog = defineAsyncComponent(() => import('./ImportDialog.vue'))
const TerminalDialog = defineAsyncComponent(() => import('./TerminalDialog.vue'))
const DeviceWebConsole = defineAsyncComponent(() => import('./DeviceWebConsole.vue'))

const props = defineProps<{ entityKey: string; cabinetId?: string }>()
const emit = defineEmits<{ navigate: [key: string, params?: Row] }>()

const entity = computed(() => entities[props.entityKey]!)
const scopedCabinetId = computed(() => (props.entityKey === 'devices' ? props.cabinetId : undefined))
const cabinet = ref<CabinetSummary | null>(null)
const rows = ref<Row[]>([])
const total = ref(0)
const search = ref('')
const filters = ref<Record<string, string>>({})
const page = ref(1)
const pageSize = ref(20)
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const selected = ref<Row[]>([])
const formVisible = ref(false)
const editing = ref(false)
const initialRow = ref<Row | null>(null)
const options = reactive<Record<string, Row[]>>({})
const loadingFilters = ref(false)
const filterError = ref('')
const importVisible = ref(false)
const terminalDevice = ref<Row | null>(null)
const webDevice = ref<{ id: string; name: string } | null>(null)
const detailsVisible = ref(false)
const detailsTitle = ref('')
const details = ref<unknown>({})
const cabinetProbeResults = ref<ConnectionProbe[] | null>(null)
const actionLoading = ref('')
const resetVisible = ref(false)
const resetPerson = ref<Row | null>(null)
const configurationVisible = ref(false)
const configurationNode = ref<Row | null>(null)
const configuration = reactive<Row>({})
const accountNode = ref<Row | null>(null)
const isPeople = computed(() => !!entity.value.kind)
const tableRef = ref<InstanceType<typeof EntityTable>>()
const listLoads = useRequestGeneration()
const filterLoads = useRequestGeneration()
const formLoads = useRequestGeneration()
const configurationLoads = useRequestGeneration()
const actionLoads = useRequestGeneration()
let contextVersion = 0

type EntityList = Paged<Row> & { cabinet?: CabinetSummary | null }

const listKey = computed(() => `${props.entityKey}${scopedCabinetId.value ? `:${scopedCabinetId.value}` : ''}`)

function restoreListState() {
  const saved = readListState(listKey.value)
  const available = new Set((entity.value.filters ?? []).map(field => field.key))
  page.value = saved.page
  pageSize.value = saved.pageSize
  search.value = saved.search
  filters.value = Object.fromEntries(Object.entries(saved.filters).filter(([key]) => available.has(key)))
}

useListPersist(listKey, () => ({
  search: search.value,
  filters: filters.value,
  page: page.value,
  pageSize: pageSize.value
}))

// Selection survives paging (reserve-selection in EntityTable) and is cleared only when the
// underlying result set is replaced: another entity, a new search, or a destructive action.
function clearSelection() {
  selected.value = []
  tableRef.value?.clearSelection()
}

function showList(data: EntityList) {
  rows.value = listItems(data)
  total.value = data?.total ?? rows.value.length
  cabinet.value = data?.cabinet ?? null
  if (scopedCabinetId.value && !cabinet.value) error.value = '机柜不存在或已被删除，请返回机柜管理。'
}

async function load() {
  const version = listLoads.begin()
  loading.value = true
  error.value = ''
  try {
    const query = new URLSearchParams({
      search: search.value,
      page: String(page.value),
      pageSize: String(pageSize.value)
    })
    if (entity.value.kind) query.set('kind', entity.value.kind)
    for (const [key, value] of Object.entries(entity.value.fixedQuery ?? {})) {
      if (value) query.set(key, value)
    }
    if (scopedCabinetId.value) query.set('cabinetId', scopedCabinetId.value)
    for (const [key, value] of Object.entries(filters.value)) {
      if (value) query.set(key, value)
    }
    const data = await api<EntityList>(`${entity.value.endpoint}?${query}`)
    if (!listLoads.isCurrent(version)) return
    showList(data)
    const recovered = recoverPage(page.value, pageSize.value, total.value, rows.value.length)
    if (recovered !== null && recovered !== page.value) {
      page.value = recovered
      await load()
    }
  } catch (err) {
    if (listLoads.isCurrent(version)) {
      error.value = messageOf(err)
      cabinet.value = null
      rows.value = []
      total.value = 0
    }
  } finally {
    if (listLoads.isCurrent(version)) loading.value = false
  }
}

// Dropdown sources change rarely but are paged through in full on every form open. A short
// cache keeps opening the dialog cheap; any write to those lists clears it.
const optionCache = new Map<string, { at: number; rows: Row[] }>()
const optionCacheTtlMs = 60_000

async function loadOptions(fields: Field[]) {
  const sources = new Set(fields.flatMap(field => (field.source ? [field.source] : [])))
  for (const source of sources) {
    const cached = optionCache.get(source)
    if (cached && Date.now() - cached.at < optionCacheTtlMs) {
      options[source] = cached.rows
      continue
    }
    const all: Row[] = []
    const [path, sourceQuery] = source.split('?')
    const searchParams = new URLSearchParams(sourceQuery)
    searchParams.set('pageSize', '200')
    let current = 1
    for (;;) {
      searchParams.set('page', String(current))
      const data = await api<Paged<Row>>(`${path}?${searchParams}`)
      const batch = listItems<Row>(data)
      all.push(...batch)
      if (!batch.length || all.length >= (data.total ?? all.length)) break
      current++
      if (current > 100) break
    }
    options[source] = all
    optionCache.set(source, { at: Date.now(), rows: all })
  }
}

async function loadFilterOptions(refresh = false) {
  const version = filterLoads.begin()
  const fields = entity.value.filters ?? []
  filterError.value = ''
  loadingFilters.value = fields.some(field => !!field.source)
  if (refresh) {
    for (const field of fields) {
      if (field.source) optionCache.delete(field.source)
    }
  }
  try {
    await loadOptions(fields)
  } catch (err) {
    if (filterLoads.isCurrent(version)) filterError.value = `筛选选项加载失败：${messageOf(err)}`
  } finally {
    if (filterLoads.isCurrent(version)) loadingFilters.value = false
  }
}

function refresh() {
  void loadFilterOptions(true)
  void load()
}

async function openForm(row?: Row) {
  if (!row && scopedCabinetId.value && !cabinet.value) return
  const context = contextVersion
  const request = formLoads.begin()
  const nextEditing = !!row
  const nextRow = row ?? (cabinet.value ? { cabinetId: cabinet.value.id } : null)
  try {
    await loadOptions(entity.value.fields)
    if (context !== contextVersion || !formLoads.isCurrent(request)) return
    editing.value = nextEditing
    initialRow.value = nextRow
    formVisible.value = true
  } catch (err) {
    if (context === contextVersion && formLoads.isCurrent(request)) ElMessage.error(messageOf(err))
  }
}

async function save(payload: Row) {
  const context = contextVersion
  const { endpoint, singular } = entity.value
  saving.value = true
  try {
    if (editing.value) await put(`${endpoint}/${payload.id}`, payload)
    else await post(endpoint, payload)
    optionCache.clear()
    if (context !== contextVersion) return
    formVisible.value = false
    ElMessage.success(`${singular}已保存`)
    await load()
  } catch (err) {
    if (context === contextVersion) ElMessage.error(messageOf(err))
  } finally {
    if (context === contextVersion) saving.value = false
  }
}

async function remove(row: Row) {
  const context = contextVersion
  const { endpoint, singular } = entity.value
  try {
    await ElMessageBox.confirm(
      `确认删除${singular}“${row.name || row.userName}”？关联记录存在时系统会阻止删除。`,
      '删除确认',
      { type: 'warning', confirmButtonText: '确认删除', cancelButtonText: '取消' }
    )
    if (context !== contextVersion) return
    await del(`${endpoint}/${row.id}`)
    optionCache.clear()
    if (context !== contextVersion) return
    ElMessage.success('已删除')
    clearSelection()
    await load()
  } catch (err) {
    if (context === contextVersion && err !== 'cancel' && err !== 'close') ElMessage.error(messageOf(err))
  }
}

async function batchDelete() {
  const context = contextVersion
  const ids = selected.value.map(row => row.id)
  try {
    await ElMessageBox.confirm(`确认删除选中的 ${ids.length} 名${entity.value.singular}及其登录账号？`, '批量删除', {
      type: 'warning',
      confirmButtonText: '确认删除',
      cancelButtonText: '取消'
    })
    if (context !== contextVersion) return
    await post('/people/batch-delete', { ids })
    if (context !== contextVersion) return
    ElMessage.success('批量删除完成')
    clearSelection()
    await load()
  } catch (err) {
    if (context === contextVersion && err !== 'cancel' && err !== 'close') ElMessage.error(messageOf(err))
  }
}

function showDetails(data: unknown, title?: string) {
  cabinetProbeResults.value = null
  details.value = data
  detailsTitle.value = title || `${entity.value.singular}详情`
  detailsVisible.value = true
}

async function testDevice(row: Row) {
  const context = contextVersion
  const data = await post<ConnectionProbe>(`/devices/${row.id}/connect-test`)
  if (context !== contextVersion) return
  showDetails(data, `${row.name} · 连接测试`)
  if (data.success === false) ElMessage.warning(data.message || '连接未成功')
  else ElMessage.success(data.message || '连接测试完成')
}

async function reclaimCabinet(row: Row) {
  await ElMessageBox.confirm(
    `回收“${row.name}”将关闭平台中该机柜的终端会话并释放占用，不会恢复设备硬件配置。`,
    '回收机柜',
    { type: 'warning', confirmButtonText: '回收', cancelButtonText: '取消' }
  )
  const data = await post<MessageResponse & { releasedSessions: number }>(`/cabinets/${row.id}/reclaim`)
  ElMessage.success(data.message || '机柜会话已释放')
  await load()
}

async function showNodeStatus(row: Row) {
  const context = contextVersion
  const data = await api<Row>(`/nodes/${row.id}/status`)
  if (context !== contextVersion) return
  showDetails(data, `${row.name} · 节点状态`)
}

async function toggleNode(row: Row) {
  if (row.enabled) {
    await ElMessageBox.confirm('停用后暂停该节点的新资源分配。已运行的虚拟机保持当前状态。', '停用节点', {
      type: 'warning',
      confirmButtonText: '确认停用',
      cancelButtonText: '取消'
    })
  }
  await post(`/nodes/${row.id}/${row.enabled ? 'disable' : 'enable'}`)
  ElMessage.success('节点分配状态已更新')
  await load()
}

async function openConfiguration(row: Row) {
  const context = contextVersion
  const request = configurationLoads.begin()
  const data = await api<Row>(`/nodes/${row.id}/configuration`)
  if (context !== contextVersion || !configurationLoads.isCurrent(request)) return
  for (const key of Object.keys(configuration)) delete configuration[key]
  Object.assign(configuration, data)
  configurationNode.value = row
  configurationVisible.value = true
}

const actionHandlers: Record<string, (row: Row) => Promise<void>> = {
  test: testDevice,
  reclaim: reclaimCabinet,
  status: showNodeStatus,
  enable: toggleNode,
  configuration: openConfiguration,
  accounts: async row => {
    accountNode.value = row
  }
}

async function action(row: Row, key: string) {
  const version = actionLoads.begin()
  actionLoading.value = `${row.id}:${key}`
  try {
    await actionHandlers[key]?.(row)
  } catch (err) {
    if (err !== 'cancel' && err !== 'close') ElMessage.error(messageOf(err))
  } finally {
    if (actionLoads.isCurrent(version)) actionLoading.value = ''
  }
}

async function testCabinets() {
  const context = contextVersion
  const version = actionLoads.begin()
  actionLoading.value = 'batch-test'
  try {
    const data = await post<ConnectionProbeList>('/cabinets/test-connections', {
      ids: selected.value.map(row => row.id)
    })
    if (context !== contextVersion) return
    cabinetProbeResults.value = data.items
    detailsTitle.value = '机柜连接测试结果'
    detailsVisible.value = true
  } catch (err) {
    ElMessage.error(messageOf(err))
  } finally {
    if (actionLoads.isCurrent(version)) actionLoading.value = ''
  }
}

async function doReset(password: string) {
  const context = contextVersion
  saving.value = true
  try {
    await post(`/people/${resetPerson.value?.id}/reset-password`, { newPassword: password })
    if (context !== contextVersion) return
    ElMessage.success('密码已重置，请单独告知本人；下次登录需修改密码。')
    resetVisible.value = false
  } catch (err) {
    if (context === contextVersion) ElMessage.error(messageOf(err))
  } finally {
    if (context === contextVersion) saving.value = false
  }
}

async function saveConfiguration(payload: Row) {
  const context = contextVersion
  saving.value = true
  try {
    await put(`/nodes/${configurationNode.value?.id}/configuration`, payload)
    if (context !== contextVersion) return
    ElMessage.success('模拟节点配置已保存')
    configurationVisible.value = false
  } catch (err) {
    if (context === contextVersion) ElMessage.error(messageOf(err))
  } finally {
    if (context === contextVersion) saving.value = false
  }
}

function selectionChange(value: Row[]) {
  selected.value = value
}

function openReset(row: Row) {
  resetPerson.value = row
  resetVisible.value = true
}

async function changeRole(row: Row, role: 'Admin' | 'Teacher') {
  const context = contextVersion
  const nextLabel = role === 'Admin' ? '管理员' : '教师'
  const currentLabel = role === 'Admin' ? '教师' : '管理员'
  try {
    await ElMessageBox.confirm(
      `确认将${currentLabel}“${row.name || row.userName}”改为${nextLabel}吗？账号会转到对应的管理页面。`,
      `改为${nextLabel}`,
      { type: 'warning', confirmButtonText: '确认调整', cancelButtonText: '取消' }
    )
    if (context !== contextVersion) return
    await put(`${entity.value.endpoint}/${row.id}`, {
      kind: 'teacher',
      name: row.name,
      userName: row.userName,
      number: row.number,
      classId: row.classId ?? null,
      gender: row.gender ?? '',
      phone: row.phone ?? '',
      email: row.email ?? '',
      role
    })
    if (context !== contextVersion) return
    ElMessage.success(`已改为${nextLabel}`)
    clearSelection()
    await load()
  } catch (err) {
    if (context === contextVersion && err !== 'cancel' && err !== 'close') ElMessage.error(messageOf(err))
  }
}

function navigateTo(key: string, params?: Row) {
  emit('navigate', key, params)
}

function closeTerminal() {
  terminalDevice.value = null
  void load()
}

// The app reuses this component across entity routes, so overlays opened for the previous
// entity must be closed instead of lingering above the new list.
function resetOverlays() {
  formVisible.value = false
  saving.value = false
  editing.value = false
  initialRow.value = null
  importVisible.value = false
  terminalDevice.value = null
  webDevice.value = null
  detailsVisible.value = false
  detailsTitle.value = ''
  details.value = {}
  cabinetProbeResults.value = null
  resetVisible.value = false
  resetPerson.value = null
  configurationVisible.value = false
  configurationNode.value = null
  for (const key of Object.keys(configuration)) delete configuration[key]
  accountNode.value = null
  actionLoads.begin()
  actionLoading.value = ''
  formLoads.begin()
  configurationLoads.begin()
}

watch(
  () => [props.entityKey, props.cabinetId],
  () => {
    contextVersion++
    resetOverlays()
    rows.value = []
    total.value = 0
    error.value = ''
    cabinet.value = null
    clearSelection()
    restoreListState()
    void loadFilterOptions()
    void load()
  },
  { immediate: true }
)
</script>

<template>
  <EntityToolbar
    :entity="entity"
    :entity-key="entityKey"
    :is-people="isPeople"
    :cabinet-name="cabinet?.name"
    :cabinet-scoped="!!scopedCabinetId"
    @import="importVisible = true"
    @create="openForm()"
    @cabinets="navigateTo('cabinets')"
  />
  <EntityTable
    ref="tableRef"
    v-model:search="search"
    v-model:filters="filters"
    v-model:page="page"
    v-model:page-size="pageSize"
    :entity="entity"
    :entity-key="entityKey"
    :cabinet-id="scopedCabinetId"
    :rows="rows"
    :total="total"
    :loading="loading"
    :error="error"
    :filter-error="filterError"
    :filter-options="options"
    :loading-filters="loadingFilters"
    :is-people="isPeople"
    :selected-count="selected.length"
    :action-loading="actionLoading"
    @refresh="load"
    @reload="refresh"
    @selection-change="selectionChange"
    @batch-delete="batchDelete"
    @test-cabinets="testCabinets"
    @terminal="terminalDevice = $event"
    @web-console="webDevice = { id: $event.id, name: $event.name }"
    @action="action"
    @reset="openReset"
    @role-change="changeRole"
    @view="showDetails"
    @edit="openForm"
    @remove="remove"
    @navigate="navigateTo"
  />
  <EntityFormDialog
    v-model="formVisible"
    :entity="entity"
    :editing="editing"
    :initial-row="initialRow"
    :options="options"
    :saving="saving"
    @submit="save"
  />
  <ResetPasswordDialog v-model="resetVisible" :person="resetPerson" :busy="saving" @submit="doReset" />
  <NodeConfigurationDialog
    v-model="configurationVisible"
    :node="configurationNode"
    :value="configuration"
    :saving="saving"
    @submit="saveConfiguration"
  />
  <el-drawer v-model="detailsVisible" :title="detailsTitle" size="min(640px, 95vw)">
    <el-table v-if="cabinetProbeResults" :data="cabinetProbeResults" row-key="id" empty-text="暂无连接测试结果">
      <el-table-column prop="name" label="机柜名称" min-width="140" />
      <el-table-column label="连接结果" width="100">
        <template #default="{ row }">
          <el-tag :type="row.success ? 'success' : 'danger'">{{ row.success ? '通过' : '失败' }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="测试范围" width="115">
        <template #default="{ row }">{{ row.isSimulated ? '模拟控制器' : 'TCP 可达性' }}</template>
      </el-table-column>
      <el-table-column prop="message" label="检测说明" min-width="170" />
    </el-table>
    <DataRecord v-else :data="details" />
  </el-drawer>
  <ImportDialog v-if="isPeople" v-model="importVisible" :kind="entity.kind!" @committed="load" />
  <TerminalDialog v-if="terminalDevice" :device="terminalDevice" @close="closeTerminal" />
  <DeviceWebConsole v-if="webDevice" :device="webDevice" @close="webDevice = null" @changed="load" />
  <NodeAccountsDialog :node="accountNode" @close="accountNode = null" @changed="load" />
</template>

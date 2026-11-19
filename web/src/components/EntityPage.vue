<script setup lang="ts">
import { computed, defineAsyncComponent, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, del, listItems, messageOf, post, put, type Row } from '../api'
import type { ConnectionProbe, ConnectionProbeList, MessageResponse, Paged } from '../types'
import { entities } from '../entities'
import { recoverPage } from '../paging'
import DataRecord from './DataRecord.vue'
import EntityFormDialog from './EntityFormDialog.vue'
import EntityTable from './EntityTable.vue'
import EntityToolbar from './EntityToolbar.vue'
import NodeAccountsDialog from './NodeAccountsDialog.vue'
import NodeConfigurationDialog from './NodeConfigurationDialog.vue'
import ResetPasswordDialog from './ResetPasswordDialog.vue'
const ImportDialog = defineAsyncComponent(() => import('./ImportDialog.vue'))
const TerminalDialog = defineAsyncComponent(() => import('./TerminalDialog.vue'))

const props = defineProps<{ entityKey: string; cabinetId?: string }>()
const emit = defineEmits<{ navigate: [key: string, params?: Row] }>()

const entity = computed(() => entities[props.entityKey]!)
const rows = ref<Row[]>([])
const total = ref(0)
const search = ref('')
const page = ref(1)
const pageSize = ref(20)
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const selected = ref<Row[]>([])
const formVisible = ref(false)
const editing = ref(false)
const editingRow = ref<Row | null>(null)
const options = reactive<Record<string, Row[]>>({})
const importVisible = ref(false)
const terminalDevice = ref<Row | null>(null)
const detailsVisible = ref(false)
const detailsTitle = ref('')
const details = ref<unknown>({})
const actionLoading = ref('')
const resetVisible = ref(false)
const resetPerson = ref<Row | null>(null)
const configurationVisible = ref(false)
const configurationNode = ref<Row | null>(null)
const configuration = reactive<Row>({})
const accountNode = ref<Row | null>(null)
const isPeople = computed(() => !!entity.value.kind)
let loadVersion = 0

async function load() {
  const version = ++loadVersion
  loading.value = true
  error.value = ''
  selected.value = []
  try {
    const query = new URLSearchParams({
      search: search.value,
      page: String(page.value),
      pageSize: String(pageSize.value)
    })
    if (entity.value.kind) query.set('kind', entity.value.kind)
    if (props.entityKey === 'devices' && props.cabinetId) query.set('cabinetId', props.cabinetId)
    const data = await api<Paged<Row>>(`${entity.value.endpoint}?${query}`)
    if (version !== loadVersion) return
    rows.value = listItems(data)
    total.value = data?.total ?? rows.value.length
    const recovered = recoverPage(page.value, pageSize.value, total.value, rows.value.length)
    if (recovered !== null && recovered !== page.value) {
      page.value = recovered
      await load()
    }
  } catch (err) {
    if (version === loadVersion) {
      error.value = messageOf(err)
      rows.value = []
      total.value = 0
    }
  } finally {
    if (version === loadVersion) loading.value = false
  }
}

async function loadOptions() {
  for (const source of [...new Set(entity.value.fields.map(field => field.source).filter(Boolean))] as string[]) {
    const all: Row[] = []
    let current = 1
    for (;;) {
      const data = await api<Paged<Row>>(`${source}?page=${current}&pageSize=200`)
      const batch = listItems<Row>(data)
      all.push(...batch)
      if (!batch.length || all.length >= (data.total ?? all.length)) break
      current++
      if (current > 100) break
    }
    options[source] = all
  }
}

async function openForm(row?: Row) {
  editing.value = !!row
  editingRow.value = row || null
  try {
    await loadOptions()
    formVisible.value = true
  } catch (err) {
    ElMessage.error(messageOf(err))
  }
}

async function save(payload: Row) {
  saving.value = true
  try {
    if (editing.value) await put(`${entity.value.endpoint}/${payload.id}`, payload)
    else await post(entity.value.endpoint, payload)
    formVisible.value = false
    ElMessage.success(`${entity.value.singular}已保存`)
    await load()
  } catch (err) {
    ElMessage.error(messageOf(err))
  } finally {
    saving.value = false
  }
}

async function remove(row: Row) {
  try {
    await ElMessageBox.confirm(
      `确认删除${entity.value.singular}“${row.name || row.userName}”？关联记录存在时系统会阻止删除。`,
      '删除确认',
      { type: 'warning', confirmButtonText: '确认删除', cancelButtonText: '取消' }
    )
    await del(`${entity.value.endpoint}/${row.id}`)
    ElMessage.success('已删除')
    await load()
  } catch (err) {
    if (err !== 'cancel' && err !== 'close') ElMessage.error(messageOf(err))
  }
}

async function batchDelete() {
  try {
    await ElMessageBox.confirm(
      `确认删除选中的 ${selected.value.length} 名${entity.value.singular}及其登录账号？`,
      '批量删除',
      { type: 'warning', confirmButtonText: '确认删除', cancelButtonText: '取消' }
    )
    await post('/people/batch-delete', { ids: selected.value.map(row => row.id) })
    ElMessage.success('批量删除完成')
    await load()
  } catch (err) {
    if (err !== 'cancel' && err !== 'close') ElMessage.error(messageOf(err))
  }
}

function showDetails(data: unknown, title?: string) {
  details.value = data
  detailsTitle.value = title || `${entity.value.singular}详情`
  detailsVisible.value = true
}

async function testDevice(row: Row) {
  const data = await post<ConnectionProbe>(`/devices/${row.id}/connect-test`)
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
  showDetails(await api<Row>(`/nodes/${row.id}/status`), `${row.name} · 节点状态`)
}

async function toggleNode(row: Row) {
  if (row.enabled) {
    await ElMessageBox.confirm('停用后暂停该节点的新资源分配，已有运行中的模拟虚拟机保持当前状态。', '停用节点', {
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
  Object.assign(configuration, await api<Row>(`/nodes/${row.id}/configuration`))
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
  actionLoading.value = `${row.id}:${key}`
  try {
    await actionHandlers[key]?.(row)
  } catch (err) {
    if (err !== 'cancel' && err !== 'close') ElMessage.error(messageOf(err))
  } finally {
    actionLoading.value = ''
  }
}

async function testCabinets() {
  actionLoading.value = 'batch-test'
  try {
    const data = await post<ConnectionProbeList>('/cabinets/test-connections', {
      ids: selected.value.map(row => row.id)
    })
    showDetails(data, '机柜连接测试结果')
  } catch (err) {
    ElMessage.error(messageOf(err))
  } finally {
    actionLoading.value = ''
  }
}

async function doReset(password: string) {
  saving.value = true
  try {
    await post(`/people/${resetPerson.value?.id}/reset-password`, { newPassword: password })
    ElMessage.success('密码已重置，请单独告知本人；下次登录需修改密码。')
    resetVisible.value = false
  } catch (err) {
    ElMessage.error(messageOf(err))
  } finally {
    saving.value = false
  }
}

async function saveConfiguration(payload: Row) {
  saving.value = true
  try {
    await put(`/nodes/${configurationNode.value?.id}/configuration`, payload)
    ElMessage.success('模拟节点配置已保存')
    configurationVisible.value = false
  } catch (err) {
    ElMessage.error(messageOf(err))
  } finally {
    saving.value = false
  }
}

function selectionChange(value: Row[]) {
  selected.value = value
}

function openReset(row: Row) {
  resetPerson.value = row
  resetVisible.value = true
}

function navigateTo(key: string, params?: Row) {
  emit('navigate', key, params)
}

function closeTerminal() {
  terminalDevice.value = null
  void load()
}

watch(
  () => [props.entityKey, props.cabinetId],
  () => {
    page.value = 1
    search.value = ''
    error.value = ''
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
    @import="importVisible = true"
    @create="openForm()"
  />
  <EntityTable
    v-model:search="search"
    v-model:page="page"
    v-model:page-size="pageSize"
    :entity="entity"
    :entity-key="entityKey"
    :rows="rows"
    :total="total"
    :loading="loading"
    :error="error"
    :is-people="isPeople"
    :selected-count="selected.length"
    :action-loading="actionLoading"
    @refresh="load"
    @selection-change="selectionChange"
    @batch-delete="batchDelete"
    @test-cabinets="testCabinets"
    @terminal="terminalDevice = $event"
    @action="action"
    @reset="openReset"
    @view="showDetails"
    @edit="openForm"
    @remove="remove"
    @navigate="navigateTo"
  />
  <EntityFormDialog
    v-model="formVisible"
    :entity="entity"
    :editing="editing"
    :initial-row="editingRow"
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
    <DataRecord :data="details" />
  </el-drawer>
  <ImportDialog v-if="isPeople" v-model="importVisible" :kind="entity.kind!" @committed="load" />
  <TerminalDialog v-if="terminalDevice" :device="terminalDevice" @close="closeTerminal" />
  <NodeAccountsDialog :node="accountNode" @close="accountNode = null" @changed="load" />
</template>

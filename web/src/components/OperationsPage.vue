<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  Refresh,
  Download,
  Plus,
  Upload,
  Search,
  VideoPlay,
  VideoPause,
  Monitor,
  Document,
  FolderOpened,
  Check,
  Delete,
  EditPen
} from '@element-plus/icons-vue'
import { api, post, put, listItems, messageOf, dateTime, bytes, type Row } from '../api'
import { readListState, writeListState } from '../listState'
import { focusFirstField } from '../focusField'
import { downloadFile } from '../exam'
import type { BackupItem, LicenseUploadResult, NodeRow, Paged } from '../types'
import DataRecord from './DataRecord.vue'
import EntityRowMenu, { type EntityRowAction } from './EntityRowMenu.vue'
import ServerOverview from './ServerOverview.vue'
import VmConsoleDialog from './VmConsoleDialog.vue'
const props = defineProps<{ pageKey: string; productVersion: string }>()
const data = ref<Row | null>(null),
  rows = ref<Row[]>([]),
  loading = ref(false),
  saving = ref(false),
  error = ref(''),
  search = ref(''),
  page = ref(1),
  total = ref(0)
const settings = reactive<Row>({ schoolName: '' }),
  detail = ref<Row | null>(null),
  licenseInput = ref<HTMLInputElement>(),
  uploadResults = ref<Row[]>([])
const vmNameInput = ref<{ focus?: () => void; $el?: HTMLElement }>()
const vmVisible = ref(false),
  vmEditing = ref(false),
  vmLoading = ref(false),
  vmNodes = ref<NodeRow[]>([]),
  vmModel = reactive<Row>({ name: '', nodeId: '', cpuCount: 2, memoryMb: 2048, diskGb: 20, remark: '' })
const vmProvider = ref<'simulated' | 'hyperv'>('simulated')
const vmConsoleVisible = ref(false)
const consoleMachine = ref<Row | null>(null)
const powerActionNames = { start: '启动', stop: '停止', restart: '重启' }
const detectionLabels: Record<string, string> = { reachable: '可连接', offline: '离线', unsupported: '未接入' }
const vmViewport = window.matchMedia('(max-width: 1020px)')
const compactVm = ref(vmViewport.matches)
function syncVmViewport(event: MediaQueryListEvent) {
  compactVm.value = event.matches
}
onMounted(() => vmViewport.addEventListener('change', syncVmViewport))
onBeforeUnmount(() => {
  saveOpsState()
  vmViewport.removeEventListener('change', syncVmViewport)
})
let loadVersion = 0,
  pageVersion = 0
function resetSearch() {
  page.value = 1
  void load()
}
const searching = computed(() => !!search.value.trim())
function clearSearch() {
  search.value = ''
  resetSearch()
}
const titles: Record<string, { title: string; subtitle: string; section: string }> = {
  server: { title: '服务器信息', subtitle: '查看当前管理服务器的真实硬件与网络信息。', section: '运行维护 / 主机状态' },
  'virtual-machines': {
    title: '虚拟机管理',
    subtitle: '查找实训虚拟机，连接控制台或控制电源。',
    section: '实训资源 / 虚拟资源'
  },
  audits: { title: '操作日志', subtitle: '追溯账号、资源与系统配置的关键操作。', section: '运行维护 / 审计记录' },
  backups: {
    title: '备份与恢复',
    subtitle: '创建一致性备份，将业务数据和必要密钥一同保留。',
    section: '运行维护 / 数据保护'
  },
  licenses: {
    title: '许可证管理',
    subtitle: '查看设备特征码、授权资源和许可证校验结果。',
    section: '运行维护 / 产品授权'
  },
  settings: { title: '系统设置', subtitle: '维护学校名称与当前部署信息。', section: '运行维护 / 系统配置' }
}
const title = computed(() => titles[props.pageKey]!)
const licenseMetadata = computed(() =>
  data.value ? { machineCode: data.value.machineCode, items: data.value.items } : null
)
async function load() {
  const version = ++loadVersion
  loading.value = true
  error.value = ''
  data.value = null
  rows.value = []
  try {
    const path =
      props.pageKey === 'server'
        ? '/server/info'
        : ['audits', 'virtual-machines'].includes(props.pageKey)
          ? `${props.pageKey === 'virtual-machines' && vmProvider.value === 'hyperv' ? '/hyperv' : ''}/${props.pageKey}?search=${encodeURIComponent(search.value)}&page=${page.value}&pageSize=20`
          : `/${props.pageKey}`
    const result = await api<Row>(path)
    if (version !== loadVersion) return
    data.value = result
    if (['virtual-machines', 'audits', 'backups'].includes(props.pageKey)) {
      rows.value = listItems(result)
      total.value = result?.total ?? rows.value.length
    }
    if (props.pageKey === 'settings') Object.assign(settings, result)
  } catch (err) {
    if (version === loadVersion) error.value = messageOf(err)
  } finally {
    if (version === loadVersion) loading.value = false
  }
}
async function createBackup() {
  saving.value = true
  try {
    const result = await post<BackupItem>('/backups')
    ElMessage.success(`备份已创建：${result.fileName || ''}`)
    await load()
  } catch (err) {
    ElMessage.error(messageOf(err))
  } finally {
    saving.value = false
  }
}
async function saveSettings() {
  if (!String(settings.schoolName || '').trim()) {
    ElMessage.warning('请填写学校名称')
    return
  }
  saving.value = true
  try {
    await put('/settings', { schoolName: settings.schoolName })
    settings.schoolName = String(settings.schoolName).trim()
    ElMessage.success('系统设置已保存')
    window.dispatchEvent(new CustomEvent('school-name-changed', { detail: settings.schoolName }))
    await load()
  } catch (err) {
    ElMessage.error(messageOf(err))
  } finally {
    saving.value = false
  }
}
function vmActionPath(row: Row) {
  if (row.provider === 'hyperv') return `/hyperv/nodes/${row.nodeId}/virtual-machines/${row.id}`
  return `/virtual-machines/${row.id}`
}

async function openConsole(row: Row) {
  if (row.provider === 'hyperv') {
    try {
      await downloadFile(`${vmActionPath(row)}/connection`, `SchoolTraining-${row.id}.rdp`)
      ElMessage.info('请打开下载的远程桌面文件，使用学校分配的来宾系统账号登录。')
    } catch (failure) {
      ElMessage.error(messageOf(failure))
    }
    return
  }
  consoleMachine.value = row
  vmConsoleVisible.value = true
}

async function vmAction(row: Row, action: keyof typeof powerActionNames) {
  const label = powerActionNames[action]
  try {
    await ElMessageBox.confirm(
      `确认${label}${row.isSimulated ? '模拟' : ' Hyper-V '}虚拟机“${row.name}”？`,
      '确认资源操作',
      {
        type: action === 'start' ? 'info' : 'warning',
        confirmButtonText: label,
        cancelButtonText: '取消'
      }
    )
    saving.value = true
    await post(`${vmActionPath(row)}/${action}`)
    ElMessage.success(`虚拟机已${label}`)
    await load()
  } catch (err) {
    if (err !== 'cancel' && err !== 'close') ElMessage.error(messageOf(err))
  } finally {
    saving.value = false
  }
}

function vmConnectReason(row: Row) {
  if (row.detectionStatus === 'reachable') return ''
  if (row.detectionStatus === 'offline') return '节点离线，无法连接控制台'
  if (row.detectionStatus === 'unsupported') return '节点未接入，无法连接控制台'
  return '当前无法连接控制台'
}

function vmStartReason(row: Row) {
  if (!row.nodeEnabled) return '节点已停用，不能启动'
  if (row.status === 'stopped') return ''
  return row.status === 'running' ? '虚拟机运行中，无需启动' : '仅已停止的虚拟机可以启动'
}

function vmRestartReason(row: Row) {
  if (!row.nodeEnabled) return '节点已停用，不能重启'
  return row.status === 'running' ? '' : '仅运行中的虚拟机可以重启'
}

function vmEditReason(row: Row) {
  if (!row.isSimulated) return '只有内置模拟虚拟机可以在此编辑'
  if (vmLoading.value) return '正在加载配置，请稍候'
  return row.status === 'running' ? '请先停止虚拟机再编辑' : ''
}

function vmActionReason(row: Row, action: 'connect' | 'start' | 'stop' | 'restart' | 'edit' | 'destroy') {
  if (action === 'connect') return vmConnectReason(row)
  const verbs = { start: '启动', stop: '停止', restart: '重启', edit: '编辑', destroy: '销毁' } as const
  if (row.isReserved) return `该虚拟机正被实训占用，不能${verbs[action]}`
  if (saving.value && action !== 'edit') return '正在处理其他操作，请稍候'
  if (action === 'start') return vmStartReason(row)
  if (action === 'stop') return row.status === 'running' ? '' : '仅运行中的虚拟机可以停止'
  if (action === 'restart') return vmRestartReason(row)
  if (action === 'edit') return vmEditReason(row)
  if (action === 'destroy' && !row.isSimulated && row.status !== 'stopped') return '请先停止该虚拟机再销毁'
  return ''
}

function vmActions(row: Row): EntityRowAction[] {
  return [
    {
      label: '连接',
      icon: Monitor,
      disabled: row.detectionStatus !== 'reachable',
      reason: vmActionReason(row, 'connect'),
      run: () => void openConsole(row)
    },
    {
      label: '启动',
      icon: VideoPlay,
      disabled: row.status !== 'stopped' || saving.value || !row.nodeEnabled || row.isReserved,
      reason: vmActionReason(row, 'start'),
      run: () => void vmAction(row, 'start')
    },
    {
      label: '停止',
      icon: VideoPause,
      type: 'warning',
      disabled: row.status !== 'running' || saving.value || row.isReserved,
      reason: vmActionReason(row, 'stop'),
      run: () => void vmAction(row, 'stop')
    },
    {
      label: '重启',
      icon: Refresh,
      disabled: row.status !== 'running' || !row.nodeEnabled || row.isReserved || saving.value,
      reason: vmActionReason(row, 'restart'),
      run: () => void vmAction(row, 'restart')
    },
    {
      label: '编辑',
      icon: EditPen,
      disabled: row.status === 'running' || vmLoading.value || row.isReserved || !row.isSimulated,
      reason: vmActionReason(row, 'edit'),
      run: () => void openVm(row)
    },
    {
      label: '销毁',
      icon: Delete,
      type: 'danger',
      disabled: row.isReserved || (!row.isSimulated && row.status !== 'stopped') || saving.value,
      reason: vmActionReason(row, 'destroy'),
      run: () => void deleteVm(row)
    }
  ]
}

function vmButtonType(action: EntityRowAction) {
  return action.type === 'danger' || action.type === 'warning' ? action.type : 'primary'
}
function focusVmName() {
  const root = vmNameInput.value?.$el
  if (root) focusFirstField(root)
  else vmNameInput.value?.focus?.()
}
async function openVm(row?: Row) {
  if (vmLoading.value) return
  const version = pageVersion
  vmLoading.value = true
  try {
    const nodes: NodeRow[] = []
    const pageSize = 500
    for (let nodePage = 1; ; nodePage++) {
      const result = await api<Paged<NodeRow>>(`/nodes?provider=simulated&page=${nodePage}&pageSize=${pageSize}`)
      const batch = listItems<NodeRow>(result)
      nodes.push(...batch)
      if (!batch.length || nodePage * pageSize >= result.total) break
    }
    if (version !== pageVersion || props.pageKey !== 'virtual-machines') return
    if (!nodes.length) {
      ElMessage.warning('请先创建一个模拟节点，或在控制台创建模拟环境')
      return
    }
    vmNodes.value = nodes
    Object.keys(vmModel).forEach(key => delete vmModel[key])
    Object.assign(
      vmModel,
      { name: '', nodeId: nodes[0]?.id, cpuCount: 2, memoryMb: 2048, diskGb: 20, remark: '' },
      row || {}
    )
    vmEditing.value = !!row
    vmVisible.value = true
  } catch (err) {
    if (version === pageVersion) ElMessage.error(messageOf(err))
  } finally {
    vmLoading.value = false
  }
}
async function saveVm() {
  if (!String(vmModel.name || '').trim() || !vmModel.nodeId) {
    ElMessage.warning('请填写名称并选择所属节点')
    return
  }
  saving.value = true
  try {
    if (vmEditing.value) await put(`/virtual-machines/${vmModel.id}`, vmModel)
    else await post('/virtual-machines', vmModel)
    ElMessage.success('模拟虚拟机已保存')
    vmVisible.value = false
    await load()
  } catch (err) {
    ElMessage.error(messageOf(err))
  } finally {
    saving.value = false
  }
}
async function deleteVm(row: Row) {
  try {
    const message = row.isSimulated
      ? `销毁会停止并移除模拟虚拟机“${row.name}”。确认销毁？`
      : `将从 Hyper-V 移除已关机的虚拟机“${row.name}”。磁盘文件将保留。`
    await ElMessageBox.confirm(message, '销毁虚拟机', {
      type: 'warning',
      confirmButtonText: '确认销毁',
      cancelButtonText: '取消'
    })
    saving.value = true
    await post(`${vmActionPath(row)}/destroy`)
    ElMessage.success('虚拟机已移除')
    if (rows.value.length === 1 && page.value > 1) page.value--
    await load()
  } catch (err) {
    if (err !== 'cancel' && err !== 'close') ElMessage.error(messageOf(err))
  } finally {
    saving.value = false
  }
}
async function uploadLicense(event: Event) {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files || [])
  input.value = ''
  if (!files.length) return
  uploadResults.value = []
  if (files.length > 20 || files.some(file => file.size < 1 || file.size > 262144)) {
    ElMessage.warning('每次请选择 1–20 个非空许可证文件，每个文件最多 256 KB。')
    return
  }
  saving.value = true
  try {
    const body = new FormData()
    for (const file of files) body.append('file', file)
    const result = await api<LicenseUploadResult>('/licenses', { method: 'POST', body })
    uploadResults.value = files.map(file => ({
      fileName: file.name,
      success: true,
      message: `已与本批其他文件一起导入，共 ${result.importedCount} 项。`
    }))
    await load()
  } catch (err) {
    uploadResults.value = files.map(file => ({
      fileName: file.name,
      success: false,
      message: `本批未导入：${messageOf(err)}`
    }))
  } finally {
    saving.value = false
  }
}
function copyMachineCode() {
  const value = data.value?.machineCode
  if (!value) {
    ElMessage.warning('暂未获取设备特征码')
    return
  }
  if (!navigator.clipboard) {
    ElMessage.warning('浏览器未允许复制，请手动选择设备特征码')
    return
  }
  navigator.clipboard
    .writeText(value)
    .then(() => ElMessage.success('设备特征码已复制'))
    .catch(() => ElMessage.warning('浏览器未允许复制，请手动选择设备特征码'))
}
function opsStateKey() {
  return `ops:${props.pageKey}`
}

function saveOpsState() {
  writeListState(opsStateKey(), { search: search.value, filters: {}, page: page.value, pageSize: 20 })
}

watch(
  () => props.pageKey,
  () => {
    pageVersion++
    const saved = readListState(opsStateKey())
    search.value = saved.search
    page.value = saved.page
    uploadResults.value = []
    vmVisible.value = false
    vmConsoleVisible.value = false
    consoleMachine.value = null
    vmEditing.value = false
    vmNodes.value = []
    detail.value = null
    void load()
  },
  { immediate: true }
)
// Declared after the page switch so restoring the incoming page's state never
// overwrites it with the previous page's filters.
watch([search, page], saveOpsState)
</script>
<template>
  <section class="page-heading">
    <div>
      <h1>{{ title.title }}</h1>
      <p>{{ title.subtitle }}</p>
    </div>
    <div class="heading-actions">
      <el-button
        v-if="pageKey === 'virtual-machines'"
        type="primary"
        :icon="Plus"
        :loading="vmLoading"
        @click="openVm()"
        >新增模拟虚拟机</el-button
      ><el-button v-if="pageKey === 'backups'" type="primary" :icon="Plus" :loading="saving" @click="createBackup"
        >创建备份</el-button
      ><template v-if="pageKey === 'licenses'"
        ><el-button tag="a" href="/api/licenses/guide" :icon="Document">使用说明</el-button
        ><el-button type="primary" :icon="Upload" :loading="saving" @click="licenseInput?.click()">上传许可证</el-button
        ><input
          ref="licenseInput"
          type="file"
          accept=".json,.license,.lic"
          multiple
          class="visually-hidden"
          aria-label="选择许可证文件"
          @change="uploadLicense" /></template
      ><el-button :icon="Refresh" @click="load">刷新</el-button>
    </div>
  </section>
  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
  <template v-if="pageKey === 'virtual-machines'">
    <el-radio-group v-model="vmProvider" class="section-notice" @change="resetSearch" aria-label="虚拟化平台">
      <el-radio-button value="simulated">内置模拟节点</el-radio-button>
      <el-radio-button value="hyperv">Hyper-V</el-radio-button>
    </el-radio-group>
    <el-alert
      v-if="vmProvider === 'hyperv' && data?.unavailableNodes?.length"
      type="warning"
      show-icon
      :closable="false"
      class="section-notice"
      :title="`无法连接节点 ${data.unavailableNodes.join('、')}`" />
    <section class="surface" v-loading="loading">
      <div class="table-toolbar">
        <div class="search-box">
          <el-input
            v-model="search"
            clearable
            :prefix-icon="Search"
            placeholder="搜索名称、UUID 或 IP"
            @keyup.enter="resetSearch"
            @clear="resetSearch"
          /><el-button @click="resetSearch">搜索</el-button
          ><el-button v-if="searching" @click="clearSearch">清除</el-button>
        </div>
        <span class="record-count">{{ total }} 台虚拟机</span>
      </div>
      <el-table
        :data="rows"
        :empty-text="
          searching
            ? '没有符合条件的虚拟机，试试清除搜索或更换平台。'
            : '暂无虚拟机。请先接入节点或在控制台创建模拟环境。'
        "
        scrollbar-always-on
        ><el-table-column prop="name" label="虚拟机名称" min-width="180"
          ><template #default="{ row }"
            ><span class="table-name">{{ row.name }}</span>
            <el-tag v-if="row.isReserved" type="warning" size="small">实训占用</el-tag></template
          ></el-table-column
        ><el-table-column prop="uuid" label="UUID" min-width="245" show-overflow-tooltip />
        <el-table-column prop="ipAddress" label="IP 地址" min-width="135" />
        <el-table-column label="检测状态" width="106">
          <template #default="{ row }">
            <el-tag :type="row.detectionStatus === 'reachable' ? 'success' : 'info'" effect="plain">
              {{ detectionLabels[row.detectionStatus] || '未知' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="nodeName" label="所属节点" min-width="150" show-overflow-tooltip /><el-table-column
          label="运行状态"
          width="130"
          ><template #default="{ row }"
            ><el-tag :type="row.status === 'running' ? 'success' : 'info'">{{
              row.status === 'running' ? '运行中' : row.status === 'stopped' ? '已停止' : row.status
            }}</el-tag></template
          ></el-table-column
        ><el-table-column prop="cpuCount" label="CPU 核心" width="110" /><el-table-column label="内存" width="120"
          ><template #default="{ row }">{{ row.memoryMb }} MB</template></el-table-column
        ><el-table-column label="磁盘" width="100"
          ><template #default="{ row }">{{ row.diskGb == null ? '—' : `${row.diskGb} GB` }}</template></el-table-column
        ><el-table-column
          label="操作"
          :width="compactVm ? 86 : 350"
          :align="compactVm ? 'center' : 'left'"
          fixed="right"
          ><template #default="{ row }"
            ><EntityRowMenu v-if="compactVm" :row-name="row.name" :actions="vmActions(row)" />
            <template v-else>
              <el-tooltip
                v-for="action in vmActions(row)"
                :key="action.label"
                :disabled="!(action.disabled && action.reason)"
                :content="action.reason"
                placement="top"
              >
                <span class="disabled-action-wrap">
                  <el-button
                    link
                    :type="vmButtonType(action)"
                    :icon="action.icon"
                    :disabled="action.disabled || action.busy"
                    @click="action.run"
                    >{{ action.label }}</el-button
                  >
                </span>
              </el-tooltip>
            </template></template
          ></el-table-column
        ></el-table
      >
      <p v-if="compactVm && rows.length" class="table-scroll-hint">横向滚动可查看更多列</p>
      <div class="table-footer">
        <span>实训占用的虚拟机可从对应的监控页面重启。</span
        ><el-pagination
          v-model:current-page="page"
          :page-size="20"
          :total="total"
          layout="prev,pager,next"
          @change="load"
        />
      </div></section
  ></template>
  <section v-else-if="pageKey === 'audits'" class="surface">
    <div class="table-toolbar">
      <div class="search-box">
        <el-input
          v-model="search"
          clearable
          :prefix-icon="Search"
          placeholder="搜索操作、目标或账号"
          @keyup.enter="resetSearch"
          @clear="resetSearch"
        /><el-button @click="resetSearch">搜索</el-button
        ><el-button v-if="searching" @click="clearSearch">清除</el-button>
      </div>
      <span class="record-count">共 {{ total }} 条记录</span>
    </div>
    <el-table
      :data="rows"
      v-loading="loading"
      :empty-text="searching ? '没有符合条件的操作记录，试试调整关键词或清除搜索。' : '暂无操作记录'"
      ><el-table-column label="操作时间（北京时间）" width="220"
        ><template #default="{ row }">{{ dateTime(row.at) }}</template></el-table-column
      ><el-table-column prop="userName" label="操作账号" width="130" /><el-table-column
        prop="action"
        label="操作"
        min-width="160" /><el-table-column
        prop="target"
        label="目标"
        min-width="170"
        show-overflow-tooltip /><el-table-column prop="detail" label="详情" min-width="240" show-overflow-tooltip
    /></el-table>
    <div class="table-footer">
      <span>操作日志由系统自动记录</span
      ><el-pagination
        v-model:current-page="page"
        :page-size="20"
        :total="total"
        layout="prev,pager,next"
        @change="load"
      />
    </div>
  </section>
  <template v-else-if="pageKey === 'backups'"
    ><div class="info-band">
      <div class="info-band-icon">
        <el-icon :size="25"><FolderOpened /></el-icon>
      </div>
      <div>
        <strong>备份文件包含业务数据与必要密钥</strong>
        <p>请妥善保存下载文件。恢复时使用随安装包提供的恢复工具，按说明停止服务并验证恢复结果。</p>
      </div>
    </div>
    <section class="surface" v-loading="loading">
      <div class="table-toolbar">
        <strong>备份记录</strong><span class="record-count">{{ rows.length }} 个文件</span>
      </div>
      <el-table :data="rows" empty-text="暂无备份，点击“创建备份”生成首个备份文件"
        ><el-table-column prop="fileName" label="备份文件" min-width="260" /><el-table-column
          label="创建时间（北京时间）"
          min-width="220"
          ><template #default="{ row }">{{ dateTime(row.createdAt) }}</template></el-table-column
        ><el-table-column label="文件大小" width="140"
          ><template #default="{ row }">{{ bytes(row.size) }}</template></el-table-column
        ><el-table-column label="操作" width="120"
          ><template #default="{ row }"
            ><el-button
              link
              type="primary"
              :icon="Download"
              tag="a"
              :href="`/api/backups/${encodeURIComponent(row.fileName)}`"
              >下载</el-button
            ></template
          ></el-table-column
        ></el-table
      >
    </section></template
  >
  <template v-else-if="pageKey === 'licenses'"
    ><el-alert
      v-if="data && !data.publicKeyConfigured"
      title="验签公钥缺失或不可用。请联系交付人员检查，以恢复已启用的许可证额度。"
      type="info"
      show-icon
      :closable="false"
      class="section-notice" />
    <section class="surface license-surface" v-loading="loading">
      <div class="card-heading">
        <div>
          <h2>产品授权</h2>
          <p>许可证经签名验证后生效，设备特征码用于识别当前部署。</p>
        </div>
        <el-button v-if="data?.machineCode" @click="copyMachineCode">复制设备特征码</el-button>
      </div>
      <DataRecord v-if="licenseMetadata" :data="licenseMetadata" />
      <el-empty v-else-if="!loading" description="暂未获取授权信息，可点击右上角上传许可证。" />
    </section>
    <section v-if="data?.quotas" class="surface section-gap">
      <div class="card-heading">
        <div>
          <h2>资源使用与许可额度</h2>
          <p>为资源导入授权后，新增和分配操作受该额度约束。</p>
        </div>
      </div>
      <el-table :data="data.quotas">
        <el-table-column prop="name" label="资源" min-width="130" />
        <el-table-column prop="code" label="资源授权码" min-width="200" />
        <el-table-column prop="used" label="已用" width="90" />
        <el-table-column label="许可额度" min-width="140">
          <template #default="{ row }">{{ row.limit === null ? '未启用限制' : row.limit }}</template>
        </el-table-column>
      </el-table>
    </section>
    <section v-if="uploadResults.length" class="surface section-gap">
      <div class="card-heading"><h2>上传结果</h2></div>
      <el-table :data="uploadResults"
        ><el-table-column prop="fileName" label="文件" min-width="200" /><el-table-column label="结果" width="110"
          ><template #default="{ row }"
            ><el-tag :type="row.success ? 'success' : 'danger'">{{ row.success ? '成功' : '未通过' }}</el-tag></template
          ></el-table-column
        ><el-table-column prop="message" label="说明" min-width="300"
      /></el-table></section
  ></template>
  <section v-else-if="pageKey === 'settings'" class="surface settings-surface" v-loading="loading">
    <div class="card-heading">
      <div>
        <h2>学校与部署</h2>
        <p>修改名称将更新平台中显示的学校信息。</p>
      </div>
    </div>
    <el-form scroll-to-error label-position="top" class="settings-form" @submit.prevent="saveSettings"
      ><el-form-item label="学校名称" required
        ><el-input v-model="settings.schoolName" maxlength="100" placeholder="请输入学校名称" /></el-form-item
      ><el-form-item v-if="settings.dataDirectory" label="数据目录"
        ><el-input :model-value="settings.dataDirectory" readonly /></el-form-item
      ><el-form-item v-if="settings.backupDirectory" label="备份目录"
        ><el-input :model-value="settings.backupDirectory" readonly /></el-form-item
      ><el-form-item v-if="productVersion" label="程序版本"
        ><el-input :model-value="productVersion" readonly /></el-form-item
      ><el-button type="primary" :icon="Check" :loading="saving" native-type="submit">保存设置</el-button></el-form
    >
  </section>
  <div v-else-if="pageKey === 'server'" v-loading="loading" class="server-page">
    <ServerOverview :data="data" :loading="loading" />
  </div>
  <section v-else class="surface host-surface" v-loading="loading">
    <div class="card-heading">
      <div>
        <h2>管理服务器</h2>
        <p>以下数据来自当前运行主机。采集不到的字段会标记为不可获取。</p>
      </div>
      <el-tag type="success" effect="light" v-if="data">实时采集</el-tag>
    </div>
    <DataRecord v-if="data" :data="data" unavailable-text="不可获取" /><el-empty
      v-else-if="!loading"
      description="暂未获取服务器信息，请稍后刷新重试。"
    />
  </section>
  <VmConsoleDialog
    v-if="consoleMachine"
    v-model="vmConsoleVisible"
    scope="machine"
    :target-id="consoleMachine.id"
    :machine-name="consoleMachine.name"
  />
  <el-dialog
    v-model="vmVisible"
    :title="vmEditing ? '编辑模拟虚拟机' : '新增模拟虚拟机'"
    width="600px"
    :close-on-click-modal="false"
    @opened="focusVmName"
    ><el-alert
      title="此处配置的是内置模拟资源，不创建或运行实际操作系统。"
      type="info"
      :closable="false"
      show-icon
      class="section-notice"
    /><el-form scroll-to-error label-position="top" class="entity-form"
      ><el-form-item label="虚拟机名称" required
        ><el-input ref="vmNameInput" v-model="vmModel.name" maxlength="100" /></el-form-item
      ><el-form-item label="所属模拟节点" required
        ><el-select v-model="vmModel.nodeId" filterable placeholder="搜索或选择模拟节点"
          ><el-option
            v-for="node in vmNodes"
            :key="node.id"
            :label="node.name"
            :value="node.id" /></el-select></el-form-item
      ><el-form-item label="CPU 核心数"
        ><el-input-number v-model="vmModel.cpuCount" :min="1" :max="4096" controls-position="right" /></el-form-item
      ><el-form-item label="内存 (MB)"
        ><el-input-number
          v-model="vmModel.memoryMb"
          :min="128"
          :max="16777216"
          controls-position="right" /></el-form-item
      ><el-form-item label="磁盘 (GB)"
        ><el-input-number v-model="vmModel.diskGb" :min="1" :max="1048576" controls-position="right" /></el-form-item
      ><el-form-item label="备注" class="full-field"
        ><el-input v-model="vmModel.remark" type="textarea" :rows="3" maxlength="1000" /></el-form-item></el-form
    ><template #footer
      ><el-button @click="vmVisible = false">取消</el-button
      ><el-button type="primary" :loading="saving" @click="saveVm">保存模拟虚拟机</el-button></template
    ></el-dialog
  ><el-drawer :model-value="!!detail" title="资源详情" @close="detail = null"><DataRecord :data="detail" /></el-drawer>
</template>

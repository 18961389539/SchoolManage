<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  Refresh,
  Download,
  Plus,
  Upload,
  Search,
  VideoPlay,
  VideoPause,
  Document,
  FolderOpened,
  Check
} from '@element-plus/icons-vue'
import { api, post, put, del, listItems, messageOf, dateTime, bytes, type Row } from '../api'
import type { BackupItem, LicenseUploadResult, NodeRow, Paged } from '../types'
import DataRecord from './DataRecord.vue'
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
const vmVisible = ref(false),
  vmEditing = ref(false),
  vmNodes = ref<NodeRow[]>([]),
  vmModel = reactive<Row>({ name: '', nodeId: '', cpuCount: 2, memoryMb: 2048, diskGb: 20, remark: '' })
let loadVersion = 0
function resetSearch() {
  page.value = 1
  void load()
}
const titles: Record<string, { title: string; subtitle: string; section: string }> = {
  server: { title: '服务器信息', subtitle: '查看当前管理服务器的真实硬件与网络信息。', section: '运行维护 / 主机状态' },
  'virtual-machines': {
    title: '虚拟机管理',
    subtitle: '查看已接入节点的虚拟机，执行资源启停操作。',
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
          ? `/${props.pageKey}?search=${encodeURIComponent(search.value)}&page=${page.value}&pageSize=20`
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
    ElMessage.success('系统设置已保存')
    window.dispatchEvent(new CustomEvent('school-name-changed', { detail: settings.schoolName }))
    await load()
  } catch (err) {
    ElMessage.error(messageOf(err))
  } finally {
    saving.value = false
  }
}
async function vmAction(row: Row, action: string) {
  try {
    await ElMessageBox.confirm(
      `${action === 'start' ? '启动' : '停止'}虚拟机“${row.name}”？${row.isSimulated ? '本次操作只改变内置模拟资源状态。' : ''}`,
      '确认资源操作',
      {
        type: action === 'stop' ? 'warning' : 'info',
        confirmButtonText: action === 'start' ? '启动' : '停止',
        cancelButtonText: '取消'
      }
    )
    saving.value = true
    await post(`/virtual-machines/${row.id}/${action}`)
    ElMessage.success('资源状态已更新')
    await load()
  } catch (err) {
    if (err !== 'cancel' && err !== 'close') ElMessage.error(messageOf(err))
  } finally {
    saving.value = false
  }
}
async function openVm(row?: Row) {
  try {
    const nodes = await api<Paged<NodeRow>>('/nodes?pageSize=500')
    vmNodes.value = listItems<NodeRow>(nodes).filter(node => node.provider === 'simulated')
    if (!vmNodes.value.length) {
      ElMessage.warning('请先创建一个模拟节点，或在控制台创建模拟环境')
      return
    }
    Object.keys(vmModel).forEach(key => delete vmModel[key])
    Object.assign(
      vmModel,
      { name: '', nodeId: vmNodes.value[0]?.id, cpuCount: 2, memoryMb: 2048, diskGb: 20, remark: '' },
      row || {}
    )
    vmEditing.value = !!row
    vmVisible.value = true
  } catch (err) {
    ElMessage.error(messageOf(err))
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
    await ElMessageBox.confirm(`确认删除模拟虚拟机“${row.name}”？请先停止运行中的资源。`, '删除模拟虚拟机', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消'
    })
    await del(`/virtual-machines/${row.id}`)
    ElMessage.success('模拟虚拟机已删除')
    await load()
  } catch (err) {
    if (err !== 'cancel' && err !== 'close') ElMessage.error(messageOf(err))
  }
}
async function uploadLicense(event: Event) {
  const files = Array.from((event.target as HTMLInputElement).files || [])
  if (!files.length) return
  uploadResults.value = []
  saving.value = true
  for (const file of files) {
    if (file.size > 262144) {
      uploadResults.value.push({ fileName: file.name, success: false, message: '文件大小不能超过 256 KB' })
      continue
    }
    const body = new FormData()
    body.append('file', file)
    try {
      const result = await api<LicenseUploadResult>('/licenses', { method: 'POST', body })
      uploadResults.value.push({
        fileName: file.name,
        success: true,
        message: `已导入 ${result.importedCount} 个许可证`
      })
    } catch (err) {
      uploadResults.value.push({ fileName: file.name, success: false, message: messageOf(err) })
    }
  }
  saving.value = false
  if (licenseInput.value) licenseInput.value.value = ''
  await load()
}
function copyMachineCode() {
  const value = data.value?.machineCode
  if (!value) {
    ElMessage.warning('暂未获取设备特征码')
    return
  }
  navigator.clipboard
    .writeText(value)
    .then(() => ElMessage.success('设备特征码已复制'))
    .catch(() => ElMessage.warning('浏览器未允许复制，请手动选择设备特征码'))
}
watch(
  () => props.pageKey,
  () => {
    search.value = ''
    page.value = 1
    uploadResults.value = []
    void load()
  },
  { immediate: true }
)
</script>
<template>
  <section class="page-heading">
    <div>
      <div class="eyebrow">{{ title.section }}</div>
      <h1>{{ title.title }}</h1>
      <p>{{ title.subtitle }}</p>
    </div>
    <div class="heading-actions">
      <el-button v-if="pageKey === 'virtual-machines'" type="primary" :icon="Plus" @click="openVm()"
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
  <template v-if="pageKey === 'virtual-machines'"
    ><el-alert
      type="info"
      show-icon
      :closable="false"
      class="section-notice"
      title="模拟虚拟机的启停仅改变平台内的模拟状态，不会创建或运行真实操作系统。" />
    <section class="surface" v-loading="loading">
      <div class="table-toolbar">
        <div class="search-box">
          <el-input
            v-model="search"
            clearable
            :prefix-icon="Search"
            placeholder="搜索虚拟机名称"
            @keyup.enter="resetSearch"
            @clear="resetSearch"
          /><el-button @click="resetSearch">搜索</el-button>
        </div>
        <span class="record-count">{{ total }} 台虚拟机</span>
      </div>
      <el-table :data="rows" empty-text="暂无虚拟机。请先接入节点或在控制台创建模拟环境。"
        ><el-table-column prop="name" label="虚拟机名称" min-width="180"
          ><template #default="{ row }"
            ><span class="table-name">{{ row.name }}</span>
            <el-tag v-if="row.isSimulated" type="warning" size="small">模拟</el-tag></template
          ></el-table-column
        ><el-table-column prop="nodeName" label="所属节点" min-width="150" /><el-table-column
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
          ><template #default="{ row }">{{ row.diskGb }} GB</template></el-table-column
        ><el-table-column label="操作" width="275" fixed="right"
          ><template #default="{ row }"
            ><el-button
              link
              type="primary"
              :icon="VideoPlay"
              :disabled="row.status === 'running' || saving"
              @click="vmAction(row, 'start')"
              >启动</el-button
            ><el-button
              link
              type="warning"
              :icon="VideoPause"
              :disabled="row.status !== 'running' || saving"
              @click="vmAction(row, 'stop')"
              >停止</el-button
            ><el-button link type="primary" :disabled="row.status === 'running'" @click="openVm(row)">编辑</el-button
            ><el-button link type="danger" :disabled="row.status === 'running'" @click="deleteVm(row)"
              >删除</el-button
            ></template
          ></el-table-column
        ></el-table
      >
      <div class="table-footer">
        <span>所有模拟资源均不运行真实操作系统</span
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
        /><el-button @click="resetSearch">搜索</el-button>
      </div>
      <span class="record-count">共 {{ total }} 条记录</span>
    </div>
    <el-table :data="rows" v-loading="loading" empty-text="暂无操作记录"
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
      title="尚未配置供应商验签公钥。请联系交付人员配置后导入许可证；当前模拟资源仍可使用。"
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
      <DataRecord v-if="data" :data="data" /><el-empty v-else-if="!loading" description="暂未获取授权信息" />
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
    <el-form label-position="top" class="settings-form" @submit.prevent="saveSettings"
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
  <section v-else class="surface host-surface" v-loading="loading">
    <div class="card-heading">
      <div>
        <h2>管理服务器</h2>
        <p>以下数据来自当前运行主机；缺失字段会保留实际采集结果。</p>
      </div>
      <el-tag type="success" effect="light" v-if="data">实时采集</el-tag>
    </div>
    <DataRecord v-if="data" :data="data" /><el-empty v-else-if="!loading" description="暂未获取服务器信息" />
  </section>
  <el-dialog
    v-model="vmVisible"
    :title="vmEditing ? '编辑模拟虚拟机' : '新增模拟虚拟机'"
    width="600px"
    :close-on-click-modal="false"
    ><el-alert
      title="此处配置的是内置模拟资源，不创建或运行实际操作系统。"
      type="info"
      :closable="false"
      show-icon
      class="section-notice"
    /><el-form label-position="top" class="entity-form"
      ><el-form-item label="虚拟机名称" required><el-input v-model="vmModel.name" maxlength="100" /></el-form-item
      ><el-form-item label="所属模拟节点" required
        ><el-select v-model="vmModel.nodeId"
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

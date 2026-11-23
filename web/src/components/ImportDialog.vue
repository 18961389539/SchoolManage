<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { UploadFilled, Download, CircleCheck, Warning, Delete } from '@element-plus/icons-vue'
import { api, post, messageOf, passwordIssue, ApiError, type Row } from '../api'
import { appState, maxUploadMegabytes } from '../store'
import type { ImportIssue, ImportJobState, ImportPreviewResponse, ImportRow, ImportStatusResponse } from '../types'
const props = defineProps<{ modelValue: boolean; kind: string }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; committed: [] }>()
const visible = computed({ get: () => props.modelValue, set: value => emit('update:modelValue', value) })
const busy = ref(false),
  preview = ref<ImportPreviewResponse | null>(null),
  fileName = ref(''),
  error = ref(''),
  removedInvalid = ref(false),
  password = ref('')
const page = ref(1),
  pageSize = ref(50),
  onlyInvalid = ref(false)
const jobStatus = ref<ImportJobState | ''>(''),
  jobProcessed = ref(0),
  jobTotal = ref(0),
  cancelBusy = ref(false)
let pollRun = 0
let previewRun = 0
const chooseFileButton = ref<{ ref?: HTMLElement; $el?: HTMLElement }>()
const passwordInput = ref<{ focus?: () => void }>()
const fileInput = ref<HTMLInputElement>()
const progressText = computed(() => {
  if (jobStatus.value === 'queued') return '等待导入任务'
  if (jobStatus.value === 'running')
    return jobTotal.value ? `正在创建账号 ${jobProcessed.value} / ${jobTotal.value}` : '正在创建账号'
  if (jobStatus.value === 'cancelling') return '正在取消导入'
  return '正在提交导入'
})
const activeJob = computed(() => ['queued', 'running', 'cancelling'].includes(jobStatus.value))
const previewWidths: Record<string, number> = {
  name: 90,
  userName: 90,
  number: 110,
  className: 90,
  gender: 64,
  phone: 110,
  email: 160
}
const previewColumnWidth = (key: string) => previewWidths[key] || 110

const columns = [
  { key: 'name', label: '姓名' },
  { key: 'userName', label: '账号' },
  { key: 'number', label: '学号/工号' },
  { key: 'className', label: '班级' },
  { key: 'gender', label: '性别' },
  { key: 'phone', label: '手机号码' },
  { key: 'email', label: '电子邮箱' }
]
const rows = computed<ImportRow[]>(() => preview.value?.rows || [])
const invalidCount = computed(() => rows.value.filter(row => row.errors?.length).length)
const validCount = computed(() => rows.value.length - invalidCount.value)
const filteredRows = computed(() =>
  removedInvalid.value
    ? rows.value.filter(row => !row.errors?.length)
    : onlyInvalid.value
      ? rows.value.filter(row => !!row.errors?.length)
      : rows.value
)
const pagedRows = computed(() =>
  filteredRows.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value)
)
const rangeText = computed(() => {
  const total = filteredRows.value.length
  return total
    ? `第 ${(page.value - 1) * pageSize.value + 1}–${Math.min(page.value * pageSize.value, total)} 行，共 ${total} 行`
    : '没有匹配的数据行'
})
watch([() => filteredRows.value.length, pageSize], () => {
  const last = Math.max(1, Math.ceil(filteredRows.value.length / pageSize.value))
  if (page.value > last) page.value = last
})
watch([removedInvalid, onlyInvalid], () => {
  page.value = 1
})
async function chooseFile(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]
  if (!file) return
  if (file.size > appState.maxUploadBytes) {
    ElMessage.error(`文件大小不能超过 ${maxUploadMegabytes()} MB`)
    if (fileInput.value) fileInput.value.value = ''
    return
  }
  const run = ++previewRun
  fileName.value = file.name
  busy.value = true
  error.value = ''
  preview.value = null
  removedInvalid.value = false
  const data = new FormData()
  data.append('file', file)
  try {
    const result = await api<ImportPreviewResponse>(`/imports/preview?kind=${props.kind}`, {
      method: 'POST',
      body: data
    })
    if (run !== previewRun) return
    preview.value = result
    page.value = 1
    onlyInvalid.value = false
  } catch (err) {
    if (run === previewRun) error.value = messageOf(err)
  } finally {
    if (run === previewRun) busy.value = false
    if (fileInput.value) fileInput.value.value = ''
  }
}
async function waitForImport(batchId: string, run: number) {
  const deadline = Date.now() + 60 * 60 * 1000
  let consecutiveErrors = 0
  while (Date.now() < deadline && run === pollRun) {
    let job: ImportStatusResponse
    try {
      job = await api<ImportStatusResponse>(`/imports/${batchId}`)
      consecutiveErrors = 0
    } catch (err) {
      if (run !== pollRun) throw err
      consecutiveErrors++
      if (consecutiveErrors >= 3) throw err
      await new Promise(resolve => setTimeout(resolve, 1500))
      continue
    }
    if (run !== pollRun) return job
    jobStatus.value = job.status
    jobProcessed.value = Number(job.processedCount || 0)
    jobTotal.value = Number(job.totalCount || validCount.value)
    if (job.status === 'completed') return job
    if (job.status === 'failed') throw new Error(job.failureMessage || job.message || '导入任务失败')
    if (job.status === 'cancelled') throw new Error('导入任务已取消')
    await new Promise(resolve => setTimeout(resolve, 800))
  }
  throw new Error('导入任务仍在后台处理，请稍后重新打开页面确认结果')
}
function validateBeforeCommit(): boolean {
  if (!preview.value) return false
  const issue = passwordIssue(password.value)
  if (issue) {
    ElMessage.warning(issue)
    return false
  }
  if (invalidCount.value && !removedInvalid.value) {
    ElMessage.warning('请先移除待导入数据中的问题行')
    return false
  }
  return true
}
function applyServerFeedback(err: unknown) {
  error.value = messageOf(err)
  if (!(err instanceof ApiError) || !preview.value) return
  const details = err.details as Row | undefined
  if (Array.isArray(details?.rows)) {
    preview.value.rows = details.rows as ImportRow[]
    removedInvalid.value = false
  }
}
async function startImport(target: ImportPreviewResponse) {
  busy.value = true
  error.value = ''
  jobStatus.value = ''
  jobProcessed.value = 0
  jobTotal.value = validCount.value
  const run = ++pollRun
  try {
    const started = await post<ImportStatusResponse>(`/imports/${target.batchId}/commit`, {
      removeInvalidRows: removedInvalid.value,
      initialPassword: password.value
    })
    if (run !== pollRun) return
    jobStatus.value = started.status
    const result = await waitForImport(String(target.batchId), run)
    if (run !== pollRun) return
    ElMessage.success(result.message || `已导入 ${result.importedCount ?? validCount.value} 条记录`)
    emit('committed')
    jobStatus.value = ''
    busy.value = false
    visible.value = false
  } catch (err) {
    if (run !== pollRun) return
    if (jobStatus.value === 'cancelled') ElMessage.info('导入任务已取消')
    else applyServerFeedback(err)
  } finally {
    if (run === pollRun) {
      busy.value = false
      jobStatus.value = ''
    }
  }
}
async function commit() {
  if (!validateBeforeCommit()) return
  const target = preview.value!
  const kind = props.kind
  const context = pollRun
  try {
    await ElMessageBox.confirm(
      `将创建 ${validCount.value} 个${kind === 'teacher' ? '教师' : '学生'}档案及账号。账号首次登录需要修改密码。`,
      '确认导入',
      { confirmButtonText: '开始导入', cancelButtonText: '取消' }
    )
  } catch {
    return
  }
  if (pollRun !== context || props.kind !== kind || preview.value !== target) return
  await startImport(target)
}
async function cancelImport() {
  const target = preview.value
  if (!target || !activeJob.value || cancelBusy.value) return
  const run = pollRun
  cancelBusy.value = true
  try {
    const job = await post<ImportStatusResponse>(`/imports/${target.batchId}/cancel`)
    if (run !== pollRun || preview.value !== target) return
    jobStatus.value = job.status
    ElMessage.info(job.message || '正在取消导入任务')
  } catch (err) {
    if (run === pollRun && preview.value === target) ElMessage.error(messageOf(err))
  } finally {
    if (run === pollRun) cancelBusy.value = false
  }
}
async function confirmClose(): Promise<boolean> {
  if (busy.value) {
    ElMessage.warning('导入任务正在处理，请等待完成或先取消任务')
    return false
  }
  if (!preview.value) return true
  try {
    await ElMessageBox.confirm('文件已经完成校验，关闭后需要重新选择文件。', '关闭批量导入？', {
      type: 'warning',
      confirmButtonText: '关闭导入',
      cancelButtonText: '继续查看'
    })
    return true
  } catch {
    return false
  }
}

async function beforeClose(done: () => void) {
  if (await confirmClose()) done()
}

async function closeDialog() {
  if (await confirmClose()) visible.value = false
}
// Element Plus passes table slot rows as its own loose DefaultRow type, so the
// template-facing helpers accept Row while the underlying collections stay typed.
function rowClass({ row }: { row: Row }) {
  return row.errors?.length ? 'import-error-row' : ''
}
function cellError(row: Row, key: string) {
  const aliases: Record<string, string[]> = {
    name: ['姓名'],
    userName: ['账号'],
    number: ['学号', '工号', '学号/工号'],
    className: ['班级'],
    gender: ['性别'],
    phone: ['手机号', '手机号码'],
    email: ['邮箱', '电子邮箱']
  }
  return (row.errors || [])
    .filter((err: ImportIssue) => err.column === key || aliases[key]?.includes(err.column))
    .map((err: ImportIssue) => err.message)
    .join('；')
}
function focusImportStart() {
  const button = chooseFileButton.value?.ref || chooseFileButton.value?.$el
  button?.focus?.()
}

function focusImportPassword() {
  passwordInput.value?.focus?.()
}

function onImportOpened() {
  if (preview.value) focusImportPassword()
  else focusImportStart()
}

watch(
  () => props.modelValue,
  value => {
    pollRun++
    previewRun++
    if (value) {
      preview.value = null
      fileName.value = ''
      error.value = ''
      removedInvalid.value = false
      password.value = ''
      page.value = 1
      onlyInvalid.value = false
      jobStatus.value = ''
      jobProcessed.value = 0
      jobTotal.value = 0
      busy.value = false
      cancelBusy.value = false
    }
  }
)
watch(preview, value => {
  if (value) void nextTick(() => focusImportPassword())
})

onBeforeUnmount(() => {
  pollRun++
  previewRun++
})
</script>
<template>
  <el-dialog
    v-model="visible"
    :title="`批量导入${kind === 'teacher' ? '教师' : '学生'}`"
    width="min(1120px, 96vw)"
    :close-on-click-modal="false"
    :before-close="beforeClose"
    class="import-dialog"
    @opened="onImportOpened"
  >
    <div class="import-steps">
      <div class="active"><span>1</span>选择文件</div>
      <div :class="{ active: preview }"><span>2</span>校验与预览</div>
      <div :class="{ active: preview && (!invalidCount || removedInvalid) }"><span>3</span>确认导入</div>
    </div>
    <div class="import-upload">
      <div class="upload-label">
        <el-icon :size="30"><UploadFilled /></el-icon>
        <div>
          <strong>{{ fileName || '上传师生档案文件' }}</strong>
          <p>支持 .xlsx / .csv，最大 {{ maxUploadMegabytes() }} MB。请使用模板中的列名填写。</p>
        </div>
      </div>
      <div class="heading-actions">
        <el-button :icon="Download" tag="a" :href="`/api/imports/template?kind=${kind}`">下载模板</el-button
        ><el-button ref="chooseFileButton" type="primary" :loading="busy" @click="fileInput?.click()">{{
          preview ? '重新选择' : '选择文件'
        }}</el-button>
      </div>
      <input
        ref="fileInput"
        type="file"
        accept=".xlsx,.csv"
        class="visually-hidden"
        aria-label="选择导入文件"
        @change="chooseFile"
      />
    </div>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon class="section-notice" />
    <template v-if="preview"
      ><div class="import-summary">
        <span class="valid-count"
          ><el-icon><CircleCheck /></el-icon>{{ validCount }} 行通过校验</span
        ><span :class="invalidCount ? 'invalid-count' : ''"
          ><el-icon><Warning /></el-icon>{{ invalidCount }} 行需要处理</span
        ><el-button
          v-if="invalidCount && !removedInvalid"
          type="danger"
          plain
          :icon="Delete"
          @click="removedInvalid = true"
          >移除问题行</el-button
        ><el-checkbox v-if="invalidCount && !removedInvalid" v-model="onlyInvalid" class="import-filter"
          >仅看问题行</el-checkbox
        ><el-tag v-if="removedInvalid" type="info">已从待导入列表中移除 {{ invalidCount }} 行</el-tag>
      </div>
      <el-progress
        v-if="busy && activeJob"
        :percentage="jobTotal ? Math.min(100, Math.round((jobProcessed / jobTotal) * 100)) : 0"
        :status="jobStatus === 'cancelling' ? 'warning' : undefined"
        :stroke-width="6"
        class="import-progress" />
      <el-table :data="pagedRows" max-height="350" :row-class-name="rowClass" border
        ><el-table-column prop="rowNumber" label="原始行" width="72" /><el-table-column
          v-for="column in columns"
          :key="column.key"
          :label="column.label"
          :min-width="previewColumnWidth(column.key)"
          ><template #default="{ row }"
            ><el-tooltip v-if="cellError(row, column.key)" :content="cellError(row, column.key)"
              ><span class="invalid-cell">{{ row.data?.[column.key] || '空值' }}</span></el-tooltip
            ><span v-else>{{ row.data?.[column.key] || '—' }}</span></template
          ></el-table-column
        ><el-table-column label="校验结果" min-width="230"
          ><template #default="{ row }"
            ><div v-if="row.errors?.length" class="row-errors">
              <div v-for="(issue, index) in row.errors" :key="index">{{ issue.column }}：{{ issue.message }}</div>
            </div>
            <el-tag v-else type="success" size="small">校验通过</el-tag></template
          ></el-table-column
        ></el-table
      >
      <div v-if="filteredRows.length > pageSize" class="import-pager">
        <span>{{ rangeText }}</span><el-pagination
          v-model:current-page="page"
          v-model:page-size="pageSize"
          :total="filteredRows.length"
          :page-sizes="[50, 100, 200]"
          :pager-count="5"
          layout="sizes, prev, pager, next"
          size="small"
          background
        />
      </div>
      <div class="import-password">
        <div>
          <strong>设置导入账号的初始密码</strong>
          <p>密码须为 12–128 位，且同时包含字母和数字。导入后请通过适当渠道单独告知本人；首次登录强制修改。</p>
        </div>
        <el-input
          ref="passwordInput"
          v-model="password"
          type="password"
          show-password
          autocomplete="new-password"
          placeholder="密码须为 12–128 位，且同时包含字母和数字"
        /></div
    ></template>
    <div v-else-if="!busy" class="import-empty">
      <el-icon :size="32"><UploadFilled /></el-icon>
      <p>选择文件后，将在这里显示每一行的校验结果</p>
    </div>
    <template #footer
      ><span class="import-footnote">{{
        busy && activeJob ? progressText : '移除问题行只影响本次待导入数据，不会删除已有档案。'
      }}</span
      ><el-button v-if="activeJob && jobStatus !== 'cancelling'" :loading="cancelBusy" @click="cancelImport"
        >取消导入</el-button
      ><el-button :disabled="busy" @click="closeDialog">关闭</el-button
      ><el-button
        type="primary"
        :disabled="!preview || !validCount || (!!invalidCount && !removedInvalid) || busy"
        :loading="busy"
        @click="commit"
        >{{ busy && activeJob ? progressText : `导入 ${preview ? validCount : ''} 条有效记录` }}</el-button
      ></template
    >
  </el-dialog>
</template>
<style scoped>
:global(.import-dialog .el-table .cell) {
  padding-left: 8px;
  padding-right: 8px;
}

.import-pager {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-top: 12px;
  font-size: 12px;
  color: #56697c;
}
</style>

<script setup lang="ts">
import { onUnmounted, ref, watch } from 'vue'
import { Camera, Delete, Download, Monitor, Refresh, Search, Upload } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, bytes, dateTime, del, downloadFile, messageOf, post } from '../api'
import { virtualMachineStatusText } from '../camp'
import type { CaseEvidence, ClassroomCaseSession, ClassroomCaseSessionList } from '../types'
import ClassroomCaseWork from './ClassroomCaseWork.vue'
import TableEmpty from './TableEmpty.vue'

const props = defineProps<{ visible: boolean; classroomId: string; classroomName: string }>()
const emit = defineEmits<{ 'update:visible': [value: boolean] }>()

const sessions = ref<ClassroomCaseSession[]>([])
const total = ref(0)
const activeCount = ref(0)
const loading = ref(false)
const error = ref('')
const search = ref('')
const statusFilter = ref<'active' | 'all'>('active')
const autoRefresh = ref(true)
const restartingId = ref('')

const environmentVisible = ref(false)
const environmentSession = ref<ClassroomCaseSession | null>(null)
const environmentKey = ref(0)

const evidenceVisible = ref(false)
const evidenceSession = ref<ClassroomCaseSession | null>(null)
const evidences = ref<CaseEvidence[]>([])
const evidenceLoading = ref(false)
const evidenceNote = ref('')
const uploading = ref(false)
const fileInput = ref<HTMLInputElement | null>(null)

let timer: number | undefined
let requestVersion = 0

async function load(silent = false) {
  if (!props.visible || !props.classroomId || (silent && loading.value)) return
  const version = ++requestVersion
  if (!silent) loading.value = true
  if (!silent) error.value = ''
  try {
    const params = new URLSearchParams({ status: statusFilter.value })
    if (search.value.trim()) params.set('search', search.value.trim())
    const data = await api<ClassroomCaseSessionList>(
      `/classrooms/${props.classroomId}/case-sessions?${params.toString()}`
    )
    if (version !== requestVersion) return
    sessions.value = data.items
    total.value = data.total
    activeCount.value = data.activeCount
    error.value = ''
  } catch (failure) {
    if (version === requestVersion) error.value = messageOf(failure)
  } finally {
    if (version === requestVersion) loading.value = false
  }
}

function clearSessionSearch() {
  search.value = ''
  void load()
}

function showAllSessions() {
  statusFilter.value = 'all'
  void load()
}

function startTimer() {
  stopTimer()
  if (autoRefresh.value) timer = window.setInterval(() => void load(true), 15000)
}

function stopTimer() {
  if (timer !== undefined) {
    window.clearInterval(timer)
    timer = undefined
  }
}

function openEnvironment(row: ClassroomCaseSession) {
  environmentSession.value = row
  environmentKey.value++
  environmentVisible.value = true
}

async function restart(row: ClassroomCaseSession) {
  try {
    await ElMessageBox.confirm(
      `将为“${row.studentName}”重新分配并启动实操虚拟机，当前环境中的临时操作可能丢失。是否继续？`,
      '重启实操环境',
      { type: 'warning', confirmButtonText: '重启', cancelButtonText: '取消' }
    )
    restartingId.value = row.sessionId
    await post(`/classrooms/case-sessions/${row.sessionId}/restart`)
    ElMessage.success('实操环境已重启')
    await load()
    if (environmentVisible.value && environmentSession.value?.sessionId === row.sessionId) {
      environmentSession.value =
        sessions.value.find(item => item.sessionId === row.sessionId) || environmentSession.value
      environmentKey.value++
    }
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  } finally {
    restartingId.value = ''
  }
}

async function openEvidence(row: ClassroomCaseSession) {
  evidenceSession.value = row
  evidenceVisible.value = true
  evidenceNote.value = ''
  await loadEvidences()
}

async function loadEvidences() {
  if (!evidenceSession.value) return
  evidenceLoading.value = true
  try {
    const data = await api<{ items: CaseEvidence[] }>(
      `/classrooms/case-sessions/${evidenceSession.value.sessionId}/evidences`
    )
    evidences.value = data.items
  } catch (failure) {
    evidences.value = []
    ElMessage.error(messageOf(failure))
  } finally {
    evidenceLoading.value = false
  }
}

async function uploadEvidence(file: File) {
  if (!evidenceSession.value) return
  if (!/^image\/(png|jpeg|webp)$/.test(file.type)) {
    ElMessage.warning('仅支持 PNG、JPEG 或 WebP 截图')
    return
  }
  if (file.size > 5 * 1024 * 1024) {
    ElMessage.warning('截图不能超过 5MB')
    return
  }
  uploading.value = true
  try {
    const form = new FormData()
    form.append('file', file)
    form.append('note', evidenceNote.value.trim())
    await api(`/classrooms/case-sessions/${evidenceSession.value.sessionId}/evidences`, {
      method: 'POST',
      body: form
    })
    ElMessage.success('截图已保存')
    evidenceNote.value = ''
    await Promise.all([loadEvidences(), load()])
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    uploading.value = false
  }
}

function chooseFile() {
  fileInput.value?.click()
}

async function onFileChange(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (file) await uploadEvidence(file)
}

async function captureScreen() {
  if (!navigator.mediaDevices?.getDisplayMedia) {
    ElMessage.warning('当前浏览器不支持屏幕捕获，请改用上传截图')
    return
  }
  let stream: MediaStream
  try {
    stream = await navigator.mediaDevices.getDisplayMedia({ video: true })
  } catch {
    return
  }
  try {
    const video = document.createElement('video')
    video.srcObject = stream
    video.muted = true
    await new Promise<void>((resolve, reject) => {
      video.onloadedmetadata = () => resolve()
      video.onerror = () => reject(new Error('无法读取屏幕画面'))
    })
    await video.play()
    await new Promise(resolve => window.setTimeout(resolve, 200))
    const canvas = document.createElement('canvas')
    canvas.width = video.videoWidth
    canvas.height = video.videoHeight
    canvas.getContext('2d')?.drawImage(video, 0, 0)
    const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/png'))
    if (!blob) throw new Error('无法生成截图')
    const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19)
    await uploadEvidence(new File([blob], `实操截图-${stamp}.png`, { type: 'image/png' }))
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    stream.getTracks().forEach(track => track.stop())
  }
}

async function download(evidence: CaseEvidence) {
  try {
    await downloadFile(`/case-evidences/${evidence.id}/download`, evidence.fileName)
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  }
}

async function removeEvidence(evidence: CaseEvidence) {
  try {
    await ElMessageBox.confirm(`删除截图“${evidence.fileName}”后不可恢复，是否继续？`, '删除取证截图', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消'
    })
    await del(`/case-evidences/${evidence.id}`)
    ElMessage.success('截图已删除')
    await Promise.all([loadEvidences(), load()])
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

function sessionStatusText(status: string) {
  return status === 'active' ? '进行中' : status === 'saved' ? '已保存' : '已结束'
}

watch(
  () => props.visible,
  visible => {
    if (visible) {
      search.value = ''
      statusFilter.value = 'active'
      sessions.value = []
      total.value = 0
      activeCount.value = 0
      void load()
      startTimer()
    } else {
      requestVersion++
      loading.value = false
      stopTimer()
      environmentVisible.value = false
      evidenceVisible.value = false
    }
  }
)

watch(autoRefresh, () => {
  if (props.visible) startTimer()
})
onUnmounted(stopTimer)
</script>

<template>
  <el-drawer
    :model-value="visible"
    :title="`${classroomName || '课堂'} · 实操监控`"
    header-class="case-monitor-drawer-header"
    size="min(74rem, 100vw)"
    @update:model-value="value => emit('update:visible', value)"
  >
    <section class="monitor-controls" aria-label="实操监控筛选">
      <div class="monitor-overview">
        <div class="monitor-counts" aria-live="polite">
          <span
            >进行中 <strong>{{ activeCount }}</strong></span
          >
          <span
            >当前结果 <strong>{{ total }}</strong></span
          >
        </div>
        <el-switch v-model="autoRefresh" active-text="自动刷新" aria-label="每 15 秒自动刷新实操记录" />
      </div>
      <form class="monitor-filters" @submit.prevent="load()">
        <el-radio-group v-model="statusFilter" aria-label="按会话状态筛选" @change="load()">
          <el-radio-button value="active">进行中</el-radio-button>
          <el-radio-button value="all">全部记录</el-radio-button>
        </el-radio-group>
        <div class="monitor-search">
          <el-input
            v-model="search"
            clearable
            aria-label="搜索学生、班级、题目或虚拟机"
            placeholder="搜索学生、班级、题目或虚拟机"
            :prefix-icon="Search"
            @clear="load()"
          />
          <el-button native-type="submit" :icon="Refresh" :loading="loading">刷新</el-button>
        </div>
      </form>
    </section>

    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
    <div v-loading="loading" class="monitor-results" :aria-busy="loading">
      <table v-if="sessions.length" class="monitor-table" aria-label="实操会话" role="table">
        <thead role="rowgroup">
          <tr role="row">
            <th scope="col" role="columnheader">学生</th>
            <th scope="col" role="columnheader">实操题目</th>
            <th scope="col" role="columnheader">环境</th>
            <th scope="col" role="columnheader">状态</th>
            <th scope="col" role="columnheader">实操时间</th>
            <th scope="col" role="columnheader">重启与取证</th>
            <th scope="col" role="columnheader">操作</th>
          </tr>
        </thead>
        <tbody role="rowgroup">
          <tr v-for="row in sessions" :key="row.sessionId" role="row">
            <td class="monitor-student" role="cell">
              <strong>{{ row.studentName }}</strong>
              <small>{{ row.studentNumber || row.userName }} · {{ row.className || '未分班' }}</small>
            </td>
            <td role="cell">
              <span class="mobile-label" aria-hidden="true">实操题目</span>
              {{ row.resourceName }}
              <small>{{ [row.chapterName, row.sectionName].filter(Boolean).join(' / ') }}</small>
            </td>
            <td role="cell">
              <span class="mobile-label" aria-hidden="true">环境</span>
              <template v-if="row.virtualMachineId">
                {{ row.virtualMachineName }}
                <small>
                  {{ row.nodeName || '节点未知' }}
                  <span class="vm-state">· {{ virtualMachineStatusText(row.virtualMachineStatus) }}</span>
                </small>
              </template>
              <span v-else class="muted">未分配虚拟机</span>
            </td>
            <td class="monitor-status" role="cell">
              <el-tag :type="row.status === 'active' ? 'success' : 'info'" effect="light" size="small">
                {{ sessionStatusText(row.status) }}
              </el-tag>
            </td>
            <td role="cell">
              <span class="mobile-label" aria-hidden="true">实操时间</span>
              <div>
                开始 <time :datetime="row.startedAt">{{ dateTime(row.startedAt) }}</time>
              </div>
              <small v-if="row.endedAt"
                >结束 <time :datetime="row.endedAt">{{ dateTime(row.endedAt) }}</time></small
              >
            </td>
            <td role="cell">
              <span class="mobile-label" aria-hidden="true">重启与取证</span>
              <div class="activity-value">重启 {{ row.restartCount }} 次</div>
              <div class="activity-value">截图 {{ row.evidenceCount }} 张</div>
            </td>
            <td class="monitor-actions-cell" role="cell">
              <div class="monitor-actions">
                <el-button link type="primary" :icon="Monitor" @click="openEnvironment(row)">环境</el-button>
                <el-button link type="primary" :icon="Camera" @click="openEvidence(row)">取证</el-button>
                <el-button
                  link
                  type="warning"
                  :disabled="!row.canRestart"
                  :loading="restartingId === row.sessionId"
                  @click="restart(row)"
                >
                  重启
                </el-button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-else-if="!loading && !error" class="monitor-empty" role="status">
        <strong>{{
          search.trim() ? '没有符合条件的实操会话' : statusFilter === 'active' ? '暂无进行中的实操会话' : '暂无实操会话'
        }}</strong>
        <p>
          {{
            search.trim()
              ? '试试调整关键词，或清除搜索查看当前筛选下的会话。'
              : statusFilter === 'active'
                ? '学生开始案例实操后会出现在这里，也可以切换查看全部会话。'
                : '学生开始案例实操后，这里会显示会话状态。'
          }}
        </p>
        <el-button v-if="search.trim()" @click="clearSessionSearch">清除搜索</el-button>
        <el-button v-else-if="statusFilter === 'active'" @click="showAllSessions">查看全部会话</el-button>
      </div>
    </div>

    <el-dialog
      v-model="environmentVisible"
      :title="environmentSession ? `${environmentSession.studentName} · 实操环境` : '实操环境'"
      width="min(860px, 94vw)"
      append-to-body
    >
      <ClassroomCaseWork
        v-if="environmentVisible && environmentSession"
        :key="environmentKey"
        :url="`/classrooms/case-sessions/${environmentSession.sessionId}/work`"
      />
    </el-dialog>

    <el-dialog
      v-model="evidenceVisible"
      :title="evidenceSession ? `${evidenceSession.studentName} · 实操截图取证` : '实操截图取证'"
      width="min(760px, 94vw)"
      append-to-body
    >
      <div class="evidence-toolbar">
        <el-button type="primary" :icon="Camera" :loading="uploading" @click="captureScreen">截取屏幕</el-button>
        <el-button :icon="Upload" :loading="uploading" @click="chooseFile">上传截图</el-button>
        <el-input
          v-model="evidenceNote"
          maxlength="300"
          placeholder="取证说明（可选，最多 300 字）"
          class="evidence-note"
        />
        <input ref="fileInput" type="file" accept="image/png,image/jpeg,image/webp" hidden @change="onFileChange" />
      </div>
      <el-alert
        title="截图用于记录学生实操过程，支持 PNG、JPEG、WebP，单张不超过 5MB，每个会话最多 50 张。"
        type="info"
        show-icon
        :closable="false"
        class="section-notice"
      />
      <el-table v-loading="evidenceLoading" :data="evidences" row-key="id" empty-text="">
        <template #empty>
          <TableEmpty
            v-if="!evidenceLoading"
            title="暂无取证截图"
            hint="点击“截取屏幕”或“上传截图”后，取证记录会显示在这里。"
          />
        </template>
        <el-table-column label="预览" width="110">
          <template #default="{ row }">
            <el-image
              :src="`/api/case-evidences/${row.id}/download`"
              :preview-src-list="[`/api/case-evidences/${row.id}/download`]"
              preview-teleported
              fit="cover"
              class="evidence-thumb"
            />
          </template>
        </el-table-column>
        <el-table-column prop="fileName" label="文件名" min-width="170" show-overflow-tooltip />
        <el-table-column label="大小" width="90">
          <template #default="{ row }">{{ bytes(row.size) }}</template>
        </el-table-column>
        <el-table-column prop="note" label="说明" min-width="150" show-overflow-tooltip />
        <el-table-column label="取证人 / 时间" min-width="165">
          <template #default="{ row }">
            {{ row.capturedByName || '—' }}<br />
            <span class="muted">{{ dateTime(row.createdAt) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="120" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" :icon="Download" @click="download(row as CaseEvidence)">下载</el-button>
            <el-button link type="danger" :icon="Delete" @click="removeEvidence(row as CaseEvidence)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-dialog>
  </el-drawer>
</template>

<style scoped>
:global(.el-drawer__header.case-monitor-drawer-header) {
  margin-bottom: 8px;
}

.monitor-controls {
  display: grid;
  gap: 14px;
  margin-bottom: 16px;
}

.monitor-overview,
.monitor-filters,
.monitor-search,
.monitor-counts {
  display: flex;
  align-items: center;
  gap: 10px;
}

.monitor-overview,
.monitor-filters {
  justify-content: space-between;
}

.monitor-counts {
  flex-wrap: wrap;
}

.monitor-counts span {
  display: inline-flex;
  align-items: baseline;
  gap: 8px;
  padding: 5px 10px;
  border-radius: 7px;
  background: var(--el-fill-color-light);
  color: var(--el-text-color-regular);
  font-size: 13px;
}

.monitor-counts strong {
  color: var(--el-text-color-primary);
  font-size: 16px;
  font-variant-numeric: tabular-nums;
}

.monitor-search {
  flex: 0 1 25rem;
  min-width: 0;
}

.monitor-search :deep(.el-button) {
  flex: none;
}

.monitor-results {
  min-height: 76px;
}

.monitor-table {
  width: 100%;
  border: 1px solid var(--el-border-color-light);
  border-spacing: 0;
  border-radius: 10px;
  table-layout: fixed;
  color: var(--el-text-color-primary);
  font-size: 14px;
}

.monitor-table th,
.monitor-table td {
  padding: 12px;
  text-align: left;
  vertical-align: middle;
}

.monitor-table th {
  background: var(--el-fill-color-light);
  color: var(--el-text-color-regular);
  font-weight: 600;
}

.monitor-table th:first-child {
  width: 13%;
  border-start-start-radius: 9px;
}

.monitor-table th:nth-child(2) {
  width: 16%;
}

.monitor-table th:nth-child(3) {
  width: 19%;
}

.monitor-table th:nth-child(4) {
  width: 8%;
}

.monitor-table th:nth-child(5) {
  width: 18%;
}

.monitor-table th:nth-child(6) {
  width: 10%;
}

.monitor-table th:last-child {
  width: 16%;
  border-start-end-radius: 9px;
}

.monitor-table td {
  border-top: 1px solid var(--el-border-color-lighter);
  overflow-wrap: anywhere;
  line-height: 1.55;
}

.monitor-table small {
  display: block;
  margin-top: 3px;
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.activity-value {
  color: var(--el-text-color-regular);
  font-size: 13px;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.mobile-label {
  display: none;
}

.monitor-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 0 9px;
}

.monitor-actions :deep(.el-button) {
  margin: 0;
  padding-inline: 0;
}

.vm-state {
  white-space: nowrap;
}

.monitor-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  margin: 0;
  padding: 32px 16px;
  border: 1px dashed var(--el-border-color);
  border-radius: 10px;
  text-align: center;
}

.monitor-empty p {
  max-width: 36ch;
  margin: 0;
  color: var(--el-text-color-regular);
  font-size: 12px;
  line-height: 1.5;
}

@media (max-width: 930px) {
  .monitor-table,
  .monitor-table tbody {
    display: block;
    border: 0;
  }

  .monitor-table thead {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip-path: inset(50%);
    white-space: nowrap;
  }

  .monitor-table tbody {
    display: grid;
    gap: 12px;
  }

  .monitor-table tr {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 12px 16px;
    padding: 14px;
    border: 1px solid var(--el-border-color-light);
    border-radius: 10px;
  }

  .monitor-table td {
    display: block;
    padding: 0;
    border: 0;
  }

  .monitor-status {
    grid-area: 1 / 2;
    justify-self: end;
  }

  .mobile-label {
    display: block;
    margin-bottom: 2px;
    color: var(--el-text-color-regular);
    font-size: 12px;
    font-weight: 600;
  }

  .monitor-table td.monitor-actions-cell {
    grid-column: 1 / -1;
    padding-top: 6px;
    border-top: 1px solid var(--el-border-color-lighter);
  }

  .monitor-actions {
    gap: 8px 18px;
  }

  .monitor-actions :deep(.el-button) {
    min-height: 36px;
  }
}

@media (max-width: 540px) {
  .monitor-filters {
    align-items: stretch;
    flex-direction: column;
  }

  .monitor-search {
    flex: auto;
  }

  .monitor-counts {
    gap: 5px;
  }

  .monitor-counts span {
    gap: 4px;
    padding-inline: 6px;
  }
}

.muted {
  color: #64788a;
  font-size: 12px;
}

.evidence-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-bottom: 12px;
}

.evidence-note {
  flex: 1 1 220px;
  min-width: 180px;
}

.evidence-thumb {
  width: 76px;
  height: 48px;
  border-radius: 6px;
  cursor: zoom-in;
}
</style>

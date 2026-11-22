<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { Clock, Monitor, Refresh, RefreshRight } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, dateTime, messageOf, post } from '../api'
import { virtualMachineStatusText } from '../camp'
import { countdownText } from '../exam'
import { parseApiDate } from '../time'
import type { CampMonitorRow, CampRestartResponse } from '../types'
import CampEnvironmentPanel from './CampEnvironmentPanel.vue'
import TableEmpty from './TableEmpty.vue'

const items = ref<CampMonitorRow[]>([])
const loading = ref(false)
const error = ref('')
const now = ref(Date.now())
const envVisible = ref(false)
const envAttempt = ref<CampMonitorRow | null>(null)
const restarting = ref('')
let ticker: number | undefined
let refresher: number | undefined

const envUrl = computed(() => (envAttempt.value ? `/camp/monitor/${envAttempt.value.attemptId}/environment` : ''))
const nearest = computed(() => {
  const seconds = items.value
    .filter(item => item.remainingSeconds > 0)
    .map(item => Math.max(0, Math.floor((parseApiDate(item.deadline).getTime() - now.value) / 1000)))
  return seconds.length ? Math.min(...seconds) : 0
})

function remaining(row: CampMonitorRow): number {
  return Math.max(0, Math.floor((parseApiDate(row.deadline).getTime() - now.value) / 1000))
}

function machineStatusType(status: string): 'success' | 'warning' | 'info' {
  if (status === 'running') return 'success'
  if (status === 'starting') return 'warning'
  return 'info'
}

async function load(silent = false) {
  if (!silent) loading.value = true
  error.value = ''
  try {
    const data = await api<{ items?: CampMonitorRow[] }>('/camp/monitor')
    items.value = data.items || []
    now.value = Date.now()
  } catch (failure) {
    if (!silent) error.value = messageOf(failure)
  } finally {
    if (!silent) loading.value = false
  }
}

function openEnvironment(row: CampMonitorRow) {
  envAttempt.value = row
  envVisible.value = true
}

async function restart(row: CampMonitorRow) {
  if (restarting.value) return
  try {
    await ElMessageBox.confirm(
      `将为 ${row.studentName} 重启测评虚拟机并重新计时环境，学生作答不会被清空。确认重启吗？`,
      '重启测评环境',
      { type: 'warning', confirmButtonText: '重启', cancelButtonText: '取消' }
    )
  } catch {
    return
  }
  restarting.value = row.attemptId
  try {
    const result = await post<CampRestartResponse>(`/camp/monitor/${row.attemptId}/restart`, {})
    ElMessage.success(`已重启虚拟机 ${result.virtualMachineName}`)
    await load(true)
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    restarting.value = ''
  }
}

onMounted(() => {
  void load()
  ticker = window.setInterval(() => {
    now.value = Date.now()
  }, 1000)
  refresher = window.setInterval(() => void load(true), 15000)
})

onBeforeUnmount(() => {
  if (ticker !== undefined) window.clearInterval(ticker)
  if (refresher !== undefined) window.clearInterval(refresher)
})
</script>

<template>
  <section class="page-heading">
    <div>
      <h1>测评监控</h1>
      <p>查看正在进行的岗位测评、剩余时间与虚拟机状态，必要时可重启测评环境。</p>
    </div>
    <el-button :icon="Refresh" :loading="loading" @click="load()">刷新监控</el-button>
  </section>

  <section class="surface monitor-toolbar">
    <div class="monitor-summary">
      <span
        >进行中 <strong>{{ loading && !items.length ? '—' : items.length }}</strong> 人</span
      >
      <span v-if="nearest > 0" class="monitor-nearest">
        <el-icon><Clock /></el-icon>最近截止 {{ countdownText(nearest) }}
      </span>
    </div>
    <span class="monitor-hint">每 15 秒自动刷新 · 页面每 1 秒更新倒计时</span>
  </section>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />

  <section class="surface monitor-table">
    <el-table v-loading="loading" :data="items" row-key="attemptId" empty-text="">
      <template #empty>
        <TableEmpty
          v-if="!loading"
          :title="error ? '监控数据加载失败' : '暂无进行中的测评'"
          :hint="error ? '请检查服务状态后刷新重试' : '学生开始测评后，这里会显示实时状态。'"
        />
      </template>
      <el-table-column label="学生" min-width="150">
        <template #default="{ row }">
          <div class="student-cell">
            <strong>{{ row.studentName }}</strong>
            <span>{{ row.studentNumber || row.studentUserName }}</span>
          </div>
        </template>
      </el-table-column>
      <el-table-column prop="className" label="班级" min-width="120" show-overflow-tooltip />
      <el-table-column prop="positionName" label="岗位" min-width="170" show-overflow-tooltip />
      <el-table-column label="开始时间" min-width="160">
        <template #default="{ row }">{{ dateTime(row.startedAt) }}</template>
      </el-table-column>
      <el-table-column label="剩余时间" width="130">
        <template #default="{ row }">
          <span class="monitor-countdown" :class="{ urgent: remaining(row as CampMonitorRow) < 300 }">
            {{ countdownText(remaining(row as CampMonitorRow)) }}
          </span>
        </template>
      </el-table-column>
      <el-table-column label="测评虚拟机" min-width="150">
        <template #default="{ row }">
          <template v-if="row.virtualMachineName">
            <span class="machine-name">{{ row.virtualMachineName }}</span>
            <el-tag :type="machineStatusType(row.virtualMachineStatus)" size="small" effect="plain">
              {{ virtualMachineStatusText(row.virtualMachineStatus) }}
            </el-tag>
          </template>
          <span v-else class="muted">未分配</span>
        </template>
      </el-table-column>
      <el-table-column label="操作" width="190" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" :icon="Monitor" @click="openEnvironment(row as CampMonitorRow)"
            >环境</el-button
          >
          <el-button
            link
            type="warning"
            :icon="RefreshRight"
            :disabled="!row.canRestart"
            :loading="restarting === row.attemptId"
            @click="restart(row as CampMonitorRow)"
          >
            重启环境
          </el-button>
        </template>
      </el-table-column>
    </el-table>
  </section>

  <el-dialog
    v-model="envVisible"
    :title="envAttempt ? `${envAttempt.studentName} · 测评环境` : '测评环境'"
    width="min(52rem, 94vw)"
    append-to-body
    destroy-on-close
  >
    <CampEnvironmentPanel v-if="envVisible && envUrl" :source-url="envUrl" :attempt-id="envAttempt?.attemptId" />
  </el-dialog>
</template>

<style scoped>
.monitor-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 10px;
  padding: 14px 18px;
  background: #f8fbfb;
  border-bottom: 1px solid #dce7eb;
}

.monitor-summary {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 16px;
  color: #56697c;
  font-size: 13px;
}

.monitor-summary strong {
  color: #23405c;
  font-size: 16px;
}

.monitor-nearest {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  color: #94610f;
}

.monitor-hint {
  color: #56697c;
  font-size: 11px;
}

.monitor-table {
  padding: 4px 8px 8px;
}

.student-cell {
  display: grid;
  gap: 2px;
}

.student-cell strong {
  color: #26425d;
  font-size: 13px;
}

.student-cell span {
  color: #56697c;
  font-size: 11px;
}

.monitor-countdown {
  color: #2f6d62;
  font-size: 14px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.monitor-countdown.urgent {
  color: #c2410c;
}

.machine-name {
  margin-right: 6px;
  color: #35546f;
  font-size: 12px;
}

.muted {
  color: #5d6f80;
  font-size: 12px;
}
</style>

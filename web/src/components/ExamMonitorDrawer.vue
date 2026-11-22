<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { Refresh, Search } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import { attemptStatusLabel, attemptStatusType, deploymentLabel, deploymentType } from '../exam'
import type { ExamMonitorResponse, ExamMonitorRow } from '../types'

const props = defineProps<{ modelValue: boolean; examId: string; examName: string }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()

const visible = computed({
  get: () => props.modelValue,
  set: value => emit('update:modelValue', value)
})

const data = ref<ExamMonitorResponse | null>(null)
const loading = ref(false)
const error = ref('')
const search = ref('')
let timer: number | undefined

const items = computed(() => {
  const rows = data.value?.items || []
  const keyword = search.value.trim().toLowerCase()
  if (!keyword) return rows
  return rows.filter(item =>
    [item.name, item.userName, item.number, item.className].some(value => value.toLowerCase().includes(keyword))
  )
})

async function load(silent = false) {
  if (!props.examId) return
  if (!silent) loading.value = true
  error.value = ''
  try {
    data.value = await api<ExamMonitorResponse>(`/exams/${props.examId}/monitor`)
  } catch (failure) {
    if (!silent) error.value = messageOf(failure)
  } finally {
    if (!silent) loading.value = false
  }
}

function startTimer() {
  stopTimer()
  timer = window.setInterval(() => void load(true), 20000)
}

function stopTimer() {
  if (timer !== undefined) {
    window.clearInterval(timer)
    timer = undefined
  }
}

watch(visible, open => {
  if (open) {
    search.value = ''
    void load()
    startTimer()
  } else {
    stopTimer()
  }
})

onBeforeUnmount(stopTimer)

function rowProgressType(row: ExamMonitorRow): 'success' | 'warning' | 'exception' {
  if (row.status === 'submitted' || row.status === 'graded') return 'success'
  if (row.status === 'inProgress') return row.progress >= 100 ? 'success' : 'warning'
  return 'exception'
}
</script>

<template>
  <el-drawer v-model="visible" :title="`考试监控 · ${examName}`" size="min(1020px, 96vw)">
    <div class="monitor-top">
      <div class="monitor-counts">
        <span
          ><small>应考</small><strong>{{ data?.counts.expected ?? 0 }}</strong></span
        >
        <span
          ><small>已开考</small
          ><strong>{{ data?.counts.expected ? data.counts.expected - data.counts.notStarted : 0 }}</strong></span
        >
        <span class="in-progress"
          ><small>作答中</small><strong>{{ data?.counts.inProgress ?? 0 }}</strong></span
        >
        <span class="submitted"
          ><small>待批阅</small><strong>{{ data?.counts.submitted ?? 0 }}</strong></span
        >
        <span class="graded"
          ><small>已批阅</small><strong>{{ data?.counts.graded ?? 0 }}</strong></span
        >
        <span
          ><small>未参加</small><strong>{{ data?.counts.notStarted ?? 0 }}</strong></span
        >
        <span
          ><small>环境就绪</small><strong>{{ data?.counts.deployed ?? 0 }}</strong></span
        >
      </div>
      <div class="monitor-actions">
        <el-input v-model="search" class="search-box" clearable placeholder="搜索学生" :prefix-icon="Search" />
        <el-button :icon="Refresh" :loading="loading" @click="load()">刷新</el-button>
      </div>
    </div>
    <p class="monitor-hint">每 20 秒自动刷新；进度来自学生已保存的答案。</p>

    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
    <el-table v-loading="loading" :data="items" row-key="studentUserId" empty-text="暂无监控数据">
      <el-table-column prop="name" label="学生" min-width="130">
        <template #default="{ row }">
          <strong>{{ row.name }}</strong>
          <p class="monitor-sub">{{ row.userName }} · {{ row.number }}</p>
        </template>
      </el-table-column>
      <el-table-column prop="className" label="班级" min-width="110">
        <template #default="{ row }">{{ row.className || '未分班' }}</template>
      </el-table-column>
      <el-table-column label="状态" width="104">
        <template #default="{ row }">
          <el-tag :type="attemptStatusType(row.status)" size="small">{{ attemptStatusLabel(row.status) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="作答进度" min-width="190">
        <template #default="{ row }">
          <div class="progress-cell">
            <el-progress
              :percentage="row.progress"
              :stroke-width="8"
              :status="rowProgressType(row as ExamMonitorRow)"
              :show-text="false"
            />
            <span>{{ row.answeredCount }} / {{ row.questionCount }}</span>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="环境" width="112">
        <template #default="{ row }">
          <el-tag :type="deploymentType(row.deploymentStatus)" size="small" effect="plain">
            {{ deploymentLabel(row.deploymentStatus) }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="最近作答" width="168">
        <template #default="{ row }">{{ dateTime(row.lastAnsweredAt) }}</template>
      </el-table-column>
      <el-table-column label="提交时间" width="168">
        <template #default="{ row }">
          {{ dateTime(row.submittedAt) }}
          <p v-if="row.autoSubmitted" class="monitor-sub auto">超时自动提交</p>
        </template>
      </el-table-column>
      <el-table-column label="成绩" width="90" align="center">
        <template #default="{ row }">
          <span v-if="row.totalScore !== null">{{ row.totalScore }} / {{ row.maxScore }}</span>
          <span v-else class="monitor-sub">—</span>
        </template>
      </el-table-column>
    </el-table>
  </el-drawer>
</template>

<style scoped>
.monitor-top {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  align-items: flex-start;
  gap: 16px;
}

.monitor-counts {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.monitor-counts > span {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 72px;
  padding: 8px 12px;
  border: 1px solid #e6edf5;
  border-radius: 8px;
  background: #f9fbfd;
}

.monitor-counts small {
  color: #56697c;
  font-size: 11px;
}

.monitor-counts strong {
  color: #2d4a64;
  font-size: 20px;
}

.monitor-counts .in-progress strong {
  color: #94610f;
}

.monitor-counts .submitted strong {
  color: #94610f;
}

.monitor-counts .graded strong {
  color: #12735f;
}

.monitor-actions {
  display: flex;
  gap: 8px;
}

.monitor-actions .search-box {
  width: 200px;
}

.monitor-hint {
  margin: 10px 0 16px;
  color: #5d6f80;
  font-size: 12px;
}

.monitor-sub {
  margin: 3px 0 0;
  color: #56697c;
  font-size: 12px;
}

.monitor-sub.auto {
  color: #94610f;
}

.progress-cell {
  display: flex;
  align-items: center;
  gap: 10px;
}

.progress-cell :deep(.el-progress) {
  flex: 1;
}

.progress-cell span {
  color: #56697c;
  font-size: 12px;
  white-space: nowrap;
}

@media (max-width: 720px) {
  .monitor-actions,
  .monitor-actions .search-box {
    width: 100%;
  }
}
</style>

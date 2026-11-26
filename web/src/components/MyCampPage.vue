<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { Clock, Refresh, Search, Trophy } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import { readListState, useListPersist } from '../listState'
import { campAttemptLabel, campAttemptStatusType } from '../camp'
import { countdownText } from '../exam'
import { durationText } from '../teaching'
import { parseApiDate } from '../time'
import { usePolling } from '../usePolling'
import type { CampPositionLearnerSummary } from '../types'
import CampRecordDrawer from './CampRecordDrawer.vue'
import CampReportDrawer from './CampReportDrawer.vue'

const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()

const items = ref<CampPositionLearnerSummary[]>([])
const loading = ref(false)
const error = ref('')
const search = ref(readListState('my-camp').search)
const { now } = usePolling(() => load(true), { intervalMs: 30000 })
useListPersist('my-camp', () => ({ search: search.value, filters: {} }))
const reportVisible = ref(false)
const recordVisible = ref(false)
const activeAttemptId = ref('')
let requestId = 0
let fetching = false

const sections = [
  { key: 'resume', title: '正在测评' },
  { key: 'ready', title: '可开始测评' },
  { key: 'graded', title: '已完成测评' },
  { key: 'waiting', title: '暂不可开始' }
] as const

function sectionOf(item: CampPositionLearnerSummary): (typeof sections)[number]['key'] {
  if (item.attempt?.status === 'inProgress') return 'resume'
  if (item.attempt?.status === 'graded') return 'graded'
  return item.canStart ? 'ready' : 'waiting'
}

const filtered = computed(() => {
  const keyword = search.value.trim().toLowerCase()
  if (!keyword) return items.value
  return items.value.filter(item => [item.name, item.description].some(value => value.toLowerCase().includes(keyword)))
})

const grouped = computed(() =>
  sections
    .map(section => ({
      ...section,
      positions: filtered.value.filter(item => sectionOf(item) === section.key)
    }))
    .filter(section => section.positions.length)
)

function remaining(item: CampPositionLearnerSummary): number {
  if (item.attempt?.status === 'inProgress') {
    return Math.max(0, Math.floor((parseApiDate(item.attempt.deadline).getTime() - now.value) / 1000))
  }
  return 0
}

function startReason(item: CampPositionLearnerSummary) {
  if (item.attempt && item.attempt.status !== 'inProgress' && item.attempt.status !== 'graded') {
    return '本次测评已提交，评分完成后可查看结果'
  }
  if (!item.questionCount) return '该岗位尚未配置测评题，请联系管理员'
  return '暂不可进入测评'
}

function enter(item: CampPositionLearnerSummary) {
  emit('navigate', 'camp-taking', { positionId: item.id })
}

function openReport(attemptId: string) {
  activeAttemptId.value = attemptId
  reportVisible.value = true
}

function openRecord(attemptId: string) {
  activeAttemptId.value = attemptId
  recordVisible.value = true
}

async function load(silent = false) {
  if (silent && (fetching || document.hidden)) return
  const request = ++requestId
  fetching = true
  if (!silent) {
    loading.value = true
    error.value = ''
  }
  try {
    const data = await api<{ items?: CampPositionLearnerSummary[] }>('/camp/positions')
    if (request !== requestId) return
    items.value = data.items || []
    now.value = Date.now()
    error.value = ''
  } catch (failure) {
    if (request === requestId && !silent) error.value = messageOf(failure)
  } finally {
    if (request === requestId) {
      fetching = false
      loading.value = false
    }
  }
}

onMounted(() => {
  void load()
})

onBeforeUnmount(() => {
  ++requestId
})
</script>

<template>
  <section class="page-heading learner-catalog-heading">
    <h1>岗位训练营</h1>
    <el-button :icon="Refresh" :loading="loading" @click="load()">刷新岗位</el-button>
  </section>

  <div class="surface student-filters" role="search" aria-label="查找测评">
    <el-input
      v-model="search"
      class="search-box"
      clearable
      placeholder="搜索岗位名称或描述"
      aria-label="搜索岗位名称或描述"
      :prefix-icon="Search"
    />
    <span class="record-count">共 {{ filtered.length }} 个岗位</span>
  </div>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />

  <div v-loading="loading" :aria-busy="loading" class="assessment-collections">
    <section v-for="section in grouped" :key="section.key" :aria-labelledby="'camp-group-' + section.key">
      <header class="assessment-group-head">
        <h2 :id="'camp-group-' + section.key">{{ section.title }}</h2>
        <span>{{ section.positions.length }} 个</span>
      </header>
      <div class="assessment-grid">
        <article
          v-for="item in section.positions"
          :key="item.id"
          class="surface assessment-card"
          :class="{ 'is-resumable': section.key === 'resume' }"
        >
          <div class="assessment-card-top">
            <el-tag v-if="item.attempt" :type="campAttemptStatusType(item.attempt.status)" size="small" effect="plain">
              {{ campAttemptLabel(item.attempt.status) }}
            </el-tag>
            <el-tag v-else size="small" type="info" effect="plain">未开始</el-tag>
            <span
              v-if="item.attempt?.status === 'inProgress'"
              class="assessment-time"
              :class="{ urgent: remaining(item) < 300 }"
            >
              <el-icon><Clock /></el-icon>剩余 {{ countdownText(remaining(item)) }}
            </span>
            <span v-else class="assessment-time"
              ><el-icon><Clock /></el-icon>{{ durationText(item.durationMinutes) }}</span
            >
          </div>
          <h3>{{ item.name }}</h3>
          <p v-if="item.description" class="assessment-description">{{ item.description }}</p>
          <div class="camp-stats">
            <span
              ><strong>{{ item.skillCount }}</strong> 技能维度</span
            >
            <span
              ><strong>{{ item.questionCount }}</strong> 测评题</span
            >
            <span
              ><strong>{{ item.maxScore }}</strong> 满分</span
            >
          </div>
          <div v-if="item.attempt?.status === 'graded'" class="camp-result">
            <el-icon><Trophy /></el-icon>
            <div>
              <strong>最近得分 {{ item.attempt.totalScore }} / {{ item.attempt.maxScore }}</strong>
              <span v-if="item.completedCount > 1">已完成 {{ item.completedCount }} 次</span>
              <span v-else-if="item.attempt.submittedAt">{{ dateTime(item.attempt.submittedAt) }}</span>
            </div>
          </div>
          <div class="assessment-card-foot">
            <span class="assessment-note">实操题 {{ item.practicalCount }} 道</span>
            <div class="camp-actions">
              <template v-if="item.attempt?.status === 'graded'">
                <el-button link type="primary" @click="openReport(item.attempt.id)">能力报告</el-button>
                <el-button link @click="openRecord(item.attempt.id)">作答记录</el-button>
                <el-button v-if="item.canStart" type="primary" @click="enter(item)">再次测评</el-button>
              </template>
              <el-button v-else-if="item.canStart" type="primary" @click="enter(item)">
                {{ item.attempt?.status === 'inProgress' ? '继续测评' : '开始测评' }}
              </el-button>
              <span v-else class="assessment-note">{{ startReason(item) }}</span>
            </div>
          </div>
        </article>
      </div>
    </section>
  </div>

  <el-empty
    v-if="!loading && !error && !filtered.length"
    :description="items.length ? '没有符合条件的岗位测评' : '暂无可参加的岗位测评'"
  >
    <el-button v-if="items.length" @click="search = ''">清除搜索</el-button>
    <p v-else class="empty-hint">暂无已发布的岗位测评。岗位由平台管理员发布后才会出现在这里，如有需要请联系管理员。</p>
  </el-empty>

  <CampReportDrawer v-model="reportVisible" :attempt-id="activeAttemptId" />
  <CampRecordDrawer v-model="recordVisible" :attempt-id="activeAttemptId" />
</template>

<style scoped>
.camp-stats {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 6px 16px;
  padding: 10px 0;
  color: var(--text-muted);
  font-size: 12px;
}

.camp-stats strong {
  margin-right: 2px;
  color: #213e53;
  font-size: 15px;
}

.camp-result {
  display: flex;
  align-items: center;
  gap: 9px;
  margin: 0 0 12px;
  padding: 9px 11px;
  border-radius: 8px;
  background: #eaf5f0;
  color: #236449;
  font-size: 13px;
}

.camp-result div {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 3px 10px;
}

.camp-result span {
  color: #466961;
  font-size: 12px;
}

.camp-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 5px 10px;
  margin-left: auto;
}

.camp-actions :deep(.el-button + .el-button) {
  margin-left: 0;
}

.empty-hint {
  max-width: 28ch;
  margin: 8px auto 0;
  color: #56697c;
  font-size: 13px;
  line-height: 1.6;
}
</style>

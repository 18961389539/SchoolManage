<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { Clock, Document, Monitor, Refresh, Search, Tickets } from '@element-plus/icons-vue'
import { api, messageOf } from '../api'
import {
  attemptStatusLabel,
  attemptStatusType,
  countdownText,
  deploymentLabel,
  deploymentType,
  examStatusLabel,
  examStatusOptions,
  examStatusType
} from '../exam'
import { durationText } from '../teaching'
import { parseApiDate, rangeText } from '../time'
import type { ExamHallItem } from '../types'

const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()

const items = ref<ExamHallItem[]>([])
const loading = ref(false)
const error = ref('')
const search = ref('')
const statusFilter = ref('')
const now = ref(Date.now())
let ticker: number | undefined
let refresher: number | undefined
let requestId = 0
let fetching = false

const sections = [
  { key: 'resume', title: '正在作答' },
  { key: 'ready', title: '可进入考试' },
  { key: 'scheduled', title: '即将开始' },
  { key: 'waiting', title: '等待实操环境' },
  { key: 'past', title: '已交卷与结束' }
] as const

function sectionOf(item: ExamHallItem): (typeof sections)[number]['key'] {
  if (item.status === 'open' && item.attemptStatus === 'inProgress') return 'resume'
  if (item.canStart) return 'ready'
  if (item.status === 'scheduled') return 'scheduled'
  if (item.status === 'open' && item.attemptStatus === 'notStarted') return 'waiting'
  return 'past'
}

const filtered = computed(() => {
  const keyword = search.value.trim().toLowerCase()
  return items.value.filter(item => {
    if (statusFilter.value && item.status !== statusFilter.value) return false
    if (!keyword) return true
    return [item.name, item.description, item.paperName].some(value => value.toLowerCase().includes(keyword))
  })
})

const grouped = computed(() =>
  sections
    .map(section => {
      const exams = filtered.value.filter(item => sectionOf(item) === section.key)
      if (section.key === 'resume' || section.key === 'ready' || section.key === 'scheduled') {
        function relevantTime(item: ExamHallItem) {
          if (section.key === 'resume') return parseApiDate(item.deadline || item.endAt).getTime()
          return parseApiDate(section.key === 'scheduled' ? item.startAt : item.endAt).getTime()
        }
        exams.sort((left, right) => relevantTime(left) - relevantTime(right))
      }
      return { ...section, exams }
    })
    .filter(section => section.exams.length)
)

function clearFilters() {
  search.value = ''
  statusFilter.value = ''
}

const filtering = computed(() => !!search.value.trim() || !!statusFilter.value)

function remainingSeconds(item: ExamHallItem): number {
  if (item.attemptStatus === 'inProgress' && item.deadline) {
    return Math.max(0, Math.floor((parseApiDate(item.deadline).getTime() - now.value) / 1000))
  }
  if (item.status === 'open') return Math.max(0, Math.floor((parseApiDate(item.endAt).getTime() - now.value) / 1000))
  return 0
}

function showsCountdown(item: ExamHallItem): boolean {
  return item.status === 'open' && (item.attemptStatus === 'inProgress' || item.attemptStatus === 'notStarted')
}

function countdownLabel(item: ExamHallItem): string {
  const label = item.attemptStatus === 'inProgress' ? '作答剩余' : '距结束'
  return `${label} ${countdownText(remainingSeconds(item))}`
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
    const data = await api<{ items: ExamHallItem[] }>('/exam-hall')
    if (request !== requestId) return
    items.value = data.items
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

function enterExam(item: ExamHallItem) {
  emit('navigate', 'exam-taking', { examId: item.id })
}

function viewRecord(item: ExamHallItem) {
  emit('navigate', 'exam-records', { search: item.name })
}

function unavailableText(item: ExamHallItem): string {
  if (item.attemptStatus === 'submitted') return '等待教师批阅'
  if (item.attemptStatus === 'graded') return '考试结束后开放复盘'
  if (item.attemptStatus === 'inProgress') return '等待系统结算'
  if (item.status === 'scheduled') return '开考后可进入作答'
  if (item.status === 'open' && item.deploymentStatus === 'failed') return '实操环境部署失败，请联系教师'
  if (item.status === 'open') return '等待实操环境'
  return '未参加考试'
}

function scoreText(item: ExamHallItem): string {
  if (item.attemptScore === null || item.attemptScore === undefined) return ''
  return `${item.attemptScore} / ${item.maxScore ?? item.totalScore}`
}

onMounted(() => {
  void load()
  ticker = window.setInterval(() => {
    now.value = Date.now()
  }, 1000)
  refresher = window.setInterval(() => void load(true), 30000)
})

onBeforeUnmount(() => {
  ++requestId
  if (ticker !== undefined) window.clearInterval(ticker)
  if (refresher !== undefined) window.clearInterval(refresher)
})
</script>

<template>
  <section class="page-heading learner-catalog-heading">
    <h1>我的考试</h1>
    <el-button :icon="Refresh" :loading="loading" @click="load()">刷新考试</el-button>
  </section>

  <div class="surface student-filters" role="search" aria-label="查找考试">
    <el-input
      v-model="search"
      class="search-box"
      clearable
      placeholder="搜索考试或试卷"
      aria-label="搜索考试或试卷"
      :prefix-icon="Search"
    />
    <el-select v-model="statusFilter" class="table-filter" aria-label="考试状态" placeholder="全部状态" clearable>
      <el-option v-for="option in examStatusOptions" :key="option.value" :label="option.label" :value="option.value" />
    </el-select>
    <el-button v-if="filtering" @click="clearFilters">清除筛选</el-button>
    <span class="record-count">{{
      loading && !items.length ? '正在加载考试…' : '共 ' + filtered.length + ' 场考试'
    }}</span>
  </div>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />

  <div v-loading="loading" :aria-busy="loading" class="assessment-collections">
    <section v-for="section in grouped" :key="section.key" :aria-labelledby="'exam-group-' + section.key">
      <header class="assessment-group-head">
        <h2 :id="'exam-group-' + section.key">{{ section.title }}</h2>
        <span>{{ section.exams.length }} 场</span>
      </header>
      <div class="assessment-grid">
        <article
          v-for="item in section.exams"
          :key="item.id"
          class="surface assessment-card"
          :class="{ 'is-resumable': section.key === 'resume' }"
        >
          <div class="assessment-card-top">
            <div class="exam-tags">
              <el-tag v-if="item.attemptStatus === 'notStarted'" :type="examStatusType(item.status)" size="small">
                {{ examStatusLabel(item.status) }}
              </el-tag>
              <el-tag v-else :type="attemptStatusType(item.attemptStatus)" size="small" effect="plain">
                {{ attemptStatusLabel(item.attemptStatus) }}
              </el-tag>
              <el-tag
                v-if="item.deploymentStatus !== 'notRequired'"
                :type="deploymentType(item.deploymentStatus)"
                size="small"
                effect="plain"
              >
                {{ deploymentLabel(item.deploymentStatus) }}
              </el-tag>
            </div>
            <span v-if="showsCountdown(item)" class="assessment-time" :class="{ urgent: remainingSeconds(item) < 300 }">
              <el-icon><Clock /></el-icon>{{ countdownLabel(item) }}
            </span>
          </div>
          <h3>{{ item.name }}</h3>
          <p v-if="item.description" class="assessment-description">{{ item.description }}</p>
          <dl class="exam-meta">
            <div>
              <dt>
                <el-icon><Document /></el-icon>试卷
              </dt>
              <dd>{{ item.paperName }} · {{ item.questionCount }} 题 / {{ item.totalScore }} 分</dd>
            </div>
            <div>
              <dt>
                <el-icon><Clock /></el-icon>考试时间
              </dt>
              <dd>{{ rangeText(item.startAt, item.endAt) }}</dd>
            </div>
            <div>
              <dt>
                <el-icon><Tickets /></el-icon>考试时长
              </dt>
              <dd>{{ durationText(item.durationMinutes) }}</dd>
            </div>
            <div v-if="item.deploymentMessage">
              <dt>
                <el-icon><Monitor /></el-icon>考试环境
              </dt>
              <dd>{{ item.deploymentMessage }}</dd>
            </div>
            <div v-if="item.attemptStatus === 'graded' && scoreText(item)">
              <dt>
                <el-icon><Tickets /></el-icon>当前成绩
              </dt>
              <dd class="exam-score">{{ scoreText(item) }}</dd>
            </div>
          </dl>
          <div class="assessment-card-foot">
            <span v-if="item.autoSubmitted" class="assessment-note">超时自动交卷</span>
            <span v-if="!item.canStart && !item.canReview" class="assessment-note">{{ unavailableText(item) }}</span>
            <el-button v-if="item.canStart" type="primary" @click="enterExam(item)">
              {{ item.attemptStatus === 'inProgress' ? '继续作答' : '进入考试' }}
            </el-button>
            <el-button v-else-if="item.canReview" type="primary" plain @click="viewRecord(item)">查看成绩</el-button>
          </div>
        </article>
      </div>
    </section>
  </div>

  <el-empty
    v-if="!loading && !error && !filtered.length"
    :description="filtering ? '没有符合条件的考试' : '暂时没有安排给你的考试'"
  >
    <el-button v-if="filtering" @click="clearFilters">清除筛选</el-button>
    <p v-else class="empty-hint">请联系授课教师，确认考试已经发布并分配给你。</p>
  </el-empty>
</template>

<style scoped>
.exam-tags {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.exam-meta {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 235px), 1fr));
  gap: 10px 18px;
  margin: 0 0 12px;
  padding: 12px;
  border: 1px solid #e4eeea;
  border-radius: 8px;
  background: #f5f9f8;
}

.exam-meta > div {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  align-items: start;
  gap: 8px;
}

.exam-meta dt {
  display: flex;
  align-items: center;
  gap: 5px;
  color: var(--text-muted);
  font-size: 12px;
}

.exam-meta dd {
  margin: 0;
  color: #2f4c63;
  font-size: 13px;
  line-height: 1.5;
  text-align: right;
  overflow-wrap: anywhere;
}

.exam-meta .exam-score {
  color: var(--workspace-accent);
  font-size: 15px;
  font-weight: 650;
  font-variant-numeric: tabular-nums;
}

.empty-hint {
  max-width: 28ch;
  margin: 8px auto 0;
  color: #56697c;
  font-size: 13px;
  line-height: 1.6;
}
</style>

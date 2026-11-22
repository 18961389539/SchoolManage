<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ArrowLeft, Clock, Refresh, Search } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import { campAttemptLabel, campAttemptStatusType } from '../camp'
import { countdownText } from '../exam'
import { parseApiDate } from '../time'
import type { CampHistoryRow } from '../types'
import CampRecordDrawer from './CampRecordDrawer.vue'
import CampReportDrawer from './CampReportDrawer.vue'

const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()

const items = ref<CampHistoryRow[]>([])
const loading = ref(false)
const error = ref('')
const search = ref('')
const appliedSearch = ref('')
const now = ref(Date.now())
const reportVisible = ref(false)
const recordVisible = ref(false)
const activeAttemptId = ref('')
let ticker: number | undefined
let refresher: number | undefined
let searchTimer: number | undefined
let requestId = 0
let fetching = false

const gradedCount = computed(() => items.value.filter(item => item.status === 'graded').length)
const ordered = computed(() => [
  ...items.value.filter(item => item.status === 'inProgress'),
  ...items.value.filter(item => item.status === 'graded')
])

function remaining(row: CampHistoryRow): number {
  if (row.status !== 'inProgress') return 0
  return Math.max(0, Math.floor((parseApiDate(row.deadline).getTime() - now.value) / 1000))
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
    const term = appliedSearch.value ? `?search=${encodeURIComponent(appliedSearch.value)}` : ''
    const data = await api<{ items: CampHistoryRow[] }>(`/camp/history${term}`)
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

function applySearch() {
  window.clearTimeout(searchTimer)
  appliedSearch.value = search.value.trim()
  void load()
}

function resetSearch() {
  search.value = ''
  applySearch()
}

function openReport(row: CampHistoryRow) {
  activeAttemptId.value = row.attemptId
  reportVisible.value = true
}

function openRecord(row: CampHistoryRow) {
  activeAttemptId.value = row.attemptId
  recordVisible.value = true
}

watch(search, value => {
  window.clearTimeout(searchTimer)
  if (value.trim() !== appliedSearch.value) searchTimer = window.setTimeout(applySearch, 350)
})

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
  if (searchTimer !== undefined) window.clearTimeout(searchTimer)
})
</script>

<template>
  <section class="page-heading learner-catalog-heading history-heading">
    <div>
      <el-button link :icon="ArrowLeft" class="history-back" @click="emit('navigate', 'my-camp')">
        返回岗位训练营
      </el-button>
      <h1>测评历史</h1>
    </div>
    <el-button :icon="Refresh" :loading="loading" @click="load()">刷新历史</el-button>
  </section>

  <form class="surface student-filters" role="search" aria-label="查找测评历史" @submit.prevent="applySearch">
    <el-input
      v-model="search"
      class="search-box"
      clearable
      placeholder="搜索岗位名称"
      aria-label="搜索岗位名称"
      :prefix-icon="Search"
      @clear="resetSearch"
    />
    <div class="record-count learner-record-count">
      <span>{{ items.length }} 次测评记录</span>
      <span v-if="gradedCount">已完成 {{ gradedCount }} 次</span>
    </div>
  </form>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />

  <div v-loading="loading" class="learner-records" :aria-busy="loading">
    <el-empty
      v-if="!loading && !error && !items.length"
      :description="appliedSearch ? '没有匹配的测评记录' : '开始测评后，记录会显示在这里'"
    >
      <el-button v-if="appliedSearch" @click="resetSearch">清除搜索</el-button>
    </el-empty>

    <article
      v-for="row in ordered"
      :key="row.attemptId"
      class="surface learner-record"
      :class="{ 'is-active': row.status === 'inProgress' }"
    >
      <div class="learner-record-main">
        <header class="learner-record-heading">
          <h2>{{ row.positionName }}</h2>
          <div class="learner-record-tags">
            <el-tag :type="campAttemptStatusType(row.status)" size="small">{{ campAttemptLabel(row.status) }}</el-tag>
            <el-tag v-if="row.autoSubmitted" size="small" type="warning" effect="plain">超时自动提交</el-tag>
          </div>
        </header>
        <dl class="learner-record-dates">
          <div>
            <dt>开始测评</dt>
            <dd>{{ dateTime(row.startedAt) }}</dd>
          </div>
          <div v-if="row.submittedAt">
            <dt>提交时间</dt>
            <dd>{{ dateTime(row.submittedAt) }}</dd>
          </div>
          <div v-else>
            <dt>作答截止</dt>
            <dd>{{ dateTime(row.deadline) }}</dd>
          </div>
        </dl>
      </div>
      <div class="learner-record-aside">
        <div v-if="row.status === 'inProgress'">
          <span class="learner-record-label">作答剩余</span>
          <strong class="learner-record-score" role="timer">
            <el-icon aria-hidden="true"><Clock /></el-icon>{{ countdownText(remaining(row)) }}
          </strong>
        </div>
        <div v-else>
          <span class="learner-record-label">测评得分</span>
          <strong class="learner-record-score"
            >{{ row.totalScore }} <small>/ {{ row.maxScore }}</small></strong
          >
        </div>
        <div class="learner-record-actions">
          <el-button
            v-if="row.status === 'inProgress'"
            type="primary"
            @click="emit('navigate', 'camp-taking', { positionId: row.positionId })"
          >
            继续测评
          </el-button>
          <template v-else-if="row.hasReport">
            <el-button type="primary" plain @click="openReport(row)">能力报告</el-button>
            <el-button @click="openRecord(row)">作答记录</el-button>
          </template>
        </div>
      </div>
    </article>
  </div>

  <CampReportDrawer v-model="reportVisible" :attempt-id="activeAttemptId" />
  <CampRecordDrawer v-model="recordVisible" :attempt-id="activeAttemptId" />
</template>

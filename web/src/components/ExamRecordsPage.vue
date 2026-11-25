<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { ArrowLeft, Refresh, Search } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import { readListState, useListPersist } from '../listState'
import { recoverPage } from '../paging'
import { attemptStatusLabel, attemptStatusType } from '../exam'
import { usePolling } from '../usePolling'
import type { ExamRecordSummary, Paged } from '../types'
import AttemptReviewDrawer from './AttemptReviewDrawer.vue'

const props = defineProps<{ initialSearch?: string }>()
const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()

const loading = ref(false)
const error = ref('')
const items = ref<ExamRecordSummary[]>([])
const total = ref(0)
const listKey = 'exam-records'
const savedList = readListState(listKey, sessionStorage, [10, 20, 50, 100], 10)
const page = ref(props.initialSearch ? 1 : savedList.page)
const pageSize = ref(props.initialSearch ? 10 : savedList.pageSize)
const search = ref(props.initialSearch || savedList.search)
const appliedSearch = ref(search.value.trim())
usePolling(() => load(true), { intervalMs: 30000, clockMs: 0 })
let requestId = 0
let fetching = false

const reviewVisible = ref(false)
const reviewEndpoint = ref('')
const reviewTitle = ref('')

const pendingCount = computed(() => items.value.filter(item => item.status === 'submitted').length)

async function load(silent = false) {
  if (silent && (fetching || document.hidden)) return
  const request = ++requestId
  fetching = true
  if (!silent) {
    loading.value = true
    error.value = ''
  }
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSize.value) })
    if (appliedSearch.value) params.set('search', appliedSearch.value)
    const data = await api<Paged<ExamRecordSummary>>(`/exam-records?${params.toString()}`)
    if (request === requestId) {
      items.value = data.items
      total.value = data.total
      error.value = ''
      const recovered = recoverPage(page.value, pageSize.value, total.value, items.value.length)
      if (recovered !== null && recovered !== page.value) {
        page.value = recovered
        void load(silent)
      }
    }
  } catch (failure) {
    if (request === requestId && !silent) error.value = messageOf(failure)
  } finally {
    if (request === requestId) {
      fetching = false
      loading.value = false
    }
  }
}

useListPersist(listKey, () => ({
  search: appliedSearch.value,
  filters: {},
  page: page.value,
  pageSize: pageSize.value
}))

function applySearch() {
  appliedSearch.value = search.value.trim()
  page.value = 1
  void load()
}

function resetSearch() {
  search.value = ''
  applySearch()
}

function scorePercent(record: ExamRecordSummary) {
  return record.maxScore ? Math.round((record.totalScore * 100) / record.maxScore) : 0
}

function openReview(record: ExamRecordSummary) {
  reviewEndpoint.value = `/exam-records/${record.attemptId}`
  reviewTitle.value = `考试复盘 · ${record.examName}`
  reviewVisible.value = true
}

onMounted(() => {
  void load()
})
onUnmounted(() => {
  ++requestId
})
</script>

<template>
  <section class="page-heading learner-catalog-heading history-heading">
    <div>
      <el-button link :icon="ArrowLeft" class="history-back" @click="emit('navigate', 'my-exams')">
        返回我的考试
      </el-button>
      <h1>考试记录</h1>
      <p>这里是交卷后的作答记录与复盘，进行中的考试请到「我的考试」。</p>
    </div>
    <el-button :icon="Refresh" :loading="loading" @click="load()">刷新记录</el-button>
  </section>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />

  <form class="surface student-filters" role="search" aria-label="查找考试记录" @submit.prevent="applySearch">
    <el-input
      v-model="search"
      class="search-box"
      clearable
      placeholder="搜索考试名称"
      aria-label="搜索考试名称"
      :prefix-icon="Search"
      @clear="resetSearch"
    />
    <el-button native-type="submit" type="primary">搜索</el-button>
    <div class="record-count learner-record-count">
      <span>{{ total }} 次考试记录</span>
      <span v-if="pendingCount">本页 {{ pendingCount }} 次待批阅</span>
    </div>
  </form>

  <div v-loading="loading" class="learner-records" :aria-busy="loading">
    <el-empty
      v-if="!loading && !error && !items.length"
      :description="appliedSearch ? '没有匹配的考试记录' : '提交考试后，作答记录会显示在这里'"
    >
      <el-button v-if="appliedSearch" @click="resetSearch">清除搜索</el-button>
    </el-empty>

    <article v-for="record in items" :key="record.attemptId" class="surface learner-record">
      <div class="learner-record-main">
        <header class="learner-record-heading">
          <h2>{{ record.examName }}</h2>
          <div class="learner-record-tags">
            <el-tag size="small" :type="attemptStatusType(record.status)">{{
              attemptStatusLabel(record.status)
            }}</el-tag>
            <el-tag v-if="record.autoSubmitted" size="small" type="warning" effect="plain">自动交卷</el-tag>
          </div>
        </header>
        <p class="learner-record-context">{{ record.paperName }} · {{ record.questionCount }} 题</p>
        <dl class="learner-record-dates">
          <div>
            <dt>开始作答</dt>
            <dd>{{ dateTime(record.startedAt) }}</dd>
          </div>
          <div>
            <dt>提交时间</dt>
            <dd>{{ dateTime(record.submittedAt) }}</dd>
          </div>
          <div v-if="record.gradedAt">
            <dt>批阅时间</dt>
            <dd>{{ dateTime(record.gradedAt) }}</dd>
          </div>
        </dl>
        <dl class="learner-record-parts">
          <div>
            <dt>客观分</dt>
            <dd>{{ record.objectiveScore }}</dd>
          </div>
          <div>
            <dt>实操分</dt>
            <dd v-if="record.status === 'submitted'">
              <template v-if="record.practicalScore > 0">{{ record.practicalScore }} · </template>待批阅
            </dd>
            <dd v-else>{{ record.practicalScore }}</dd>
          </div>
        </dl>
      </div>
      <div class="learner-record-aside">
        <div>
          <span class="learner-record-label">{{ record.status === 'submitted' ? '暂定得分' : '总分' }}</span>
          <span v-if="record.status === 'submitted'" class="learner-record-pending">待教师批阅，不是最终成绩</span>
          <strong class="learner-record-score"
            >{{ record.totalScore }} <small>/ {{ record.maxScore }}</small></strong
          >
          <span v-if="record.status === 'graded'" class="learner-record-label">得分率 {{ scorePercent(record) }}%</span>
        </div>
        <div class="learner-record-actions">
          <el-button v-if="record.canReview" type="primary" plain @click="openReview(record)">查看复盘</el-button>
          <span v-else class="learner-record-label">考试结束并出分后开放复盘</span>
        </div>
      </div>
    </article>
  </div>

  <div v-if="total > pageSize" class="records-pagination">
    <el-pagination
      v-model:current-page="page"
      v-model:page-size="pageSize"
      :total="total"
      :page-sizes="[10, 20, 50]"
      :pager-count="5"
      layout="total, sizes, prev, pager, next"
      @current-change="load()"
      @size-change="applySearch"
    />
  </div>

  <AttemptReviewDrawer v-model="reviewVisible" :endpoint="reviewEndpoint" :title="reviewTitle" />
</template>

<style scoped>
.learner-record-pending {
  display: block;
  margin-top: 4px;
  color: #b8821a;
  font-size: 12px;
  line-height: 1.4;
}

.records-pagination {
  display: flex;
  justify-content: flex-end;
  margin-top: 16px;
}

@media (max-width: 620px) {
  .records-pagination {
    justify-content: center;
  }

  .records-pagination :deep(.el-pagination__sizes) {
    display: none;
  }
}
</style>

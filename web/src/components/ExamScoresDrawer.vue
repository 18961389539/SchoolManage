<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Refresh, Search, View } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import { attemptStatusLabel, attemptStatusType } from '../exam'
import type { AttemptStatus, ExamScoreRow, ExamTypeScore, Paged } from '../types'
import AttemptReviewDrawer from './AttemptReviewDrawer.vue'

const props = defineProps<{ modelValue: boolean; examId: string; examName: string }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; changed: [] }>()

const visible = computed({
  get: () => props.modelValue,
  set: value => emit('update:modelValue', value)
})

const items = ref<ExamScoreRow[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)
const loading = ref(false)
const error = ref('')
const search = ref('')
const appliedSearch = ref('')
const statusFilter = ref('')
let requestId = 0

const reviewVisible = ref(false)
const reviewEndpoint = ref('')
const reviewGradePath = ref('')
const reviewTitle = ref('')

async function load() {
  if (!props.examId) return
  const request = ++requestId
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSize.value) })
    if (appliedSearch.value) params.set('search', appliedSearch.value)
    if (statusFilter.value) params.set('status', statusFilter.value)
    const data = await api<Paged<ExamScoreRow>>(`/exams/${props.examId}/scores?${params.toString()}`)
    if (request !== requestId) return
    items.value = data.items
    total.value = data.total
  } catch (failure) {
    if (request === requestId) error.value = messageOf(failure)
  } finally {
    if (request === requestId) loading.value = false
  }
}

function applyFilters() {
  page.value = 1
  appliedSearch.value = search.value.trim()
  void load()
}

function togglePending() {
  statusFilter.value = statusFilter.value === 'submitted' ? '' : 'submitted'
  applyFilters()
}

function resetFilters() {
  search.value = ''
  statusFilter.value = ''
  applyFilters()
}

function scoreFor(score: ExamTypeScore, status: AttemptStatus) {
  const result = `${score.score} / ${score.maxScore}`
  if (score.type !== 'practical' || status !== 'submitted') return result
  return score.score > 0 ? `${result} · 待批阅` : '待批阅'
}

function elapsedText(seconds: number | null) {
  if (seconds === null) return '—'
  const minutes = Math.floor(seconds / 60)
  return minutes ? `${minutes} 分 ${seconds % 60} 秒` : `${seconds} 秒`
}

function openReview(row: ExamScoreRow) {
  if (!row.attemptId) return
  reviewEndpoint.value = `/exams/${props.examId}/attempts/${row.attemptId}`
  reviewGradePath.value = `/exams/${props.examId}/attempts/${row.attemptId}/grade`
  reviewTitle.value = `答题批阅 · ${row.name}`
  reviewVisible.value = true
}

async function onGraded() {
  await load()
  emit('changed')
}

watch(
  () => [props.modelValue, props.examId] as const,
  ([open, examId], previous) => {
    if (previous?.[1] !== examId) {
      items.value = []
      total.value = 0
      search.value = ''
      appliedSearch.value = ''
      statusFilter.value = ''
      error.value = ''
    }
    if (!open) {
      ++requestId
      loading.value = false
      return
    }
    page.value = 1
    void load()
  },
  { immediate: true }
)
</script>

<template>
  <el-drawer v-model="visible" :title="`考试成绩 · ${examName}`" size="min(980px, 96vw)">
    <form
      class="surface student-filters score-toolbar"
      role="search"
      aria-label="查找考试成绩"
      @submit.prevent="applyFilters"
    >
      <el-input
        v-model="search"
        class="search-box"
        clearable
        placeholder="搜索姓名、账号或学号"
        aria-label="搜索姓名、账号或学号"
        :prefix-icon="Search"
        @clear="applyFilters"
      />
      <el-select
        v-model="statusFilter"
        class="table-filter"
        placeholder="全部状态"
        aria-label="批阅状态"
        clearable
        @change="applyFilters"
      >
        <el-option label="未参加" value="notStarted" />
        <el-option label="作答中" value="inProgress" />
        <el-option label="待批阅" value="submitted" />
        <el-option label="已批阅" value="graded" />
      </el-select>
      <el-button native-type="submit" type="primary" :icon="Search">查询</el-button>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      <div class="score-toolbar-summary">
        <el-button
          :type="statusFilter === 'submitted' ? 'warning' : 'default'"
          :aria-pressed="statusFilter === 'submitted'"
          plain
          @click="togglePending"
        >
          仅看待批阅
        </el-button>
        <span class="record-count">共 {{ total }} 条成绩</span>
      </div>
    </form>

    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />

    <div v-loading="loading" class="score-list" :aria-busy="loading">
      <el-empty
        v-if="!loading && !error && !items.length"
        :description="appliedSearch || statusFilter ? '没有符合条件的成绩' : '暂无成绩数据'"
      >
        <el-button v-if="appliedSearch || statusFilter" @click="resetFilters">重置筛选</el-button>
      </el-empty>
      <article
        v-for="row in items"
        :key="row.studentUserId"
        class="surface score-row"
        :class="{ 'is-pending': row.status === 'submitted' }"
      >
        <div class="score-identity">
          <h2>{{ row.name }}</h2>
          <p>{{ row.className || '未分班' }}</p>
          <p>
            {{ row.userName }}<template v-if="row.number"> · {{ row.number }}</template>
          </p>
        </div>

        <div class="score-result">
          <div class="score-labels">
            <el-tag :type="attemptStatusType(row.status)" size="small">{{ attemptStatusLabel(row.status) }}</el-tag>
            <el-tag v-if="row.autoSubmitted" size="small" type="warning" effect="plain">超时交卷</el-tag>
          </div>
          <template v-if="row.submittedAt">
            <div class="score-number">
              <span>{{ row.status === 'submitted' ? '当前得分' : '总分' }}</span>
              <strong
                >{{ row.totalScore }} <small>/ {{ row.maxScore }}</small></strong
              >
            </div>
            <div class="score-time">{{ dateTime(row.submittedAt) }} 提交</div>
            <div v-if="row.elapsedSeconds !== null" class="score-time">用时 {{ elapsedText(row.elapsedSeconds) }}</div>
          </template>
        </div>

        <dl v-if="row.submittedAt && row.typeScores.length" class="score-breakdown" aria-label="各题型得分">
          <div v-for="score in row.typeScores" :key="score.type">
            <dt>{{ score.label }}</dt>
            <dd>{{ scoreFor(score, row.status) }}</dd>
          </div>
        </dl>

        <div class="score-action">
          <el-button
            v-if="row.attemptId"
            type="primary"
            :plain="row.status !== 'submitted'"
            :icon="View"
            @click="openReview(row)"
          >
            {{ row.status === 'submitted' ? '批阅' : '查看' }}
          </el-button>
          <span v-else class="score-time">未参加</span>
        </div>
      </article>
    </div>
    <div v-if="total" class="table-footer">
      <el-pagination
        v-model:current-page="page"
        v-model:page-size="pageSize"
        :total="total"
        :page-sizes="[10, 20, 50]"
        :pager-count="5"
        layout="total, sizes, prev, pager, next"
        @current-change="load"
        @size-change="applyFilters"
      />
    </div>

    <AttemptReviewDrawer
      v-model="reviewVisible"
      :endpoint="reviewEndpoint"
      :grade-path="reviewGradePath"
      :title="reviewTitle"
      @graded="onGraded"
    />
  </el-drawer>
</template>

<style scoped>
.score-toolbar {
  padding: 10px 12px;
}

.score-toolbar-summary {
  display: flex;
  flex: 1 1 100%;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding-top: 9px;
  border-top: 1px solid #e4eeea;
}

.score-list {
  display: grid;
  gap: 9px;
}

.score-list[aria-busy='true'] {
  min-height: 120px;
}

.score-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) minmax(0, 1.2fr) auto;
  align-items: center;
  padding: 13px;
  gap: 10px 14px;
}

.score-row.is-pending {
  border-color: #e4c890;
}

.score-identity h2 {
  margin: 0 0 4px;
  color: #233c50;
  font-size: 16px;
}

.score-identity h2,
.score-identity p {
  overflow-wrap: anywhere;
}

.score-identity p,
.score-time {
  margin: 2px 0 0;
  color: var(--text-muted);
  font-size: 12px;
  line-height: 1.5;
}

.score-labels {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.score-number {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: 5px;
  margin: 5px 0 3px;
  font-size: 12px;
  color: var(--text-muted);
}

.score-number strong {
  color: #1f514c;
  font-size: 18px;
  font-variant-numeric: tabular-nums;
}

.score-number small {
  color: var(--text-muted);
  font-size: 12px;
  font-weight: 500;
}

.score-breakdown {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-start;
  gap: 5px;
  margin: 0;
}

.score-breakdown > div {
  padding: 5px 7px;
  border-radius: 6px;
  background: #eef6f4;
  font-size: 12px;
}

.score-breakdown dt {
  color: var(--text-muted);
}

.score-breakdown dd {
  margin: 2px 0 0;
  color: #254a60;
  font-weight: 600;
}

.score-action {
  grid-column: 4;
  justify-self: end;
}

.table-footer {
  justify-content: flex-end;
  padding-inline: 0;
}

@media (max-width: 760px) {
  .score-row {
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
    padding: 11px;
  }

  .score-breakdown {
    grid-column: 1 / -1;
  }

  .score-action {
    grid-column: 2;
  }

  .score-breakdown + .score-action {
    grid-column: 1 / -1;
  }

  .score-toolbar > .table-filter {
    flex: 1 1 138px;
  }
}
</style>

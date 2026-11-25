<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { Refresh, Search } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import { readListState, useListPersist } from '../listState'
import { recoverPage } from '../paging'
import { useRequestGeneration } from '../requestGeneration'
import type { CampPositionLearnerSummary, CampStaffReportRow, Paged } from '../types'
import CampReportDrawer from './CampReportDrawer.vue'
import TableEmpty from './TableEmpty.vue'

const items = ref<CampStaffReportRow[]>([])
const total = ref(0)
const savedList = readListState('camp-reports')
const page = ref(savedList.page)
const pageSize = ref(savedList.pageSize)
const search = ref(savedList.search)
const loading = ref(false)
const error = ref('')
const listRequests = useRequestGeneration()
const reportVisible = ref(false)
const activeAttemptId = ref('')
const positions = ref<CampPositionLearnerSummary[]>([])
const positionsLoading = ref(false)
const positionsError = ref('')

async function loadPositions() {
  positionsLoading.value = true
  positionsError.value = ''
  try {
    const data = await api<{ items?: CampPositionLearnerSummary[] }>('/camp/positions')
    positions.value = data.items || []
  } catch (failure) {
    positionsError.value = messageOf(failure)
    positions.value = []
  } finally {
    positionsLoading.value = false
  }
}

async function load() {
  const token = listRequests.begin()
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSize.value) })
    if (search.value.trim()) params.set('search', search.value.trim())
    const data = await api<Paged<CampStaffReportRow>>(`/camp/reports?${params.toString()}`)
    if (!listRequests.isCurrent(token)) return
    items.value = data.items
    total.value = data.total
    const recovered = recoverPage(page.value, pageSize.value, total.value, items.value.length)
    if (recovered !== null && recovered !== page.value) {
      page.value = recovered
      return load()
    }
  } catch (failure) {
    if (listRequests.isCurrent(token)) error.value = messageOf(failure)
  } finally {
    if (listRequests.isCurrent(token)) loading.value = false
  }
}

useListPersist('camp-reports', () => ({
  search: search.value,
  filters: {},
  page: page.value,
  pageSize: pageSize.value
}))

function applySearch() {
  page.value = 1
  void load()
}

function resetSearch() {
  search.value = ''
  page.value = 1
  void load()
}

function openReport(row: CampStaffReportRow) {
  activeAttemptId.value = row.attemptId
  reportVisible.value = true
}

onMounted(() => {
  void load()
  void loadPositions()
})
</script>

<template>
  <section class="page-heading">
    <div>
      <h1>能力报告</h1>
      <p>查看已发布岗位（只读）以及学生岗位测评的成绩、技能维度表现与能力雷达。岗位由管理员发布，不按人单独分配。</p>
    </div>
    <el-button
      :icon="Refresh"
      :loading="loading || positionsLoading"
      @click="
        () => {
          void load()
          void loadPositions()
        }
      "
      >刷新</el-button
    >
  </section>

  <section class="surface camp-positions-catalog" v-loading="positionsLoading">
    <header class="card-heading">
      <div>
        <h2>已发布岗位</h2>
        <p>学生交卷前可在这里确认当前对外发布了哪些岗位。如需调整发布，请联系管理员。</p>
      </div>
    </header>
    <el-alert
      v-if="positionsError"
      :title="positionsError"
      type="error"
      show-icon
      :closable="false"
      class="section-notice"
    />
    <el-table v-else :data="positions" row-key="id" empty-text="">
      <template #empty>
        <TableEmpty
          title="当前没有已发布的岗位"
          hint="岗位需由管理员先发布。取消发布后，尚未开始的学生将不再看到该岗位。"
        />
      </template>
      <el-table-column prop="name" label="岗位" min-width="160" show-overflow-tooltip />
      <el-table-column prop="description" label="说明" min-width="220" show-overflow-tooltip />
      <el-table-column label="时长" width="100">
        <template #default="{ row }">{{ row.durationMinutes }} 分钟</template>
      </el-table-column>
      <el-table-column label="题目" width="160">
        <template #default="{ row }">{{ row.questionCount }} 题 · 实操 {{ row.practicalCount }}</template>
      </el-table-column>
      <el-table-column label="满分" width="90">
        <template #default="{ row }">{{ row.maxScore }}</template>
      </el-table-column>
    </el-table>
  </section>

  <section class="surface staff-toolbar">
    <span class="staff-summary"
      >共 <strong>{{ total }}</strong> 份能力报告</span
    >
    <div class="staff-search">
      <el-input
        v-model="search"
        clearable
        placeholder="搜索学生、学号或岗位"
        :prefix-icon="Search"
        @keyup.enter="applySearch"
        @clear="resetSearch"
      />
      <el-button type="primary" @click="applySearch">搜索</el-button>
    </div>
  </section>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />

  <section class="surface staff-table">
    <el-table v-loading="loading" :data="items" row-key="attemptId">
      <template #empty>
        <TableEmpty title="暂无能力报告" hint="岗位需由管理员先发布。学生提交岗位测评后，报告会显示在这里。" />
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
      <el-table-column label="成绩" width="150">
        <template #default="{ row }">
          <span class="report-score">{{ row.totalScore }} / {{ row.maxScore }}</span>
          <el-tag
            size="small"
            effect="plain"
            :type="row.percent >= 85 ? 'success' : row.percent >= 60 ? 'warning' : 'danger'"
          >
            {{ row.percent }}%
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="提交时间" min-width="160">
        <template #default="{ row }">{{ dateTime(row.submittedAt) }}</template>
      </el-table-column>
      <el-table-column label="批阅时间" min-width="160">
        <template #default="{ row }">{{ dateTime(row.gradedAt) }}</template>
      </el-table-column>
      <el-table-column label="操作" width="110" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="openReport(row as CampStaffReportRow)">查看报告</el-button>
        </template>
      </el-table-column>
    </el-table>
    <div class="staff-pager">
      <el-pagination
        v-model:current-page="page"
        v-model:page-size="pageSize"
        :total="total"
        :page-sizes="[10, 20, 50]"
        layout="total, sizes, prev, pager, next"
        background
        @current-change="load"
        @size-change="applySearch"
      />
    </div>
  </section>

  <CampReportDrawer v-model="reportVisible" :attempt-id="activeAttemptId" />
</template>

<style scoped>
.staff-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 12px;
  padding: 14px 18px;
  background: #f8fbfb;
  border-bottom: 1px solid #dce7eb;
}

.staff-summary {
  color: #56697c;
  font-size: 13px;
}

.staff-summary strong {
  color: #23405c;
  font-size: 16px;
}

.staff-search {
  display: flex;
  gap: 8px;
  width: min(380px, 100%);
}

.staff-table {
  padding: 4px 8px 12px;
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

.report-score {
  margin-right: 8px;
  color: #22685a;
  font-weight: 600;
}

.camp-positions-catalog {
  margin-bottom: 16px;
  padding-bottom: 12px;
}

.staff-pager {
  display: flex;
  justify-content: flex-end;
  padding: 12px 8px 4px;
}
</style>

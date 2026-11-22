<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { Clock, Refresh, Search, School, User } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import {
  classroomResourceCounts,
  classroomStatusOptions,
  classroomStatusType,
  courseTypeOptions,
  durationText,
  labelOf
} from '../teaching'
import type { ClassroomStatus, ClassroomSummary, Paged } from '../types'

const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()

const items = ref<ClassroomSummary[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(12)
const loading = ref(false)
const error = ref('')
const search = ref('')
const statusFilter = ref('')
const hasAppliedFilters = ref(false)
let requestId = 0

async function load() {
  const request = ++requestId
  const keyword = search.value.trim()
  const status = statusFilter.value
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSize.value) })
    if (keyword) params.set('search', keyword)
    if (status) params.set('status', status)
    const data = await api<Paged<ClassroomSummary>>(`/classrooms?${params.toString()}`)
    if (request !== requestId) return
    hasAppliedFilters.value = Boolean(keyword || status)
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
  void load()
}

function resetFilters() {
  search.value = ''
  statusFilter.value = ''
  applyFilters()
}

function openClassroom(item: ClassroomSummary) {
  emit('navigate', 'classroom-content', { classroomId: item.id })
}

function openStatistics(item: ClassroomSummary) {
  emit('navigate', 'classroom-statistics', { classroomId: item.id, backKey: 'my-classrooms' })
}

function actionLabel(status: ClassroomStatus) {
  if (status === 'open') return '进入学习'
  if (status === 'scheduled') return '查看内容'
  return '查看回顾'
}

onMounted(load)
onBeforeUnmount(() => {
  ++requestId
})
</script>

<template>
  <section class="page-heading learner-catalog-heading">
    <h1>我的课堂</h1>
    <el-button :icon="Refresh" :loading="loading" @click="load">刷新课堂</el-button>
  </section>

  <div class="surface student-filters" role="search" aria-label="查找课堂">
    <el-input
      v-model="search"
      class="search-box"
      clearable
      placeholder="搜索课堂、课程或教师"
      aria-label="搜索课堂、课程或教师"
      :prefix-icon="Search"
      @keyup.enter="applyFilters"
      @clear="applyFilters"
    />
    <el-select
      v-model="statusFilter"
      class="table-filter"
      placeholder="全部状态"
      aria-label="课堂状态"
      clearable
      @change="applyFilters"
    >
      <el-option
        v-for="option in classroomStatusOptions"
        :key="option.value"
        :label="option.label"
        :value="option.value"
      />
    </el-select>
    <el-button type="primary" :icon="Search" @click="applyFilters">查询</el-button>
    <el-button @click="resetFilters">重置</el-button>
    <span class="record-count">共 {{ total }} 个课堂</span>
  </div>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />

  <div v-loading="loading" class="my-classroom-grid">
    <article v-for="item in items" :key="item.id" class="surface classroom-card">
      <div class="classroom-card-top">
        <el-tag size="small" effect="plain">{{ labelOf(courseTypeOptions, item.courseType) }}</el-tag>
        <el-tag :type="classroomStatusType(item.status)" size="small">
          {{ labelOf(classroomStatusOptions, item.status) }}
        </el-tag>
      </div>
      <h2>{{ item.name }}</h2>
      <p class="classroom-course">{{ item.courseName }}</p>
      <p class="classroom-course">{{ classroomResourceCounts(item.resources) }}</p>
      <p class="classroom-course">总时长 {{ durationText(item.resources.totalMinutes) }}</p>
      <dl class="classroom-meta">
        <div>
          <dt>
            <el-icon><User /></el-icon>授课教师
          </dt>
          <dd>{{ item.teacherName }}</dd>
        </div>
        <div>
          <dt>
            <el-icon><Clock /></el-icon>课程时间
          </dt>
          <dd>{{ dateTime(item.openAt) }}<br />至 {{ dateTime(item.closeAt) }}</dd>
        </div>
      </dl>
      <div class="classroom-card-foot">
        <span
          ><el-icon><School /></el-icon>{{ item.studentCount }} 名同学</span
        >
        <div class="classroom-card-actions">
          <el-button @click="openStatistics(item)">课堂统计</el-button>
          <el-button type="primary" :plain="item.status !== 'open'" @click="openClassroom(item)">
            {{ actionLabel(item.status) }}
          </el-button>
        </div>
      </div>
    </article>
  </div>

  <el-empty
    v-if="!loading && !error && !items.length"
    :description="hasAppliedFilters ? '没有符合筛选条件的课堂' : '暂未加入课堂'"
  >
    <el-button v-if="hasAppliedFilters" @click="resetFilters">清除筛选</el-button>
    <p v-else class="empty-hint">请联系授课教师，确认已加入课堂。</p>
  </el-empty>

  <div v-if="total > pageSize" class="table-footer">
    <el-pagination
      v-model:current-page="page"
      v-model:page-size="pageSize"
      :total="total"
      :page-sizes="[12, 24, 48]"
      layout="total, sizes, prev, pager, next"
      @current-change="load"
      @size-change="applyFilters"
    />
  </div>
</template>

<style scoped>
.my-classroom-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(min(100%, 330px), 1fr));
  gap: 16px;
  min-height: 150px;
}

.classroom-card {
  display: flex;
  flex-direction: column;
  padding: 20px;
}

.classroom-card-top {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
}

.classroom-card h2 {
  margin: 12px 0 4px;
  color: #1b3552;
  font-size: 18px;
  overflow-wrap: anywhere;
}

.classroom-course {
  margin: 0 0 14px;
  color: #5a7087;
  font-size: 13px;
}

.classroom-meta {
  display: grid;
  gap: 10px;
  margin: 0 0 16px;
  padding: 13px;
  background: #f7f9fd;
  border: 1px solid #e8eef6;
  border-radius: 8px;
}

.classroom-meta > div {
  display: grid;
  grid-template-columns: 88px minmax(0, 1fr);
  gap: 8px;
  align-items: start;
}

.classroom-meta dt {
  display: flex;
  align-items: center;
  gap: 5px;
  color: #718497;
  font-size: 12px;
}

.classroom-meta dd {
  margin: 0;
  color: #314b66;
  font-size: 12px;
  line-height: 1.55;
  text-align: right;
}

.classroom-card-foot {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  align-items: center;
  gap: 10px;
  padding-top: 13px;
  margin-top: auto;
  border-top: 1px solid #edf2f7;
}

.classroom-card-foot > span {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: #6e8297;
  font-size: 12px;
}

.classroom-card-actions {
  display: flex;
  margin-left: auto;
  flex-wrap: wrap;
  gap: 6px;
}

.classroom-card-actions :deep(.el-button + .el-button) {
  margin-left: 0;
}

.empty-hint {
  max-width: 420px;
  margin: 0 auto;
  color: #718396;
  font-size: 13px;
}
</style>

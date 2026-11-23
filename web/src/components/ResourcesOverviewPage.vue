<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { Collection, Document, Refresh, Search, View } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import { readListState, useListPersist } from '../listState'
import { recoverPage } from '../paging'
import { courseTypeOptions, durationText, labelOf, resourceKindOptions } from '../teaching'
import type { CoreCourseSeries, CourseDetail, CourseSummary, Paged } from '../types'

const props = defineProps<{ role?: string }>()
const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()
const isStudent = computed(() => props.role === 'Student')

const items = ref<CourseSummary[]>([])
const total = ref(0)
const listKey = 'resources-overview'
const savedList = readListState(listKey, sessionStorage, [12, 24, 48], 12)
const page = ref(savedList.page)
const pageSize = ref(savedList.pageSize)
const loading = ref(false)
const error = ref('')
const search = ref(savedList.search)
const typeFilter = ref(savedList.filters.type || '')
const savedView = savedList.filters.view
const view = ref<'all' | 'core' | 'open'>(savedView === 'core' || savedView === 'open' ? savedView : 'all')
const savedSeriesId = savedList.filters.seriesId || ''
let restoreSeriesPending = view.value === 'core' && !!savedSeriesId
const series = ref<CoreCourseSeries[]>([])
const activeSeries = ref<CoreCourseSeries | null>(null)
let loadRequest = 0

const detailVisible = ref(false)
const detailLoading = ref(false)
const detailError = ref('')
const detail = ref<CourseDetail | null>(null)

async function loadSeries(request: number) {
  const rows = await api<CoreCourseSeries[]>('/course-series')
  if (request === loadRequest) series.value = rows
  if (!restoreSeriesPending) return false
  restoreSeriesPending = false
  const match = rows.find(item => item.id === savedSeriesId)
  if (!match) return false
  activeSeries.value = match
  return true
}

async function load() {
  const request = ++loadRequest
  loading.value = true
  error.value = ''
  try {
    if (view.value === 'core' && !activeSeries.value) {
      if (await loadSeries(request)) return load()
      return
    }
    const params = new URLSearchParams({
      status: 'published',
      page: String(page.value),
      pageSize: String(pageSize.value)
    })
    if (search.value.trim()) params.set('search', search.value.trim())
    if (view.value !== 'all') params.set('type', view.value)
    else if (typeFilter.value) params.set('type', typeFilter.value)
    if (activeSeries.value) params.set('seriesId', activeSeries.value.id)
    const data = await api<Paged<CourseSummary>>(`/courses?${params.toString()}`)
    if (request !== loadRequest) return
    items.value = data.items
    total.value = data.total
    const recovered = recoverPage(page.value, pageSize.value, total.value, items.value.length)
    if (recovered !== null && recovered !== page.value) {
      page.value = recovered
      return load()
    }
  } catch (failure) {
    if (request === loadRequest) error.value = messageOf(failure)
  } finally {
    if (request === loadRequest) loading.value = false
  }
}

function selectSeries(item: CoreCourseSeries | null) {
  activeSeries.value = item
  items.value = []
  total.value = 0
  resetFilters()
}

useListPersist(listKey, () => ({
  search: search.value,
  filters: { type: typeFilter.value, view: view.value, seriesId: activeSeries.value?.id || '' },
  page: page.value,
  pageSize: pageSize.value
}))

function applyFilters() {
  page.value = 1
  void load()
}

function resetFilters() {
  search.value = ''
  typeFilter.value = ''
  applyFilters()
}

function enterCourse(course: Pick<CourseSummary, 'id'>) {
  emit('navigate', 'course-preview', { courseId: course.id, backKey: 'resources-overview' })
}

async function openCourse(course: CourseSummary) {
  detailVisible.value = true
  detailLoading.value = true
  detailError.value = ''
  detail.value = null
  try {
    detail.value = await api<CourseDetail>(`/courses/${course.id}`)
  } catch (failure) {
    detailError.value = messageOf(failure)
  } finally {
    detailLoading.value = false
  }
}

onMounted(load)
</script>

<template>
  <section class="page-heading">
    <div>
      <h1>资源概览</h1>
      <p>浏览已发布课程的章节、小节与学习资源，了解课程内容规模。</p>
    </div>
    <el-button :icon="Refresh" :loading="loading" @click="load">刷新目录</el-button>
  </section>

  <el-radio-group v-model="view" class="catalog-sections" @change="selectSeries(null)">
    <el-radio-button value="all">课程汇总</el-radio-button>
    <el-radio-button value="core">专业核心课</el-radio-button>
    <el-radio-button value="open">开放式资源库</el-radio-button>
  </el-radio-group>

  <section v-if="view === 'core' && !activeSeries" v-loading="loading" class="surface">
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" />
    <div class="course-catalog">
      <article v-for="item in series" :key="item.id" class="catalog-card">
        <h2>{{ item.name }}</h2>
        <p v-if="item.description" class="catalog-description">{{ item.description }}</p>
        <div class="catalog-foot">
          <span>{{ item.courseCount }} 门课程</span>
          <el-button link type="primary" @click="selectSeries(item)">查看系列</el-button>
        </div>
      </article>
    </div>
    <el-empty v-if="!series.length && !loading && !error" description="暂无已发布的专业核心课系列" />
  </section>
  <section v-else class="surface">
    <header v-if="activeSeries" class="overview-toolbar">
      <el-button link @click="selectSeries(null)">返回系列</el-button>
      <strong>{{ activeSeries.name }}</strong>
    </header>
    <div class="overview-toolbar">
      <el-input
        v-model="search"
        class="search-box"
        clearable
        placeholder="搜索课程名称或简介"
        :prefix-icon="Search"
        @keyup.enter="applyFilters"
        @clear="applyFilters"
      />
      <el-select
        v-if="view === 'all'"
        v-model="typeFilter"
        class="table-filter"
        placeholder="全部类型"
        clearable
        @change="applyFilters"
      >
        <el-option
          v-for="option in courseTypeOptions"
          :key="option.value"
          :label="option.label"
          :value="option.value"
        />
      </el-select>
      <el-button type="primary" :icon="Search" @click="applyFilters">查询</el-button>
      <el-button @click="resetFilters">重置</el-button>
      <span class="record-count">共 {{ total }} 门课程</span>
    </div>

    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="table-error" />

    <div v-loading="loading" class="course-catalog">
      <article v-for="course in items" :key="course.id" class="catalog-card">
        <div class="catalog-card-top">
          <el-tag size="small" effect="plain">{{ labelOf(courseTypeOptions, course.type) }}</el-tag>
          <span class="catalog-date">发布于 {{ dateTime(course.publishedAt || course.createdAt) }}</span>
        </div>
        <h2>{{ course.name }}</h2>
        <p class="catalog-description">{{ course.description || '暂无课程简介' }}</p>
        <div class="catalog-stats">
          <span
            ><strong>{{ course.chapterCount }}</strong> 大章</span
          >
          <span
            ><strong>{{ course.sectionCount }}</strong> 小节</span
          >
          <span
            ><strong>{{ course.resourceCount }}</strong> 资源</span
          >
          <span
            ><strong>{{ durationText(course.totalMinutes) }}</strong></span
          >
        </div>
        <p class="catalog-breakdown">
          理论 {{ course.theoryCount }} · 案例 {{ course.caseCount }} · 练习 {{ course.theoryPracticeCount }} · 实操
          {{ course.casePracticeCount }}
        </p>
        <div class="catalog-foot">
          <span class="catalog-owner"
            ><el-icon><Collection /></el-icon>课程目录</span
          >
          <div class="catalog-actions">
            <el-button link :icon="View" @click="openCourse(course)">查看结构</el-button>
            <el-button link type="primary" @click="enterCourse(course)">进入课程</el-button>
          </div>
        </div>
      </article>
    </div>

    <el-empty
      v-if="!loading && !error && !items.length"
      class="catalog-empty"
      :image-size="80"
      description="暂无已发布课程"
    >
      <p class="empty-hint">
        {{
          isStudent
            ? '老师发布课程后会显示在这里，加入课堂后即可开始学习。'
            : '课程发布后会出现在这里；如需组课，请先在课程管理中创建并发布课程。'
        }}
      </p>
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
  </section>

  <el-drawer v-model="detailVisible" :title="detail?.name || '课程结构'" :size="'min(760px, 96vw)'">
    <div v-if="detailLoading" class="tree-loading"><el-skeleton :rows="7" animated /></div>
    <el-alert v-else-if="detailError" :title="detailError" type="error" show-icon :closable="false" />
    <template v-else-if="detail">
      <div class="detail-summary">
        <el-tag size="small" effect="plain">{{ labelOf(courseTypeOptions, detail.type) }}</el-tag>
        <span>{{ detail.chapterCount }} 大章 · {{ detail.sectionCount }} 小节 · {{ detail.resourceCount }} 资源</span>
        <el-button type="primary" size="small" @click="enterCourse(detail)">进入课程学习</el-button>
      </div>
      <p v-if="detail.description" class="detail-description">{{ detail.description }}</p>
      <p v-if="detail.remark" class="detail-remark">备注：{{ detail.remark }}</p>

      <div v-if="!detail.chapters.length" class="tree-loading">
        <el-empty description="课程暂未配置章节" />
      </div>
      <div v-else class="catalog-tree">
        <section v-for="chapter in detail.chapters" :key="chapter.id" class="catalog-chapter">
          <h3>{{ chapter.name }}</h3>
          <div v-for="section in chapter.sections" :key="section.id" class="catalog-section">
            <div class="catalog-section-heading">
              <strong>{{ section.name }}</strong>
              <span>{{ durationText(section.durationMinutes) }}</span>
            </div>
            <ul v-if="section.resources.length" class="catalog-resources">
              <li v-for="resource in section.resources" :key="resource.id">
                <el-icon><Document /></el-icon>
                <span>{{ resource.name }}</span>
                <el-tag size="small" effect="plain">{{ labelOf(resourceKindOptions, resource.kind) }}</el-tag>
                <small>{{ resource.durationMinutes }} 分钟</small>
                <small v-if="resource.questionCount">{{ resource.questionCount }} 题</small>
              </li>
            </ul>
            <p v-else class="tree-empty">本节暂无资源</p>
          </div>
          <p v-if="!chapter.sections.length" class="tree-empty">本章暂无小节</p>
        </section>
      </div>
    </template>
  </el-drawer>
</template>

<style scoped>
.catalog-sections {
  margin-bottom: 14px;
}
.overview-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
  padding: 16px 18px;
  border-bottom: 1px solid #dce7eb;
  background: #f8fbfb;
}

.overview-toolbar > .table-filter {
  width: 170px;
  flex: 0 0 170px;
}

.overview-toolbar .record-count {
  margin-left: auto;
}

.course-catalog {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(min(100%, 300px), 1fr));
  gap: 16px;
  padding: 18px;
  min-height: 120px;
}

.catalog-card {
  display: flex;
  flex-direction: column;
  padding: 17px;
  background: linear-gradient(165deg, #ffffff 0%, #f7fbf9 100%);
  border: 1px solid #d6e0e6;
  border-radius: 10px;
  transition:
    border-color 0.18s ease,
    box-shadow 0.18s ease,
    transform 0.18s ease;
}

.catalog-card:hover {
  border-color: #bdd9d3;
  box-shadow: 0 8px 22px #146d6614;
  transform: translateY(-1px);
}

.catalog-card-top {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
}

.catalog-date {
  color: #56697c;
  font-size: 11px;
  text-align: right;
}

.catalog-card h2 {
  margin: 11px 0 6px;
  color: #1c3955;
  font-size: 17px;
  overflow-wrap: anywhere;
}

.catalog-description {
  display: -webkit-box;
  -webkit-line-clamp: 3;
  -webkit-box-orient: vertical;
  overflow: hidden;
  min-height: 51px;
  margin: 0 0 13px;
  color: #56697c;
  font-size: 12px;
  line-height: 1.65;
}

.catalog-stats {
  display: flex;
  gap: 16px;
  padding: 10px 0;
  border-top: 1px solid #e4eeea;
  border-bottom: 1px solid #e4eeea;
  color: #56697c;
  font-size: 11px;
}

.catalog-stats strong {
  margin-right: 2px;
  color: #2a4a67;
  font-size: 15px;
}

.catalog-breakdown {
  margin: 8px 0 0;
  color: #64788a;
  font-size: 11px;
}

.catalog-actions {
  display: flex;
  align-items: center;
  gap: 2px;
}

.catalog-foot {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
  margin-top: auto;
  padding-top: 11px;
}

.catalog-owner {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: #56697c;
  font-size: 11px;
}

.detail-summary {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 10px;
  color: #56697c;
  font-size: 12px;
}

.detail-description {
  margin: 12px 0 0;
  color: #405a72;
  font-size: 13px;
  line-height: 1.7;
  white-space: pre-wrap;
}

.detail-remark {
  margin: 8px 0 0;
  color: #56697c;
  font-size: 12px;
}

.catalog-tree {
  margin-top: 20px;
}

.catalog-chapter {
  padding: 0 0 8px;
  margin-bottom: 14px;
  border-bottom: 1px solid #e4eeea;
}

.catalog-chapter h3 {
  margin: 0 0 10px;
  color: #234461;
  font-size: 15px;
}

.catalog-section {
  padding: 9px 12px;
  margin-bottom: 8px;
  background: #f5f9f8;
  border: 1px solid #e4eeea;
  border-radius: 8px;
}

.catalog-section-heading {
  display: flex;
  justify-content: space-between;
  align-items: baseline;
  gap: 10px;
}

.catalog-section-heading strong {
  color: #3b5873;
  font-size: 13px;
}

.catalog-section-heading span {
  color: #56697c;
  font-size: 11px;
}

.catalog-resources {
  padding: 8px 0 0;
  margin: 0;
  list-style: none;
}

.catalog-resources li {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 7px;
  padding: 6px 2px;
  color: #4e657b;
  font-size: 12px;
}

.catalog-resources li > span {
  flex: 1;
  min-width: 120px;
  overflow-wrap: anywhere;
}

.catalog-resources small {
  color: #64788a;
  font-size: 10px;
  white-space: nowrap;
}

.tree-empty {
  margin: 6px 2px 0;
  color: #64788a;
  font-size: 11px;
}

.tree-loading {
  padding: 16px 0;
}

.empty-hint {
  max-width: 430px;
  margin: 0 auto;
  color: #56697c;
  font-size: 13px;
}

.catalog-empty {
  grid-column: 1 / -1;
  padding: 18px 0 6px;
}

@media (max-width: 640px) {
  .overview-toolbar > .table-filter {
    width: 100%;
    flex-basis: 100%;
  }

  .overview-toolbar .record-count {
    margin-left: 0;
    width: 100%;
  }
}
</style>

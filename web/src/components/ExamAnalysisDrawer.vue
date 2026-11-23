<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Download, Refresh, Search, Tickets } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { api, downloadFile, messageOf } from '../api'
import { examQuestionTypeLabel, examQuestionTypeOptions } from '../exam'
import type { ExamAnalysisResponse } from '../types'
import TableEmpty from './TableEmpty.vue'

const props = defineProps<{ modelValue: boolean; examId: string; examName: string }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; 'open-roster': [] }>()

const visible = computed({
  get: () => props.modelValue,
  set: value => emit('update:modelValue', value)
})

const data = ref<ExamAnalysisResponse | null>(null)
const loading = ref(false)
const error = ref('')
const exporting = ref(false)
const search = ref('')
const typeFilter = ref('')
const page = ref(1)
const pageSize = ref(20)
const appliedSearch = ref('')

function filterParams() {
  const params = new URLSearchParams()
  if (appliedSearch.value) params.set('search', appliedSearch.value)
  if (typeFilter.value) params.set('type', typeFilter.value)
  return params
}

async function load() {
  if (!props.examId) return
  loading.value = true
  error.value = ''
  try {
    const params = filterParams()
    params.set('page', String(page.value))
    params.set('pageSize', String(pageSize.value))
    data.value = await api<ExamAnalysisResponse>(`/exams/${props.examId}/question-analysis?${params.toString()}`)
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

function applyFilters() {
  appliedSearch.value = search.value.trim()
  page.value = 1
  void load()
}

function resetFilters() {
  search.value = ''
  typeFilter.value = ''
  applyFilters()
}

async function exportCsv() {
  exporting.value = true
  try {
    await downloadFile(
      `/exams/${props.examId}/question-analysis/export?${filterParams()}`,
      `试题分析-${props.examName}.csv`
    )
    ElMessage.success('试题分析已导出')
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    exporting.value = false
  }
}

const filtering = computed(() => !!(appliedSearch.value || typeFilter.value))

function analysisEmptyTitle() {
  if (error.value) return '试题分析加载失败'
  if (filtering.value) return '没有符合条件的试题'
  return '暂无试题分析数据'
}

function analysisEmptyHint() {
  if (error.value) return '请稍后刷新重试。'
  if (filtering.value) return '试试调整关键词或题型，或重置筛选查看全部试题。'
  if ((data.value?.finishedCount ?? 0) === 0) return '学生交卷后才会统计正确率和失分提示。'
  return '请确认本场考试已配置试卷题目。'
}

watch(
  () => [props.modelValue, props.examId] as const,
  ([open]) => {
    if (open) {
      page.value = 1
      search.value = ''
      appliedSearch.value = ''
      typeFilter.value = ''
      void load()
    }
  }
)
</script>

<template>
  <el-drawer v-model="visible" :title="`试题分析 · ${examName}`" size="min(1000px, 96vw)">
    <div class="table-toolbar">
      <div class="table-controls">
        <el-input
          v-model="search"
          class="search-box"
          clearable
          placeholder="搜索题干"
          :prefix-icon="Search"
          @keyup.enter="applyFilters"
          @clear="applyFilters"
        />
        <el-select v-model="typeFilter" class="table-filter" placeholder="全部题型" clearable @change="applyFilters">
          <el-option
            v-for="option in examQuestionTypeOptions"
            :key="option.value"
            :label="option.label"
            :value="option.value"
          />
        </el-select>
        <el-button type="primary" :icon="Search" @click="applyFilters">查询</el-button>
        <el-button @click="resetFilters">重置</el-button>
      </div>
      <div class="analysis-actions">
        <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
        <el-button :icon="Tickets" @click="emit('open-roster')">考试名单</el-button>
        <el-button :icon="Download" :loading="exporting" @click="exportCsv">导出分析</el-button>
      </div>
    </div>

    <p class="analysis-hint">
      已统计 {{ data?.finishedCount ?? 0 }} 份已交卷记录。客观题按作答人数计算正确率，实操题按已评分人数计算满分比例。
    </p>
    <p v-if="data?.items?.length" class="table-scroll-hint">横向滚动可查看更多列</p>
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
    <el-table v-loading="loading" :data="data?.items || []" row-key="paperQuestionId" empty-text="">
      <template #empty>
        <TableEmpty v-if="!loading" :title="analysisEmptyTitle()" :hint="analysisEmptyHint()">
          <template v-if="!error" #action>
            <el-button v-if="filtering" @click="resetFilters">重置筛选</el-button>
            <el-button v-else-if="(data?.finishedCount ?? 0) === 0" type="primary" plain @click="emit('open-roster')">
              打开考试名单
            </el-button>
          </template>
        </TableEmpty>
      </template>
      <el-table-column prop="number" label="题号" width="62" align="center" />
      <el-table-column label="题型" width="90">
        <template #default="{ row }">
          <el-tag size="small" effect="plain">{{ examQuestionTypeLabel(row.type) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="题干" min-width="320">
        <template #default="{ row }">
          <strong class="analysis-stem">{{ row.stem }}</strong>
        </template>
      </el-table-column>
      <el-table-column prop="maxScore" label="分值" width="70" align="center" />
      <el-table-column prop="answered" label="作答人数" width="90" align="center" />
      <el-table-column prop="unanswered" label="未作答人数" width="100" align="center" />
      <el-table-column prop="correctCount" label="正确人数" width="90" align="center" />
      <el-table-column label="正确率" width="148">
        <template #default="{ row }">
          <div v-if="row.evaluated" class="analysis-accuracy">
            <el-progress
              :percentage="row.accuracy"
              :stroke-width="7"
              :status="row.accuracy >= 60 ? 'success' : 'exception'"
              :show-text="false"
            />
            <span>{{ row.accuracy }}%</span>
          </div>
          <span v-else>—</span>
        </template>
      </el-table-column>
      <el-table-column label="失分提示" width="110" align="center">
        <template #default="{ row }">
          <span v-if="!row.evaluated">暂无数据</span>
          <el-tag v-else-if="row.accuracy < 60" type="danger" size="small" effect="plain">重点讲解</el-tag>
          <span v-else class="analysis-ok">掌握良好</span>
        </template>
      </el-table-column>
    </el-table>
    <div class="table-footer">
      <el-pagination
        v-model:current-page="page"
        v-model:page-size="pageSize"
        :total="data?.total || 0"
        :page-sizes="[10, 20, 50]"
        layout="total, sizes, prev, pager, next"
        @current-change="load"
        @size-change="applyFilters"
      />
    </div>
  </el-drawer>
</template>

<style scoped>
.analysis-actions {
  display: flex;
  gap: 8px;
}

.analysis-hint {
  margin: 0 0 14px;
  color: #56697c;
  font-size: 12px;
}

.analysis-stem {
  display: block;
  color: #29465f;
  font-size: 13px;
  line-height: 1.5;
}

.analysis-accuracy {
  display: flex;
  align-items: center;
  gap: 8px;
}

.analysis-accuracy :deep(.el-progress) {
  flex: 1;
}

.analysis-accuracy span {
  color: #56697c;
  font-size: 12px;
  white-space: nowrap;
}

.analysis-ok {
  color: #12735f;
  font-size: 12px;
}

@media (max-width: 640px) {
  .analysis-actions {
    width: 100%;
  }
}
</style>

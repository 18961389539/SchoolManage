<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Download, Refresh, Tickets } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { api, downloadFile, messageOf } from '../api'
import { examQuestionTypeLabel } from '../exam'
import type { ExamStatistics } from '../types'

const props = defineProps<{ modelValue: boolean; examId: string; examName: string }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; 'open-roster': [] }>()

const visible = computed({
  get: () => props.modelValue,
  set: value => emit('update:modelValue', value)
})

const data = ref<ExamStatistics | null>(null)
const loading = ref(false)
const error = ref('')
const exporting = ref(false)
let request = 0

async function load() {
  if (!props.examId) return
  const current = ++request
  loading.value = true
  error.value = ''
  try {
    const report = await api<ExamStatistics>(`/exams/${props.examId}/statistics`)
    if (current === request) data.value = report
  } catch (failure) {
    if (current === request) error.value = messageOf(failure)
  } finally {
    if (current === request) loading.value = false
  }
}

async function exportCsv() {
  exporting.value = true
  try {
    await downloadFile(`/exams/${props.examId}/statistics/export`, `考试统计-${props.examName}.csv`)
    ElMessage.success('成绩统计已导出')
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    exporting.value = false
  }
}

watch(
  () => [props.modelValue, props.examId] as const,
  ([open, id], previous) => {
    if (id !== previous?.[1]) data.value = null
    if (open && id) void load()
    else {
      request++
      loading.value = false
    }
  },
  { immediate: true }
)
</script>

<template>
  <el-drawer v-model="visible" :title="`考试统计 · ${examName}`" size="min(900px, 96vw)">
    <div class="stats-toolbar">
      <p class="stats-hint">分数段按应考人数计算，成绩统计按已出分考生计算。</p>
      <div class="stats-actions">
        <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
        <el-button :icon="Tickets" @click="emit('open-roster')">考试名单</el-button>
        <el-button type="primary" :icon="Download" :loading="exporting" @click="exportCsv">导出统计</el-button>
      </div>
    </div>
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
    <el-skeleton v-if="loading && !data" :rows="5" animated />
    <el-empty v-else-if="!loading && !data && !error" description="暂无考试统计数据">
      <p class="stats-empty-hint">统计数据加载后，将在这里展示应考情况、分数段和各题型正确率。</p>
    </el-empty>

    <div v-if="data" v-loading="loading" class="stats-report">
      <el-alert
        v-if="!data.expected"
        title="本场考试还没有应考学生"
        type="info"
        show-icon
        :closable="false"
        class="section-notice"
      >
        <p class="stats-empty-hint stats-empty-hint--start">
          请先在考试名单中添加学生。分数段按应考人数计算，成绩统计将在有名单后显示。
        </p>
        <el-button type="primary" plain @click="emit('open-roster')">打开考试名单</el-button>
      </el-alert>
      <el-alert
        v-else-if="!data.graded"
        title="尚无已出分成绩"
        description="平均分、及格率和高低分将在批阅完成后显示；分数段仍按应考人数计算。"
        type="info"
        show-icon
        :closable="false"
        class="section-notice"
      />
      <section class="stats-section">
        <header><h3>数据总览</h3></header>
        <dl class="stats-grid">
          <div class="stats-item">
            <dt>应考人数</dt>
            <dd>{{ data.expected }}</dd>
          </div>
          <div class="stats-item">
            <dt>实到人数</dt>
            <dd>{{ data.started }}</dd>
          </div>
          <div class="stats-item">
            <dt>缺考人数</dt>
            <dd>{{ data.absent }}</dd>
          </div>
          <div class="stats-item">
            <dt>已批阅</dt>
            <dd>
              {{ data.graded }}<small> / {{ data.submitted }}</small>
            </dd>
          </div>
          <div class="stats-item">
            <dt>平均分</dt>
            <dd>
              {{ data.graded ? data.averageScore : '—' }}<small> / {{ data.maxScore }}</small>
            </dd>
          </div>
          <div class="stats-item">
            <dt>最高分</dt>
            <dd>{{ data.graded ? data.highestScore : '—' }}</dd>
          </div>
          <div class="stats-item">
            <dt>最低分</dt>
            <dd>{{ data.graded ? data.lowestScore : '—' }}</dd>
          </div>
          <div class="stats-item highlight">
            <dt>及格率</dt>
            <dd>{{ data.graded ? `${data.passRate}%` : '—' }}</dd>
          </div>
        </dl>
      </section>

      <section class="stats-section">
        <header>
          <h3>分数段分布</h3>
          <span>平均得分率 {{ data.graded ? `${data.averageRate}%` : '—' }}</span>
        </header>
        <ul class="band-list">
          <li v-for="band in data.bands" :key="band.label" class="band-row">
            <span class="band-label">{{ band.label }}</span>
            <div class="band-track" aria-hidden="true">
              <i :style="{ width: `${band.percent}%` }" />
            </div>
            <strong>{{ band.count }} 人 · {{ band.percent }}%</strong>
          </li>
        </ul>
      </section>

      <section class="stats-section">
        <header>
          <h3>各题型正确率</h3>
          <span>实操题按已评分题中的满分作答计算</span>
        </header>
        <el-empty v-if="!data.typeStats.length" description="暂无题型统计" :image-size="70">
          <p class="stats-empty-hint">试卷题目作答并评分后，将在这里显示各题型正确率。</p>
        </el-empty>
        <ul v-else class="question-stat-list">
          <li v-for="row in data.typeStats" :key="row.type" class="question-stat">
            <h4>{{ examQuestionTypeLabel(row.type) }}</h4>
            <div class="question-accuracy">
              <span>正确率</span><strong>{{ row.accuracy }}%</strong>
              <el-progress :percentage="row.accuracy" :stroke-width="6" :show-text="false" aria-hidden="true" />
            </div>
            <dl class="question-metrics">
              <div>
                <dt>题量</dt>
                <dd>{{ row.questionCount }}</dd>
              </div>
              <div>
                <dt>单份分值</dt>
                <dd>{{ row.maxScore }}</dd>
              </div>
              <div>
                <dt>作答人次</dt>
                <dd>{{ row.answered }}</dd>
              </div>
              <div>
                <dt>正确人次</dt>
                <dd>{{ row.correct }}</dd>
              </div>
              <div>
                <dt>得分率</dt>
                <dd>{{ row.scoreRate }}%</dd>
              </div>
            </dl>
          </li>
        </ul>
      </section>
    </div>
  </el-drawer>
</template>

<style scoped>
.stats-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 10px 16px;
  margin-bottom: 18px;
}

.stats-hint,
.stats-section header span,
.stats-empty-hint {
  margin: 0;
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.stats-empty-hint {
  max-width: 36ch;
  margin: 8px auto 0;
  text-align: center;
  line-height: 1.5;
}

.stats-empty-hint--start {
  max-width: none;
  margin: 0 0 10px;
  text-align: left;
}

.stats-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.stats-actions .el-button {
  margin: 0;
}

.stats-report {
  display: grid;
  gap: 20px;
  padding-bottom: 6px;
  font-variant-numeric: tabular-nums;
}

.stats-section > header {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  align-items: baseline;
  gap: 5px 12px;
  margin-bottom: 10px;
}

.stats-section h3,
.question-stat h4 {
  margin: 0;
  color: var(--el-text-color-primary);
}

.stats-section h3 {
  font-size: 16px;
}

.stats-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 8px;
  margin: 0;
}

.stats-item {
  min-width: 0;
  padding: 12px 14px;
  background: var(--el-fill-color-light);
  border: 1px solid var(--el-border-color-light);
  border-radius: 9px;
}

.stats-item dt,
.stats-item small {
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.stats-item dd {
  margin: 3px 0 0;
  color: var(--el-text-color-primary);
  font-size: 23px;
  font-weight: 650;
  line-height: 1.35;
  overflow-wrap: anywhere;
}

.stats-item small {
  font-weight: 400;
}

.stats-item.highlight {
  background: var(--el-color-primary-light-9);
  border-color: var(--el-color-primary-light-7);
}

.stats-item.highlight dd {
  color: var(--el-color-primary-dark-2);
}

.band-list,
.question-stat-list {
  margin: 0;
  padding: 0;
  list-style: none;
}

.band-list {
  display: grid;
  gap: 11px;
  padding: 14px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 9px;
}

.band-row {
  display: grid;
  grid-template-columns: 110px minmax(0, 1fr) minmax(104px, auto);
  grid-template-areas: 'label track value';
  align-items: center;
  gap: 5px 12px;
}

.band-label {
  grid-area: label;
  color: var(--el-text-color-regular);
  font-size: 13px;
}

.band-track {
  grid-area: track;
  height: 7px;
  border-radius: 6px;
  background: var(--el-fill-color-light);
  overflow: hidden;
}

.band-track i {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: var(--el-color-primary);
}

.band-row strong {
  grid-area: value;
  color: var(--el-text-color-primary);
  font-size: 13px;
  text-align: right;
}

.question-stat-list {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 156px), 1fr));
  gap: 8px;
}

.question-stat {
  min-width: 0;
  padding: 12px 14px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 9px;
}

.question-stat h4 {
  font-size: 14px;
}

.question-accuracy,
.question-metrics {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 6px 8px;
}

.question-accuracy {
  align-items: baseline;
  margin: 8px 0 12px;
}

.question-accuracy span,
.question-metrics dt {
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.question-accuracy strong {
  justify-self: end;
  color: var(--el-color-primary-dark-2);
  font-size: 15px;
}

.question-accuracy .el-progress {
  grid-column: 1 / -1;
}

.question-metrics {
  margin: 0;
  padding-top: 9px;
  border-top: 1px solid var(--el-border-color-light);
}

.question-metrics dd {
  margin: 1px 0 0;
  color: var(--el-text-color-primary);
  font-size: 13px;
  font-weight: 550;
  overflow-wrap: anywhere;
}

@media (max-width: 720px) {
  .stats-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 540px) {
  .band-row {
    grid-template-columns: minmax(0, 1fr) auto;
    grid-template-areas:
      'label value'
      'track track';
  }
}
</style>

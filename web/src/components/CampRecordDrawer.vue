<script setup lang="ts">
import { computed, ref, useId, watch } from 'vue'
import { Grid, Refresh } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import { campAttemptStatusLabel, campAttemptStatusType } from '../camp'
import { examQuestionTypeLabel, optionLetters } from '../exam'
import type { CampRecord, CampRecordQuestion } from '../types'
import QuestionReviewIndex from './QuestionReviewIndex.vue'

const visible = defineModel<boolean>({ required: true })
const props = defineProps<{ attemptId: string }>()
const questionPrefix = useId()

const record = ref<CampRecord | null>(null)
const loading = ref(false)
const error = ref('')

const pendingCount = computed(() => (record.value?.questions || []).filter(question => !question.answered).length)

function closeIndexAfterChoice(event: MouseEvent) {
  if (
    event.target instanceof Element &&
    event.target.closest('button') &&
    event.currentTarget instanceof HTMLDetailsElement
  )
    event.currentTarget.open = false
}

async function load() {
  if (!props.attemptId) return
  loading.value = true
  error.value = ''
  try {
    record.value = await api<CampRecord>(`/camp/attempts/${props.attemptId}/record`)
  } catch (failure) {
    record.value = null
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

watch(
  () => [visible.value, props.attemptId],
  () => {
    if (visible.value) void load()
  },
  { immediate: true }
)

function selectedText(question: CampRecordQuestion): string {
  if (question.type === 'practical') return question.textAnswer || '未作答'
  if (!question.selected.length) return '未作答'
  return question.selected
    .slice()
    .sort((left, right) => left - right)
    .map(index => `${optionLetters[index] || index + 1}. ${question.options[index] ?? ''}`)
    .join('、')
}

function correctText(question: CampRecordQuestion): string {
  return question.answers.length
    ? question.answers.map(index => `${optionLetters[index] || index + 1}. ${question.options[index] ?? ''}`).join('、')
    : '—'
}
</script>

<template>
  <el-drawer
    v-model="visible"
    title="岗位测评作答记录"
    class="camp-record-drawer"
    size="min(880px, 100vw)"
    destroy-on-close
  >
    <div v-if="loading" class="record-loading"><el-skeleton :rows="8" animated /></div>
    <el-result v-else-if="error" icon="error" title="记录加载失败" :sub-title="error">
      <template #extra><el-button type="primary" :icon="Refresh" @click="load">重试</el-button></template>
    </el-result>
    <template v-else-if="record">
      <header class="record-head">
        <div>
          <h2>{{ record.position.name }}</h2>
          <p>
            {{ record.attempt.studentName }}（{{ record.attempt.studentNumber || record.attempt.studentUserName }}）
            <span v-if="record.attempt.className"> · {{ record.attempt.className }}</span>
          </p>
        </div>
        <div class="record-score">
          <strong>{{ record.attempt.totalScore }}</strong>
          <span>/ {{ record.attempt.maxScore }}</span>
          <el-tag :type="campAttemptStatusType(record.attempt.status)" size="small">
            {{ campAttemptStatusLabel(record.attempt.status) }}
          </el-tag>
        </div>
      </header>
      <dl class="record-summary">
        <div>
          <dt>得分率</dt>
          <dd>{{ record.attempt.percent }}%</dd>
        </div>
        <div>
          <dt>客观题</dt>
          <dd>{{ record.attempt.objectiveScore }} 分</dd>
        </div>
        <div>
          <dt>实操题</dt>
          <dd>{{ record.attempt.practicalScore }} 分</dd>
        </div>
      </dl>
      <div class="record-meta">
        <span>开始 {{ dateTime(record.attempt.startedAt) }}</span>
        <span>结束 {{ dateTime(record.attempt.submittedAt) }}</span>
        <el-tag v-if="record.attempt.autoSubmitted" type="warning" size="small" effect="plain">超时自动提交</el-tag>
      </div>

      <details
        v-if="record.questions.length"
        :key="record.attemptId"
        class="record-index"
        @click.capture="closeIndexAfterChoice"
      >
        <summary>
          <el-icon aria-hidden="true"><Grid /></el-icon>
          <strong>题号索引</strong>
          <span>{{ pendingCount ? `${pendingCount} 道题未作答` : `全部 ${record.questions.length} 题已作答` }}</span>
        </summary>
        <div class="record-index-scroll">
          <QuestionReviewIndex :questions="record.questions" :target-prefix="questionPrefix" />
        </div>
      </details>
      <article
        v-for="(question, index) in record.questions"
        :id="`${questionPrefix}-${index}`"
        :key="question.questionId"
        class="record-question"
        tabindex="-1"
      >
        <header class="question-head">
          <div class="question-tags">
            <h3>第 {{ index + 1 }} 题</h3>
            <el-tag size="small" effect="plain">{{ examQuestionTypeLabel(question.type) }}</el-tag>
            <el-tag v-if="question.skillName" size="small" type="info" effect="plain">{{ question.skillName }}</el-tag>
            <el-tag
              v-if="question.answered"
              size="small"
              :type="question.correct ? 'success' : question.type === 'practical' ? 'info' : 'danger'"
            >
              {{ question.correct ? '正确' : question.type === 'practical' ? '已作答' : '错误' }}
            </el-tag>
            <el-tag v-else size="small" type="info">未作答</el-tag>
          </div>
          <strong class="question-score">{{ question.scoreAwarded }} / {{ question.maxScore }} 分</strong>
        </header>
        <p class="question-stem">{{ question.stem }}</p>
        <dl v-if="question.type !== 'practical'" class="answer-comparison">
          <div>
            <dt>你的答案</dt>
            <dd :class="{ muted: !question.selected.length }">{{ selectedText(question) }}</dd>
          </div>
          <div>
            <dt>正确答案</dt>
            <dd>{{ correctText(question) }}</dd>
          </div>
        </dl>
        <template v-else>
          <ul v-if="question.automaticPractical && question.practicalDetails.length" class="rubric-list">
            <li v-for="(row, rowIndex) in question.practicalDetails" :key="rowIndex" class="rubric-item">
              <div class="rubric-heading">
                <strong>{{ row.name }}</strong>
                <span class="question-score">{{ row.matched ? row.score : 0 }} / {{ row.score }} 分</span>
              </div>
              <p v-if="row.deviceName" class="rubric-note">{{ row.deviceName }}</p>
              <dl class="answer-comparison">
                <div>
                  <dt>预期</dt>
                  <dd>{{ row.expected }}</dd>
                </div>
                <div>
                  <dt>实际</dt>
                  <dd>{{ row.actual ?? '—' }}</dd>
                </div>
              </dl>
              <p v-if="row.guidance" class="rubric-note">排错指导 · {{ row.guidance }}</p>
            </li>
          </ul>
          <p v-else-if="question.automaticPractical" class="rubric-note">暂无评分项记录</p>
          <dl v-else class="answer-comparison practical-answer">
            <div>
              <dt>实操作答</dt>
              <dd>{{ selectedText(question) }}</dd>
            </div>
          </dl>
          <ul v-if="question.rubric.length" class="rubric-list">
            <li v-for="(item, itemIndex) in question.rubric" :key="itemIndex" class="rubric-item">
              <div class="rubric-heading">
                <strong>{{ item.item }}</strong>
                <el-tag :type="item.matched ? 'success' : 'info'" size="small">
                  {{ item.matched ? '已达成' : '未达成' }}
                </el-tag>
                <span class="question-score">{{ item.matched ? item.score : 0 }} / {{ item.score }} 分</span>
              </div>
              <p class="rubric-note">关键词 · {{ item.keyword }}</p>
            </li>
          </ul>
        </template>
        <p v-if="question.analysis" class="question-analysis"><strong>解析</strong> {{ question.analysis }}</p>
      </article>
    </template>
  </el-drawer>
</template>

<style scoped>
:global(.camp-record-drawer .el-drawer__header) {
  flex: none;
  margin: 0;
  padding: 17px 22px;
  border-bottom: 1px solid #e7edf4;
}

:global(.camp-record-drawer .el-drawer__body) {
  min-height: 0;
  padding: 18px 22px 24px;
  overscroll-behavior: contain;
}

.record-loading {
  padding: 20px 0;
}

.record-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 12px 18px;
}

.record-head > div:first-child {
  min-width: 0;
  overflow-wrap: anywhere;
}

.record-head h2 {
  margin: 0 0 5px;
  color: #1c334e;
  font-size: 21px;
}

.record-head p {
  margin: 0;
  color: var(--text-muted, #586d84);
  font-size: 13px;
}

.record-score {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: 6px;
  color: var(--text-muted, #586d84);
  font-size: 13px;
  font-variant-numeric: tabular-nums;
}

.record-score strong {
  color: #1e674f;
  font-size: 30px;
  line-height: 1;
}

.record-score :deep(.el-tag) {
  align-self: center;
  margin-inline-start: 2px;
}

.record-summary {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
  margin: 14px 0 10px;
}

.record-summary > div {
  padding: 9px 11px;
  border: 1px solid #e4ecf4;
  border-radius: 8px;
  background: #f6f8fb;
}

.record-summary dt,
.answer-comparison dt {
  margin-bottom: 2px;
  color: var(--text-muted, #586d84);
  font-size: 12px;
}

.record-summary dd {
  margin: 0;
  color: #233f5b;
  font-size: 16px;
  font-weight: 650;
  font-variant-numeric: tabular-nums;
}

.record-meta {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 5px 14px;
  margin-bottom: 12px;
  color: var(--text-muted, #586d84);
  font-size: 12px;
}

.record-index {
  position: sticky;
  top: 0;
  z-index: 2;
  border: 1px solid #dde6ef;
  border-radius: 8px;
  background: #fff;
  box-shadow: 0 3px 10px #183c5c0d;
}

.record-index summary {
  display: flex;
  align-items: center;
  gap: 7px;
  min-height: 42px;
  padding: 8px 11px;
  color: #25415c;
  font-size: 13px;
  list-style: none;
  cursor: pointer;
}

.record-index summary::-webkit-details-marker {
  display: none;
}

.record-index summary::after {
  width: 6px;
  height: 6px;
  transform: rotate(45deg);
  border-inline-end: 1.5px solid currentColor;
  border-block-end: 1.5px solid currentColor;
  content: '';
}

.record-index[open] summary::after {
  transform: rotate(225deg);
}

.record-index summary > span {
  margin-inline-start: auto;
  color: var(--text-muted, #586d84);
  text-align: right;
  font-size: 12px;
}

.record-index-scroll {
  max-height: min(28vh, 230px);
  overflow: auto;
  overscroll-behavior: contain;
  padding: 4px 11px 11px;
  border-top: 1px solid #edf2f7;
}

.record-index-scroll :deep(.review-index-buttons) {
  margin: 6px 0 0;
}

.record-index-scroll :deep(.review-index-buttons > span:first-child) {
  display: none;
}

.record-index-scroll :deep(.el-button) {
  min-width: 38px;
  min-height: 36px;
}

.record-question {
  scroll-margin-top: 57px;
  padding: 15px;
  margin-top: 13px;
  border: 1px solid #e4ecf4;
  border-radius: 9px;
  background: #f7f9fc;
  overflow-wrap: anywhere;
}

.record-index summary:focus-visible,
.record-question:focus-visible {
  outline: 2px solid var(--el-color-primary);
  outline-offset: -2px;
}

.question-head,
.question-tags,
.rubric-heading {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px 8px;
}

.question-head,
.rubric-heading {
  justify-content: space-between;
}

.question-tags h3 {
  margin: 0;
  color: #274763;
  font-size: 14px;
}

.question-score {
  color: #385572;
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.question-stem {
  margin: 9px 0;
  color: #2a435b;
  font-size: 14px;
  line-height: 1.65;
  white-space: pre-wrap;
}

.answer-comparison {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 7px;
  margin: 0;
}

.answer-comparison > div {
  min-width: 0;
  padding: 7px 9px;
  border: 1px solid #e5edf5;
  border-radius: 6px;
  background: #fff;
}

.answer-comparison dd {
  margin: 0;
  color: #334e68;
  font-size: 13px;
  line-height: 1.6;
  white-space: pre-wrap;
}

.answer-comparison .muted {
  color: var(--text-muted, #586d84);
}

.practical-answer {
  grid-template-columns: minmax(0, 1fr);
}

.rubric-list {
  display: grid;
  gap: 7px;
  padding: 0;
  margin: 9px 0 0;
  list-style: none;
}

.rubric-item {
  display: grid;
  gap: 5px;
  min-width: 0;
  padding: 9px;
  border: 1px solid #e3ebf4;
  border-radius: 7px;
  background: #fff;
  color: #334e68;
  font-size: 13px;
}

.rubric-note {
  margin: 0;
  color: var(--text-muted, #586d84);
  font-size: 12px;
  white-space: pre-wrap;
}

.question-analysis {
  margin: 9px 0 0;
  color: #425d77;
  font-size: 13px;
  line-height: 1.65;
  white-space: pre-wrap;
}

@media (max-width: 540px) {
  :global(.camp-record-drawer .el-drawer__header),
  :global(.camp-record-drawer .el-drawer__body) {
    padding-inline: 13px;
  }

  .record-head h2 {
    font-size: 18px;
  }

  .record-summary > div {
    padding: 7px 8px;
  }

  .record-question {
    padding: 11px;
  }

  .answer-comparison {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>

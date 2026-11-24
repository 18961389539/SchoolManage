<script setup lang="ts">
import { computed, reactive, ref, useId, watch } from 'vue'
import { ArrowDown } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { api, dateTime, messageOf, post } from '../api'
import { attemptStatusLabel, attemptStatusType, examQuestionTypeLabel, optionLetters, selectedText } from '../exam'
import type { AttemptReview, AttemptReviewQuestion } from '../types'
import { caseFieldLabel, type CaseField } from '../caseWork'
import QuestionReviewIndex from './QuestionReviewIndex.vue'

const props = defineProps<{
  modelValue: boolean
  endpoint: string
  gradePath?: string
  title?: string
}>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; graded: [] }>()
const questionPrefix = useId()

const visible = computed({
  get: () => props.modelValue,
  set: value => emit('update:modelValue', value)
})

const loading = ref(false)
const error = ref('')
const review = ref<AttemptReview | null>(null)
const grading = ref(false)
const indexPanel = ref<HTMLDetailsElement>()
let loadRequest = 0
const gradeForm = reactive<Record<string, { score: number | null; comment: string }>>({})

const practicalQuestions = computed(() =>
  (review.value?.questions || []).filter(question => question.type === 'practical')
)
const canGrade = computed(
  () => !!props.gradePath && practicalQuestions.value.length > 0 && review.value?.status !== 'inProgress'
)
const projectedTotal = computed(() => {
  if (!review.value) return 0
  return (
    review.value.objectiveScore +
    practicalQuestions.value.reduce((sum, question) => sum + Number(gradeForm[question.paperQuestionId]?.score ?? 0), 0)
  )
})

async function load() {
  const request = ++loadRequest
  loading.value = true
  error.value = ''
  try {
    const data = await api<AttemptReview>(props.endpoint)
    if (request !== loadRequest) return
    review.value = data
    Object.keys(gradeForm).forEach(key => delete gradeForm[key])
    for (const question of data.questions.filter(item => item.type === 'practical')) {
      gradeForm[question.paperQuestionId] = {
        score: question.scoreAwarded ?? null,
        comment: question.comment || ''
      }
    }
  } catch (failure) {
    if (request === loadRequest) {
      error.value = messageOf(failure)
      review.value = null
    }
  } finally {
    if (request === loadRequest) loading.value = false
  }
}

function focusInDrawer(target: HTMLElement, top: number) {
  const content = target.closest('.attempt-review-content')
  if (content) content.scrollTop += target.getBoundingClientRect().top - content.getBoundingClientRect().top - top
  target.focus({ preventScroll: true })
}

function jumpFromIndex(event: MouseEvent) {
  const button = event.target instanceof Element ? event.target.closest('button') : null
  const target = document.getElementById(button?.getAttribute('aria-controls') || '')
  if (!target || !indexPanel.value) return
  event.stopPropagation()
  indexPanel.value.open = false
  focusInDrawer(target, 52)
}

function focusGrade(questionId: string, field: 'score' | 'comment') {
  const fieldInput = document.getElementById(`${questionPrefix}-${questionId}-${field}`)
  if (fieldInput) focusInDrawer(fieldInput, 72)
}

function configurationValue(value: string | null, field: CaseField) {
  if (value === null) return '未提交'
  if (field === 'enabled') return value === 'true' ? '启用' : '停用'
  return value || '（空）'
}

function automaticOverrideDecision(question: AttemptReviewQuestion, score: number, comment: string) {
  const reason = comment.trim()
  if (question.scoreAwarded !== null && score === question.scoreAwarded && !reason) return 'skip'
  return reason ? 'include' : 'need-reason'
}

function gradeAnswers() {
  const answers: { paperQuestionId: string; score: number; comment: string }[] = []
  for (const question of practicalQuestions.value) {
    const entry = gradeForm[question.paperQuestionId]
    const score = Number(entry?.score ?? NaN)
    const comment = entry?.comment || ''
    if (!Number.isFinite(score) || score < 0 || score > question.maxScore) {
      ElMessage.warning(`“${question.stem.slice(0, 20)}”的评分应在 0–${question.maxScore} 分之间`)
      focusGrade(question.paperQuestionId, 'score')
      return
    }
    if (comment.length > 500) {
      ElMessage.warning('评语不能超过 500 个字符')
      focusGrade(question.paperQuestionId, 'comment')
      return
    }
    if (question.automaticPractical) {
      const decision = automaticOverrideDecision(question, score, comment)
      if (decision === 'skip') continue
      if (decision === 'need-reason') {
        ElMessage.warning('覆盖自动评分时请填写原因')
        focusGrade(question.paperQuestionId, 'comment')
        return
      }
    }
    answers.push({ paperQuestionId: question.paperQuestionId, score, comment })
  }
  if (!answers.length) {
    ElMessage.warning('请先调整自动评分并填写原因')
    return
  }
  return answers
}

async function saveGrade() {
  const gradePath = props.gradePath
  if (!canGrade.value || !gradePath || loading.value || grading.value) return
  const answers = gradeAnswers()
  if (!answers) return
  const endpoint = props.endpoint
  grading.value = true
  try {
    await post(gradePath, { answers })
    ElMessage.success('批阅已保存')
    if (props.modelValue && props.endpoint === endpoint) await load()
    emit('graded')
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    grading.value = false
  }
}

function chosenText(question: AttemptReviewQuestion): string {
  return selectedText({ options: question.options, selected: question.selected })
}

function correctText(question: AttemptReviewQuestion): string {
  return selectedText({ options: question.options, answers: question.answers })
}

watch(
  () => [props.modelValue, props.endpoint] as const,
  ([open, endpoint]) => {
    if (open && endpoint) {
      review.value = null
      void load()
    } else {
      loadRequest++
      loading.value = false
    }
  },
  { immediate: true }
)
</script>

<template>
  <el-drawer
    v-model="visible"
    :title="title || '答题复盘'"
    size="min(880px, 96vw)"
    header-class="attempt-review-header"
    body-class="attempt-review-content"
    append-to-body
    footer-class="attempt-review-footer"
  >
    <div v-loading="loading" class="review-body">
      <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
      <template v-if="review">
        <section class="review-summary" aria-label="成绩概览">
          <div class="review-person">
            <strong>{{ review.studentName }}</strong>
            <small>{{ review.studentNumber }} · {{ review.className || '未分班' }}</small>
            <div class="review-state">
              <el-tag :type="attemptStatusType(review.status)" size="small">{{
                attemptStatusLabel(review.status)
              }}</el-tag>
              <el-tag v-if="review.autoSubmitted" type="warning" size="small" effect="plain">超时自动提交</el-tag>
              <el-tag v-if="review.pendingPractical" type="warning" size="small" effect="plain">
                {{ review.pendingPractical }} 道实操题待批阅
              </el-tag>
            </div>
          </div>
          <dl class="review-total">
            <dt>总分</dt>
            <dd>
              <strong>{{ review.totalScore }}</strong><small> / {{ review.maxScore }}</small>
            </dd>
          </dl>
          <dl class="review-subtotals">
            <div>
              <dt>客观分</dt>
              <dd>{{ review.objectiveScore }}</dd>
            </div>
            <div>
              <dt>实操分</dt>
              <dd>{{ review.practicalScore }}</dd>
            </div>
          </dl>
        </section>
        <div class="review-meta">
          <span>开始 {{ dateTime(review.startedAt) }}</span>
          <span>提交 {{ dateTime(review.submittedAt) }}</span>
        </div>

        <details v-if="review.questions.length" ref="indexPanel" class="review-index-panel">
          <summary>
            <strong>题号索引</strong>
            <span>{{ review.questions.length }} 道题</span>
            <el-icon aria-hidden="true"><ArrowDown /></el-icon>
          </summary>
          <div class="review-index-grid" @click.capture="jumpFromIndex">
            <QuestionReviewIndex :questions="review.questions" :target-prefix="questionPrefix" />
          </div>
        </details>
        <section
          v-for="(question, index) in review.questions"
          :id="`${questionPrefix}-${index}`"
          :key="question.paperQuestionId"
          class="review-question"
          :aria-labelledby="`${questionPrefix}-heading-${index}`"
          tabindex="-1"
        >
          <header>
            <div class="review-question-title">
              <span class="review-index">{{ index + 1 }}</span>
              <el-tag size="small" effect="plain">{{ examQuestionTypeLabel(question.type) }}</el-tag>
              <span class="review-score">{{ question.maxScore }} 分</span>
            </div>
            <div class="review-result">
              <el-tag v-if="question.type !== 'practical'" :type="question.correct ? 'success' : 'danger'" size="small">
                {{ question.correct ? '正确' : question.answered ? '错误' : '未作答' }}
              </el-tag>
              <el-tag v-else :type="question.scoreAwarded === null ? 'warning' : 'success'" size="small">
                {{ question.scoreAwarded === null ? '待批阅' : `得分 ${question.scoreAwarded} / ${question.maxScore}` }}
              </el-tag>
            </div>
          </header>
          <h4 :id="`${questionPrefix}-heading-${index}`">{{ question.stem }}</h4>
          <div v-if="question.options.length" class="review-options">
            <div
              v-for="(option, optionIndex) in question.options"
              :key="optionIndex"
              class="review-option"
              :class="{
                correct: question.answers.includes(optionIndex),
                wrong: question.selected.includes(optionIndex) && !question.answers.includes(optionIndex)
              }"
            >
              <span>{{ optionLetters[optionIndex] || optionIndex + 1 }}</span>
              <p>{{ option }}</p>
            </div>
          </div>
          <dl v-if="question.type !== 'practical'" class="review-answers">
            <div>
              <dt>学生选择</dt>
              <dd>{{ chosenText(question) }}</dd>
            </div>
            <div>
              <dt>正确答案</dt>
              <dd>{{ correctText(question) }}</dd>
            </div>
          </dl>
          <div v-else-if="question.automaticPractical" class="review-practical">
            <strong>实操自动评分 · {{ question.scoreAwarded ?? '—' }} / {{ question.maxScore }} 分</strong>
            <ul v-if="question.practicalDetails?.length" class="review-rubric" aria-label="实操评分项">
              <li v-for="(row, ruleIndex) in question.practicalDetails" :key="ruleIndex" class="rubric-rule">
                <header class="rubric-rule-heading">
                  <div>
                    <strong>{{ row.name }}</strong>
                    <div class="rubric-device">
                      {{ row.deviceName }} {{ row.interfaceName }} · {{ caseFieldLabel[row.field] }}
                    </div>
                  </div>
                  <span class="rubric-score" :class="{ 'is-matched': row.matched }">
                    <span class="visually-hidden">评分项分值</span>{{ row.matched ? row.score : 0 }} /
                    {{ row.score }} 分
                  </span>
                </header>
                <dl class="rubric-comparison">
                  <div>
                    <dt>预期</dt>
                    <dd>{{ configurationValue(row.expected, row.field) }}</dd>
                  </div>
                  <div>
                    <dt>实际</dt>
                    <dd>{{ configurationValue(row.actual, row.field) }}</dd>
                  </div>
                </dl>
                <div v-if="row.guidance" class="rubric-guidance">
                  <strong>排错指导</strong><span>{{ row.guidance }}</span>
                </div>
              </li>
            </ul>
            <p v-else-if="question.scoreAwarded !== null">考试结束后可查看评分表。</p>
          </div>
          <div v-else class="review-practical">
            <span>学生作答</span>
            <p>{{ question.textAnswer || '未作答' }}</p>
          </div>
          <div v-if="question.analysis" class="review-analysis">
            <span>题目解析</span>
            <p>{{ question.analysis }}</p>
          </div>
          <div v-if="canGrade && question.type === 'practical'" class="grading-box">
            <div class="grading-score">
              <label :for="`${questionPrefix}-${question.paperQuestionId}-score`">{{
                question.automaticPractical ? '覆盖分（0–' + question.maxScore + '）' : `评分（0–${question.maxScore}）`
              }}</label>
              <el-input-number
                :id="`${questionPrefix}-${question.paperQuestionId}-score`"
                v-model="gradeForm[question.paperQuestionId].score"
                :disabled="grading"
                :min="0"
                :max="question.maxScore"
                :step="1"
                size="small"
              />
            </div>
            <div class="grading-comment">
              <label :for="`${questionPrefix}-${question.paperQuestionId}-comment`">{{
                question.automaticPractical ? '覆盖原因（必填）' : '评语（可选）'
              }}</label>
              <el-input
                :id="`${questionPrefix}-${question.paperQuestionId}-comment`"
                v-model="gradeForm[question.paperQuestionId].comment"
                :disabled="grading"
                type="textarea"
                :rows="2"
                maxlength="500"
                show-word-limit
                :placeholder="question.automaticPractical ? '说明为何调整自动评分' : '评语会展示给学生（可选）'"
              />
            </div>
          </div>
          <div v-else-if="question.type === 'practical' && question.comment" class="teacher-comment">
            <span>教师评语</span>
            <p>{{ question.comment }}</p>
          </div>
        </section>
      </template>
    </div>
    <template v-if="canGrade && review" #footer>
      <div class="review-footer">
        <span aria-live="polite"
          >批阅后总分 <strong>{{ projectedTotal }}</strong> / {{ review.maxScore }}</span
        >
        <el-button type="primary" :loading="grading" :disabled="loading" @click="saveGrade">保存批阅</el-button>
      </div>
    </template>
  </el-drawer>
</template>

<style scoped>
:global(.attempt-review-header) {
  margin-bottom: 0;
  padding-bottom: 8px;
}

:global(.attempt-review-content) {
  overscroll-behavior: contain;
}

:global(.attempt-review-footer) {
  padding-block: 12px;
  border-top: 1px solid var(--el-border-color-light);
  background: #fff;
}

.review-body {
  min-height: 88px;
  color: var(--el-text-color-primary);
}

.review-summary {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  grid-template-areas:
    'identity total'
    'subtotals total';
  gap: 12px;
  padding: 15px;
  background: var(--el-fill-color-light);
  border: 1px solid var(--el-border-color-light);
  border-radius: 10px;
}

.review-person {
  grid-area: identity;
  display: grid;
  gap: 3px;
  min-width: 0;
  overflow-wrap: anywhere;
}

.review-person strong {
  font-size: 18px;
}

.review-person small,
.review-summary dt,
.review-total small {
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.review-state {
  display: flex;
  flex-wrap: wrap;
  gap: 5px 6px;
  margin-top: 3px;
}

.review-subtotals {
  grid-area: subtotals;
  display: flex;
  flex-wrap: wrap;
  gap: 6px 18px;
  margin: 0;
}

.review-subtotals > div {
  display: flex;
  align-items: baseline;
  gap: 6px;
}

.review-subtotals dd,
.review-total dd {
  margin: 0;
  font-variant-numeric: tabular-nums;
}

.review-subtotals dd {
  font-weight: 650;
}

.review-total {
  grid-area: total;
  align-self: start;
  margin: 0;
  padding: 8px 13px;
  border-radius: 8px;
  background: var(--el-color-primary-light-9);
  color: var(--el-color-primary-dark-2);
  text-align: right;
}

.review-total strong {
  font-size: 26px;
}

.review-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 16px;
  margin: 9px 2px 13px;
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.review-index-panel {
  position: sticky;
  top: 0;
  z-index: 2;
  margin-bottom: 14px;
  border: 1px solid var(--el-border-color);
  border-radius: 8px;
  background: #fff;
  /* Cover the drawer body padding so scrolled questions do not flash above the sticky index. */
  box-shadow: 0 -22px 0 #fff;
}

.review-index-panel summary {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 40px;
  padding: 5px 12px;
  border-radius: inherit;
  list-style: none;
  cursor: pointer;
}

.review-index-panel summary::-webkit-details-marker {
  display: none;
}

.review-index-panel summary:focus-visible,
.review-question:focus-visible {
  outline: 2px solid var(--el-color-primary);
  outline-offset: -2px;
}

.review-index-panel summary span {
  margin-left: auto;
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.review-index-panel[open] summary .el-icon {
  transform: rotate(180deg);
}

.review-index-grid {
  max-height: min(28vh, 180px);
  padding: 0 12px 8px;
  overflow: auto;
  overscroll-behavior: contain;
}

.review-index-grid :deep(.review-index-buttons) {
  margin: 3px;
}

.review-index-grid :deep(.review-index-buttons > span) {
  display: none;
}

.review-question {
  padding: 16px;
  margin-bottom: 14px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 10px;
}

.review-question > header {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  align-items: center;
  gap: 6px 10px;
}

.review-question-title {
  display: flex;
  align-items: center;
  gap: 8px;
}

.review-index {
  display: inline-grid;
  place-items: center;
  min-width: 24px;
  height: 24px;
  padding: 0 4px;
  border-radius: 6px;
  background: var(--el-fill-color-light);
  color: var(--el-text-color-primary);
  font-size: 12px;
}

.review-score {
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.review-question h4 {
  margin: 10px 0 12px;
  font-size: 14px;
  line-height: 1.6;
  overflow-wrap: anywhere;
}

.review-options {
  display: grid;
  gap: 6px;
}

.review-option {
  display: grid;
  grid-template-columns: 24px minmax(0, 1fr);
  gap: 8px;
  align-items: center;
  padding: 7px 10px;
  border-radius: 7px;
  border: 1px solid var(--el-border-color-light);
}

.review-option > span {
  display: inline-grid;
  place-items: center;
  width: 22px;
  height: 22px;
  border-radius: 50%;
  background: var(--el-fill-color-light);
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.review-option p {
  margin: 0;
  color: var(--el-text-color-primary);
  font-size: 13px;
  overflow-wrap: anywhere;
}

.review-option.correct {
  border-color: #a9d6cb;
  background: #f2faf7;
}

.review-option.wrong {
  border-color: #ecc5c5;
  background: #fdf5f5;
}

.review-answers {
  display: grid;
  gap: 4px;
  margin: 12px 0 0;
}

.review-answers > div {
  display: grid;
  grid-template-columns: 60px minmax(0, 1fr);
  gap: 8px;
}

.review-answers dt,
.review-answers dd {
  font-size: 12px;
}

.review-answers dt {
  color: var(--el-text-color-regular);
}

.review-answers dd {
  margin: 0;
  line-height: 1.5;
  overflow-wrap: anywhere;
}

.review-practical,
.review-analysis,
.teacher-comment {
  margin-top: 12px;
}

.review-practical > span,
.review-analysis > span,
.teacher-comment > span {
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.review-practical p,
.review-analysis p,
.teacher-comment p {
  margin: 5px 0 0;
  padding: 9px 11px;
  border-radius: 7px;
  background: var(--el-fill-color-light);
  color: var(--el-text-color-primary);
  font-size: 13px;
  line-height: 1.6;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.teacher-comment p {
  background: #fdf9ef;
  border: 1px solid #f1e5c9;
}

.review-rubric {
  display: grid;
  gap: 8px;
  margin: 9px 0 0;
  padding: 0;
  list-style: none;
}

.rubric-rule {
  padding: 10px 12px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
}

.rubric-rule-heading {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 8px;
  line-height: 1.45;
}

.rubric-rule-heading > div {
  min-width: 0;
  overflow-wrap: anywhere;
}

.rubric-device {
  margin-top: 2px;
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.rubric-score {
  flex: none;
  padding: 3px 6px;
  border-radius: 5px;
  background: var(--el-fill-color-light);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.rubric-score.is-matched {
  background: var(--el-color-primary-light-9);
  color: var(--el-color-primary-dark-2);
}

.rubric-comparison {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 7px;
  margin: 8px 0 0;
}

.rubric-comparison > div,
.rubric-guidance {
  padding: 6px 8px;
  border-radius: 6px;
  background: var(--el-fill-color-light);
  font-size: 12px;
}

.rubric-comparison dt {
  margin-bottom: 2px;
  color: var(--el-text-color-regular);
}

.rubric-comparison dd {
  margin: 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.rubric-guidance {
  display: flex;
  align-items: baseline;
  gap: 7px;
  margin-top: 7px;
}

.rubric-guidance strong {
  flex: none;
}

.rubric-guidance span {
  min-width: 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.grading-box {
  display: grid;
  gap: 10px;
  margin-top: 14px;
  padding-top: 12px;
  border-top: 1px solid var(--el-border-color-light);
}

.grading-score {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 10px;
}

.grading-box label {
  color: var(--el-text-color-primary);
  font-size: 13px;
  font-weight: 550;
}

.grading-comment {
  display: grid;
  gap: 5px;
}

.review-footer {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  align-items: center;
  gap: 6px 12px;
}

.review-footer span {
  color: var(--el-text-color-regular);
  font-size: 13px;
  font-variant-numeric: tabular-nums;
}

.review-footer strong {
  color: var(--el-text-color-primary);
  font-size: 20px;
}

@media (max-width: 550px) {
  .review-summary,
  .review-question {
    padding: 12px;
  }

  .review-total {
    padding: 6px 9px;
  }

  .review-total strong {
    font-size: 22px;
  }

  .review-footer .el-button {
    min-height: 40px;
  }
}
</style>

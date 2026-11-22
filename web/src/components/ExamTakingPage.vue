<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ArrowLeft, Check, Clock, Document, Grid, Refresh } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, dateTime, messageOf, post } from '../api'
import {
  attemptStatusLabel,
  attemptStatusType,
  countdownText,
  examQuestionTypeLabel,
  examQuestionTypeOptions,
  optionLetters
} from '../exam'
import { durationText } from '../teaching'
import { parseApiDate } from '../time'
import type { ExamHallDetail, ExamQuestionType, ExamSubmitAnswer, ExamSubmitResult, ExamTakingQuestion } from '../types'
import ExamPracticalPanel from './ExamPracticalPanel.vue'

interface AnswerState {
  selected: number[]
  text: string
}

const props = defineProps<{ examId: string }>()
const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()

const loading = ref(true)
const error = ref('')
const detail = ref<ExamHallDetail | null>(null)
const answers = reactive<Record<string, AnswerState>>({})
const activeType = ref<'all' | ExamQuestionType>('all')
const currentId = ref('')
const indexOpen = ref(false)
const starting = ref(false)
const submitting = ref(false)
const completed = ref<ExamSubmitResult | null>(null)
const saveState = ref<'' | 'saving' | 'saved' | 'error'>('')
const lastSavedAt = ref<Date | null>(null)
const remaining = ref(0)
const practicalVisible = ref(false)
const practicalId = ref('')
const practicalDirty = ref(false)
let hydrating = false
let dirty = false
let autoTriggered = false
let ticker: number | undefined
let autosaveTimer: number | undefined
let debounceTimer: number | undefined
let serverOffset = 0

const questions = computed<ExamTakingQuestion[]>(() => detail.value?.questions || [])
const questionNumbers = computed(
  () => new Map(questions.value.map((question, index) => [question.paperQuestionId, index + 1]))
)
const practicalQuestion = computed(() => questions.value.find(item => item.paperQuestionId === practicalId.value))
const inProgress = computed(() => detail.value?.attempt?.status === 'inProgress' && questions.value.length > 0)
const typeTabs = computed(() => {
  const present = new Set(questions.value.map(question => question.type))
  return [
    { value: 'all' as const, label: '全部' },
    ...examQuestionTypeOptions.filter(option => present.has(option.value))
  ]
})
const filteredQuestions = computed(() =>
  activeType.value === 'all' ? questions.value : questions.value.filter(question => question.type === activeType.value)
)
const answeredIds = computed(() => {
  const ids = new Set<string>()
  for (const question of questions.value) {
    const answer = answers[question.paperQuestionId]
    if (!answer) continue
    if (
      question.automaticPractical
        ? question.practicalSaved
        : question.type === 'practical'
          ? answer.text.trim()
          : answer.selected.length
    )
      ids.add(question.paperQuestionId)
  }
  return ids
})
const answeredCount = computed(() => answeredIds.value.size)
const unanswered = computed(() => questions.value.length - answeredCount.value)
const progressPercent = computed(() =>
  questions.value.length ? Math.round((answeredCount.value * 100) / questions.value.length) : 0
)
const saveText = computed(() => {
  if (saveState.value === 'saving') return '正在保存…'
  if (saveState.value === 'error') return '自动保存失败'
  if (saveState.value === 'saved')
    return `已保存 ${lastSavedAt.value ? lastSavedAt.value.toLocaleTimeString('zh-CN', { hour12: false }) : ''}`
  return '答案将自动保存'
})
const attemptSummary = computed(() => detail.value?.attempt || null)
const finishedAttempt = computed(() =>
  attemptSummary.value && attemptSummary.value.status !== 'inProgress' ? attemptSummary.value : null
)

function questionNumber(question: ExamTakingQuestion): number {
  return questionNumbers.value.get(question.paperQuestionId) ?? 0
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    const data = await api<ExamHallDetail>(`/exam-hall/${props.examId}`)
    serverOffset = parseApiDate(data.serverNow).getTime() - Date.now()
    detail.value = data
    stopClocks()
    if (data.attempt?.status === 'inProgress' && data.questions) {
      await hydrate(data.questions)
      syncRemaining()
      startClocks()
    } else if (data.attempt) {
      remaining.value = 0
    }
  } catch (failure) {
    error.value = messageOf(failure)
    detail.value = null
  } finally {
    loading.value = false
  }
}

async function hydrate(list: ExamTakingQuestion[]) {
  hydrating = true
  for (const key of Object.keys(answers)) delete answers[key]
  for (const question of list) {
    answers[question.paperQuestionId] = {
      selected: question.selected.slice(),
      text: question.textAnswer || ''
    }
  }
  currentId.value = list[0]?.paperQuestionId || ''
  activeType.value = 'all'
  indexOpen.value = false
  autoTriggered = false
  dirty = false
  await nextTick()
  hydrating = false
}

function startClocks() {
  stopClocks()
  ticker = window.setInterval(() => {
    syncRemaining()
    if (remaining.value <= 0 && inProgress.value && !autoTriggered && !submitting.value) {
      autoTriggered = true
      void submit(true)
    }
  }, 1000)
  autosaveTimer = window.setInterval(() => {
    if (dirty && inProgress.value && !submitting.value) void saveNow()
  }, 30000)
}

function stopClocks() {
  if (ticker !== undefined) {
    window.clearInterval(ticker)
    ticker = undefined
  }
  if (autosaveTimer !== undefined) {
    window.clearInterval(autosaveTimer)
    autosaveTimer = undefined
  }
  if (debounceTimer !== undefined) {
    window.clearTimeout(debounceTimer)
    debounceTimer = undefined
  }
}

function syncRemaining() {
  const deadline = detail.value?.attempt?.deadline
  remaining.value = deadline
    ? Math.max(0, Math.ceil((parseApiDate(deadline).getTime() - Date.now() - serverOffset) / 1000))
    : 0
}

function setSingle(questionId: string, value: string | number | boolean | undefined) {
  const answer = answers[questionId]
  if (!answer) return
  answer.selected = value === undefined || value === null || value === '' ? [] : [Number(value)]
}

function payload(): ExamSubmitAnswer[] {
  return questions.value
    .filter(question => !question.automaticPractical)
    .map(question => ({
      paperQuestionId: question.paperQuestionId,
      selected: answers[question.paperQuestionId]?.selected || [],
      text: answers[question.paperQuestionId]?.text || ''
    }))
}

function scheduleSave() {
  if (debounceTimer !== undefined) window.clearTimeout(debounceTimer)
  debounceTimer = window.setTimeout(() => void saveNow(), 800)
}

async function saveNow() {
  if (!inProgress.value || submitting.value) return
  if (debounceTimer !== undefined) {
    window.clearTimeout(debounceTimer)
    debounceTimer = undefined
  }
  dirty = false
  saveState.value = 'saving'
  try {
    await post(`/exam-hall/${props.examId}/answers`, { answers: payload() })
    saveState.value = 'saved'
    lastSavedAt.value = new Date()
  } catch (failure) {
    saveState.value = 'error'
    const status = (failure as { status?: number })?.status
    if (status === 409) {
      ElMessage.warning(messageOf(failure))
      await load()
    }
  }
}

async function scrollToQuestion(question: ExamTakingQuestion) {
  if (activeType.value !== 'all' && activeType.value !== question.type) activeType.value = 'all'
  indexOpen.value = false
  await nextTick()
  currentId.value = question.paperQuestionId
  const target = document.getElementById(`exam-question-${question.paperQuestionId}`)
  target?.focus({ preventScroll: true })
  target?.scrollIntoView({
    behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth',
    block: 'start'
  })
}

function openPractical(question: ExamTakingQuestion) {
  practicalId.value = question.paperQuestionId
  practicalDirty.value = false
  practicalVisible.value = true
}

async function closePractical(done: () => void) {
  if (practicalDirty.value) {
    try {
      await ElMessageBox.confirm('当前设备配置尚未保存。离开后将清除这些修改。', '离开实操', {
        confirmButtonText: '离开实操',
        cancelButtonText: '继续作答',
        type: 'warning'
      })
    } catch {
      return
    }
  }
  done()
}

async function start() {
  starting.value = true
  try {
    await post(`/exam-hall/${props.examId}/start`)
    ElMessage.success('考试已开始，计时启动')
    await load()
  } catch (failure) {
    ElMessage.error(messageOf(failure))
    await load()
  } finally {
    starting.value = false
  }
}

async function submit(auto = false) {
  if (submitting.value || !inProgress.value) return
  if (!auto) {
    const pending = unanswered.value
    try {
      await ElMessageBox.confirm(
        pending
          ? `还有 ${pending} 道题未作答，交卷后不能修改答案。确定交卷吗？`
          : '所有题目均已作答，交卷后不能修改答案。确定交卷吗？',
        '提交试卷',
        { type: 'warning', confirmButtonText: '确认交卷', cancelButtonText: '继续作答' }
      )
    } catch {
      return
    }
  }
  submitting.value = true
  autoTriggered = true
  if (debounceTimer !== undefined) {
    window.clearTimeout(debounceTimer)
    debounceTimer = undefined
  }
  try {
    const result = await post<ExamSubmitResult>(`/exam-hall/${props.examId}/submit`, { answers: payload() })
    completed.value = result
    practicalVisible.value = false
    stopClocks()
    window.scrollTo({ top: 0, behavior: 'smooth' })
  } catch (failure) {
    ElMessage.warning(messageOf(failure))
    await load()
    if (!inProgress.value) practicalVisible.value = false
  } finally {
    submitting.value = false
  }
}

function beforeUnload(event: BeforeUnloadEvent) {
  if (!inProgress.value || completed.value) return
  event.preventDefault()
  event.returnValue = ''
}

watch(
  answers,
  () => {
    if (hydrating || !inProgress.value || submitting.value) return
    dirty = true
    saveState.value = ''
    scheduleSave()
  },
  { deep: true }
)

watch(activeType, () => {
  if (filteredQuestions.value.length) currentId.value = filteredQuestions.value[0].paperQuestionId
})

onMounted(() => {
  void load()
  window.addEventListener('beforeunload', beforeUnload)
})

onBeforeUnmount(() => {
  stopClocks()
  window.removeEventListener('beforeunload', beforeUnload)
})

function goBack() {
  emit('navigate', 'my-exams')
}

function goRecords() {
  emit('navigate', 'exam-records')
}
</script>

<template>
  <div v-loading="loading" class="exam-taking-page">
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice">
      <template #default>
        <p>{{ error }}</p>
        <el-button size="small" @click="goBack">返回我的考试</el-button>
      </template>
    </el-alert>

    <template v-if="detail && !error">
      <section class="take-heading">
        <el-button link :icon="ArrowLeft" @click="goBack">返回我的考试</el-button>
        <h1>{{ detail.name }}</h1>
        <p>
          {{ detail.paperName }} · {{ detail.questionCount }} 题 / {{ detail.totalScore }} 分 · 限时
          {{ durationText(detail.durationMinutes) }}
        </p>
      </section>

      <section v-if="completed" class="surface finish-panel">
        <div class="finish-icon">
          <el-icon><Check /></el-icon>
        </div>
        <h2>{{ completed.autoSubmitted ? '考试时间已到，试卷已自动提交' : '试卷提交成功' }}</h2>
        <p v-if="completed.pendingPractical">文字实操题待批阅，最终成绩将在批阅后显示。</p>
        <p v-else>自动评分已完成。可在考试动态中查看成绩。</p>
        <div class="finish-scores">
          <div>
            <span>客观分</span><strong>{{ completed.objectiveScore }}</strong>
          </div>
          <div>
            <span>实操分</span><strong>{{ completed.practicalScore }}</strong>
          </div>
          <div>
            <span>当前总分</span
            ><strong
              >{{ completed.totalScore }}<small>/{{ completed.maxScore }}</small></strong
            >
          </div>
          <div>
            <span>状态</span
            ><el-tag :type="attemptStatusType(completed.status)">{{ attemptStatusLabel(completed.status) }}</el-tag>
          </div>
        </div>
        <div class="finish-actions">
          <el-button @click="goBack">返回我的考试</el-button>
          <el-button type="primary" @click="goRecords">查看考试动态</el-button>
        </div>
      </section>

      <template v-else-if="inProgress">
        <div class="take-layout">
          <aside class="take-side" aria-label="考试状态和导航">
            <div class="surface take-side-card">
              <div class="side-clock" :class="{ urgent: remaining <= 300 }">
                <el-icon><Clock /></el-icon>
                <span>剩余时间</span>
                <strong role="timer">{{ countdownText(remaining) }}</strong>
              </div>
              <div class="side-save" :class="{ 'save-error': saveState === 'error' }">
                <span role="status">{{ saveText }}</span>
                <el-button v-if="saveState === 'error'" link type="primary" :icon="Refresh" @click="saveNow">
                  重试
                </el-button>
              </div>
              <div class="side-progress">
                <span>答题进度</span><strong>{{ answeredCount }} / {{ questions.length }}</strong>
                <el-progress :percentage="progressPercent" :stroke-width="4" :show-text="false" aria-hidden="true" />
              </div>
              <el-button
                :icon="Grid"
                class="index-toggle"
                :aria-expanded="indexOpen"
                aria-controls="exam-question-index"
                @click="indexOpen = !indexOpen"
              >
                题目索引
              </el-button>
              <div id="exam-question-index" class="index-panel" :class="{ 'is-open': indexOpen }">
                <div class="question-index" role="group" aria-label="题目索引">
                  <button
                    v-for="question in questions"
                    :key="question.paperQuestionId"
                    type="button"
                    class="index-dot"
                    :class="{
                      answered: answeredIds.has(question.paperQuestionId),
                      current: currentId === question.paperQuestionId
                    }"
                    :aria-current="currentId === question.paperQuestionId ? 'true' : undefined"
                    :title="`第 ${questionNumber(question)} 题 · ${answeredIds.has(question.paperQuestionId) ? '已作答' : '未作答'}`"
                    @click="scrollToQuestion(question)"
                  >
                    {{ questionNumber(question) }}
                  </button>
                </div>
                <div class="index-legend">
                  <span><i class="legend-dot" />未作答</span>
                  <span><i class="legend-dot answered" />已作答</span>
                  <span><i class="legend-dot current" />当前题</span>
                </div>
                <dl class="take-side-info">
                  <div>
                    <dt>
                      <el-icon><Document /></el-icon>考试时间
                    </dt>
                    <dd>{{ dateTime(detail.startAt) }} 至 {{ dateTime(detail.endAt) }}</dd>
                  </div>
                  <div>
                    <dt>
                      <el-icon><Clock /></el-icon>截止时间
                    </dt>
                    <dd>{{ dateTime(detail.attempt?.deadline) }}</dd>
                  </div>
                </dl>
              </div>
              <el-button type="primary" class="side-submit" :loading="submitting" @click="submit()">交卷</el-button>
            </div>
          </aside>
          <div class="take-main">
            <div class="take-toolbar">
              <el-radio-group v-model="activeType" size="small">
                <el-radio-button v-for="tab in typeTabs" :key="tab.value" :value="tab.value">
                  {{ tab.label }}
                </el-radio-button>
              </el-radio-group>
            </div>

            <article
              v-for="question in filteredQuestions"
              :id="`exam-question-${question.paperQuestionId}`"
              :key="question.paperQuestionId"
              class="surface take-question"
              tabindex="-1"
              :class="{ current: currentId === question.paperQuestionId }"
              @click="currentId = question.paperQuestionId"
              @focusin="currentId = question.paperQuestionId"
            >
              <header>
                <div class="take-question-title">
                  <span class="take-question-number">{{ questionNumber(question) }}</span>
                  <el-tag size="small" effect="plain">{{ examQuestionTypeLabel(question.type) }}</el-tag>
                  <span class="take-question-score">{{ question.score }} 分</span>
                </div>
                <el-tag v-if="answeredIds.has(question.paperQuestionId)" type="success" size="small" effect="plain">
                  已作答
                </el-tag>
              </header>
              <h3>{{ question.stem }}</h3>

              <el-radio-group
                v-if="question.type === 'single' || question.type === 'judge'"
                :model-value="answers[question.paperQuestionId]?.selected[0]"
                class="take-options"
                @change="setSingle(question.paperQuestionId, $event)"
              >
                <el-radio v-for="(option, index) in question.options" :key="index" :value="index" class="take-option">
                  <span class="take-option-letter">{{ optionLetters[index] || index + 1 }}</span>
                  <span class="take-option-text">{{ option }}</span>
                </el-radio>
              </el-radio-group>

              <el-checkbox-group
                v-else-if="question.type === 'multiple'"
                v-model="answers[question.paperQuestionId].selected"
                class="take-options"
              >
                <el-checkbox
                  v-for="(option, index) in question.options"
                  :key="index"
                  :value="index"
                  class="take-option"
                >
                  <span class="take-option-letter">{{ optionLetters[index] || index + 1 }}</span>
                  <span class="take-option-text">{{ option }}</span>
                </el-checkbox>
              </el-checkbox-group>

              <div v-else-if="question.automaticPractical" class="practical-entry">
                <el-button type="primary" plain @click="openPractical(question)">进入实操环境</el-button>
                <el-tag v-if="question.practicalSaved" type="success" effect="plain">已保存配置</el-tag>
              </div>
              <template v-else>
                <section v-if="question.practicalResources" class="practical-resources" aria-label="答题资源">
                  <h3>答题资源</h3>
                  <p>{{ question.practicalResources }}</p>
                </section>
                <el-input
                  v-model="answers[question.paperQuestionId].text"
                  type="textarea"
                  :rows="6"
                  maxlength="5000"
                  show-word-limit
                  placeholder="请按题目要求分点作答，教师将根据要点评分"
                />
              </template>
            </article>

            <div class="take-bottom">
              <span>交卷后不能修改答案；到时间系统会自动提交。</span>
              <el-button type="primary" size="large" :loading="submitting" @click="submit()">提交试卷</el-button>
            </div>
          </div>
        </div>
      </template>

      <section v-else-if="finishedAttempt" class="surface finish-panel">
        <div class="finish-icon done">
          <el-icon><Check /></el-icon>
        </div>
        <h2>本次考试已提交</h2>
        <p>
          状态：{{ attemptStatusLabel(finishedAttempt.status) }}
          <template v-if="finishedAttempt.status === 'submitted'">，包含待批阅的实操题。</template>
        </p>
        <div class="finish-scores">
          <div>
            <span>客观分</span><strong>{{ finishedAttempt.objectiveScore }}</strong>
          </div>
          <div>
            <span>实操分</span><strong>{{ finishedAttempt.practicalScore }}</strong>
          </div>
          <div>
            <span>总分</span
            ><strong
              >{{ finishedAttempt.totalScore }}<small>/{{ finishedAttempt.maxScore }}</small></strong
            >
          </div>
          <div>
            <span>提交时间</span>
            <p>{{ dateTime(finishedAttempt.submittedAt) }}</p>
          </div>
        </div>
        <div class="finish-actions">
          <el-button @click="goBack">返回我的考试</el-button>
          <el-button type="primary" @click="goRecords">查看答题复盘</el-button>
        </div>
      </section>

      <section v-else class="surface exam-intro">
        <div class="intro-tags">
          <el-tag v-if="detail.status !== 'open'" type="info">{{
            detail.status === 'scheduled' ? '等待开考' : '考试已结束'
          }}</el-tag>
          <el-tag v-else type="success">考试进行中</el-tag>
          <el-tag effect="plain">{{ detail.paperName }}</el-tag>
          <el-tag effect="plain">{{ detail.questionCount }} 题 / {{ detail.totalScore }} 分</el-tag>
          <el-tag effect="plain">限时 {{ durationText(detail.durationMinutes) }}</el-tag>
        </div>
        <h2>{{ detail.name }}</h2>
        <p class="intro-desc">{{ detail.description || '按考试要求完成作答，交卷后不可修改。' }}</p>
        <dl class="intro-meta">
          <div>
            <dt>考试时间</dt>
            <dd>{{ dateTime(detail.startAt) }} 至 {{ dateTime(detail.endAt) }}</dd>
          </div>
          <div v-if="detail.deploymentStatus !== 'notRequired'">
            <dt>考试环境</dt>
            <dd>{{ detail.deploymentMessage || '尚未部署，请联系教师' }}</dd>
          </div>
          <div>
            <dt>作答说明</dt>
            <dd>理论答案自动保存。实操配置需点击保存。到时系统自动交卷。</dd>
          </div>
        </dl>
        <div class="intro-actions">
          <el-button @click="goBack">返回我的考试</el-button>
          <el-button v-if="detail.canStart" type="primary" size="large" :loading="starting" @click="start">
            开始考试
          </el-button>
          <el-button v-else-if="detail.status === 'scheduled'" disabled size="large">开考后可进入</el-button>
          <el-button v-else disabled size="large">当前不可作答</el-button>
        </div>
      </section>
    </template>
    <el-dialog
      v-model="practicalVisible"
      :title="practicalQuestion ? `实操作答 · 第 ${questionNumber(practicalQuestion)} 题` : '实操作答'"
      :before-close="closePractical"
      :close-on-click-modal="false"
      class="take-practical-dialog"
      width="min(820px, 96vw)"
      top="4vh"
      destroy-on-close
      append-to-body
    >
      <template #header="{ titleId, titleClass }">
        <div class="practical-dialog-heading">
          <h2 :id="titleId" :class="titleClass">
            {{ practicalQuestion ? `实操作答 · 第 ${questionNumber(practicalQuestion)} 题` : '实操作答' }}
          </h2>
          <el-tag type="warning">剩余 {{ countdownText(remaining) }}</el-tag>
        </div>
      </template>
      <template v-if="practicalQuestion">
        <p class="practical-stem">{{ practicalQuestion.stem }}</p>
        <section class="practical-resources" aria-label="答题资源">
          <h3>答题资源</h3>
          <p v-if="practicalQuestion.practicalResources">{{ practicalQuestion.practicalResources }}</p>
          <p v-else>本题没有附加参考材料。</p>
        </section>
        <ExamPracticalPanel
          :key="practicalId"
          :url="`/exam-hall/${examId}/practical/${practicalId}`"
          :locked="submitting || remaining <= 0"
          @saved="
            value => {
              if (practicalQuestion) practicalQuestion.practicalSaved = value
            }
          "
          @dirty="value => (practicalDirty = value)"
        />
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.practical-resources p {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
.practical-entry {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
}

.practical-stem {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

:global(.take-practical-dialog) {
  display: flex;
  flex-direction: column;
  max-height: 92vh;
  max-height: 92dvh;
}

:global(.take-practical-dialog .el-dialog__body) {
  min-height: 0;
  overflow: auto;
  overscroll-behavior: contain;
}

.practical-dialog-heading {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 14px;
  padding-right: 28px;
}

.practical-dialog-heading h2 {
  min-width: 0;
  margin: 0;
  overflow-wrap: anywhere;
}

.exam-taking-page {
  --take-sticky-top: 76px;
  --take-scroll-margin: 92px;
  min-height: 400px;
}

.take-heading {
  margin-bottom: 18px;
}

.take-heading h1 {
  margin: 6px 0 5px;
  color: var(--el-text-color-primary);
  font-size: 24px;
  overflow-wrap: anywhere;
}

.take-heading p {
  margin: 0;
  color: var(--el-text-color-regular);
  font-size: 13px;
}

.take-layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 270px;
  grid-template-areas: 'main side';
  gap: 18px;
  align-items: start;
}

.take-main {
  grid-area: main;
  min-width: 0;
}

.take-toolbar {
  margin-bottom: 12px;
  padding: 10px 12px;
  background: #fff;
  border: 1px solid var(--el-border-color);
  border-radius: 10px;
}

.take-question {
  padding: 20px;
  margin-bottom: 14px;
  scroll-margin-top: var(--take-scroll-margin);
  transition:
    border-color 0.2s ease,
    box-shadow 0.2s ease;
}

.take-question.current {
  border-color: #9ec6c8;
  box-shadow: 0 6px 20px #244f6c0c;
}

.take-question header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 10px;
}

.take-question-title {
  display: flex;
  align-items: center;
  gap: 8px;
}

.take-question-number {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  border-radius: 50%;
  background: #edf3fa;
  color: #4d6881;
  font-size: 12px;
}

.take-question-score {
  color: #95a3b2;
  font-size: 12px;
}

.take-question h3 {
  margin: 12px 0 16px;
  color: #243e58;
  font-size: 15px;
  line-height: 1.7;
  overflow-wrap: anywhere;
}

.take-options {
  display: grid;
  gap: 8px;
  width: 100%;
}

.take-option {
  display: flex;
  align-items: center;
  width: 100%;
  height: auto;
  margin: 0;
  padding: 11px 13px;
  border: 1px solid #e7edf4;
  border-radius: 8px;
  white-space: normal;
  transition:
    border-color 0.15s ease,
    background 0.15s ease;
}

.take-option:hover {
  border-color: #a9cbcd;
  background: #f8fcfc;
}

.take-option :deep(.el-radio__label),
.take-option :deep(.el-checkbox__label) {
  display: flex;
  align-items: center;
  flex: 1;
  min-width: 0;
  gap: 10px;
  color: #3b566e;
  font-size: 13px;
  line-height: 1.6;
  white-space: normal;
}

.take-option-text {
  min-width: 0;
  overflow-wrap: anywhere;
}

.take-option-letter {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  flex: none;
  border-radius: 50%;
  background: #f0f4f9;
  color: #5d748b;
  font-size: 12px;
}

.take-option.is-checked,
.take-option:has(input:checked) {
  border-color: #9ec6c8;
  background: #f4fbfa;
}

.take-bottom {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 14px;
  padding: 16px 0 8px;
}

.take-bottom > span {
  color: #8a99a9;
  font-size: 12px;
}

.take-side {
  grid-area: side;
  position: sticky;
  top: var(--take-sticky-top);
  z-index: 1;
  min-width: 0;
}

.take-side-card {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  grid-template-areas:
    'clock'
    'save'
    'progress'
    'index'
    'submit';
  gap: 10px;
  padding: 15px;
  border-color: var(--el-border-color);
}

.side-clock {
  grid-area: clock;
  display: flex;
  align-items: baseline;
  gap: 6px;
  color: var(--el-color-primary-dark-2);
}

.side-clock .el-icon {
  align-self: center;
}

.side-clock span,
.side-progress span,
.side-save {
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.side-clock strong {
  margin-left: auto;
  font-size: 25px;
  font-variant-numeric: tabular-nums;
  line-height: 1.1;
}

.side-clock.urgent,
.side-save.save-error > span {
  color: var(--el-color-danger);
}

.side-save {
  grid-area: save;
  display: flex;
  align-items: center;
  gap: 6px;
  min-height: 18px;
}

.side-progress {
  grid-area: progress;
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  align-items: baseline;
  gap: 5px 4px;
  color: var(--el-text-color-primary);
  font-size: 13px;
  font-variant-numeric: tabular-nums;
}

.side-progress .el-progress {
  grid-column: 1 / -1;
  min-width: 0;
}

.index-toggle {
  grid-area: toggle;
  display: none;
}

.index-panel {
  grid-area: index;
  min-width: 0;
}

.question-index {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(37px, 1fr));
  gap: 6px;
  max-height: min(33vh, 256px);
  padding: 3px;
  overflow: auto;
  overscroll-behavior: contain;
}

.index-dot {
  min-height: 36px;
  padding: 0 4px;
  border-radius: 6px;
  border: 1px solid var(--el-border-color);
  background: #fff;
  color: var(--el-text-color-primary);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.index-dot:hover {
  border-color: var(--el-color-primary-light-5);
}

.index-dot.answered {
  border-color: #a4d8cb;
  background: #eef8f5;
  color: #2d6f61;
}

.index-dot.current {
  border-color: var(--el-color-primary);
  box-shadow: 0 0 0 1px var(--el-color-primary);
  font-weight: 700;
}

.index-legend {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 10px;
  margin: 8px 0 0;
  color: var(--el-text-color-regular);
  font-size: 11px;
}

.index-legend span {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.legend-dot {
  width: 9px;
  height: 9px;
  border-radius: 3px;
  border: 1px solid var(--el-border-color);
  background: #fff;
}

.legend-dot.answered {
  border-color: #a4d8cb;
  background: #eef8f5;
}

.legend-dot.current {
  border-color: var(--el-color-primary);
  background: var(--el-color-primary-light-9);
}

.side-submit {
  grid-area: submit;
}

.take-side-info {
  display: grid;
  gap: 6px;
  margin: 10px 0 0;
  padding-top: 8px;
  border-top: 1px solid var(--el-border-color-light);
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.take-side-info dt {
  display: flex;
  align-items: center;
  gap: 4px;
}

.take-side-info dd {
  margin: 2px 0 0 16px;
  line-height: 1.5;
  overflow-wrap: anywhere;
}
.finish-panel,
.exam-intro {
  max-width: 760px;
  margin: 12px auto;
  padding: 34px 36px;
  text-align: center;
}

.finish-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 58px;
  height: 58px;
  border-radius: 50%;
  background: #eaf7f2;
  color: #4b9b8e;
  font-size: 26px;
}

.finish-panel h2,
.exam-intro h2 {
  margin: 16px 0 8px;
  color: #1f3a55;
  font-size: 22px;
}

.finish-panel > p,
.intro-desc {
  margin: 0;
  color: #6e8397;
  font-size: 13px;
  line-height: 1.7;
}

.finish-scores {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 10px;
  margin: 24px 0;
}

.finish-scores > div {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 12px;
  background: #f8fafd;
  border: 1px solid #e8eef6;
  border-radius: 8px;
}

.finish-scores span {
  color: #8595a6;
  font-size: 12px;
}

.finish-scores strong {
  color: #2a465f;
  font-size: 22px;
}

.finish-scores strong small {
  color: #97a5b3;
  font-size: 12px;
  font-weight: 400;
}

.finish-scores p {
  margin: 2px 0 0;
  color: #5a7087;
  font-size: 12px;
}

.finish-actions {
  display: flex;
  justify-content: center;
  gap: 10px;
}

.exam-intro {
  text-align: left;
}

.intro-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 7px;
}

.exam-intro h2 {
  margin-top: 14px;
  text-align: left;
}

.intro-meta {
  display: grid;
  gap: 10px;
  margin: 20px 0;
  padding: 16px;
  background: #f8fafd;
  border: 1px solid #e8eef6;
  border-radius: 9px;
}

.intro-meta > div {
  display: grid;
  grid-template-columns: 76px minmax(0, 1fr);
  gap: 10px;
}

.intro-meta dt {
  color: #8495a6;
  font-size: 12px;
}

.intro-meta dd {
  margin: 0;
  color: #3d5a73;
  font-size: 12px;
  line-height: 1.6;
}

.intro-actions {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
}

@media (max-width: 1050px) {
  .exam-taking-page {
    --take-scroll-margin: 176px;
  }

  .take-layout {
    grid-template-columns: minmax(0, 1fr);
    grid-template-areas:
      'side'
      'main';
    gap: 12px;
  }

  .take-side-card {
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) auto auto;
    grid-template-areas:
      'clock progress toggle submit'
      'save save save save'
      'index index index index';
    gap: 5px 10px;
    padding: 9px 10px 6px;
    align-items: center;
    box-shadow: 0 4px 14px #19364b12;
  }

  .side-clock,
  .side-progress {
    display: grid;
    grid-template-columns: minmax(0, 1fr);
    gap: 2px;
  }

  .side-clock .el-icon {
    display: none;
  }

  .side-clock strong {
    margin: 0;
    font-size: 17px;
  }

  .side-progress strong {
    line-height: 1.2;
  }

  .side-progress .el-progress {
    max-width: 90px;
  }

  .side-submit,
  .index-toggle {
    min-height: 40px;
    margin: 0;
    padding-inline: 8px;
    font-size: 12px;
  }

  .index-toggle {
    display: inline-flex;
  }

  .index-toggle[aria-expanded='true'] {
    border-color: var(--el-color-primary);
    color: var(--el-color-primary);
  }

  .index-panel {
    display: none;
  }

  .index-panel.is-open {
    display: block;
    max-height: min(42vh, 340px);
    overflow: auto;
    overscroll-behavior: contain;
    padding-bottom: 5px;
  }

  .question-index {
    max-height: none;
    padding-top: 5px;
    overflow: visible;
  }

  .index-dot {
    min-height: 40px;
  }
}

@media (max-width: 800px) {
  .exam-taking-page {
    --take-sticky-top: 70px;
  }
}

@media (max-width: 640px) {
  .take-heading {
    margin-bottom: 12px;
  }

  .take-question {
    padding: 15px;
  }

  .take-option {
    min-height: 44px;
    padding: 8px 10px;
  }

  .take-bottom,
  .finish-actions,
  .intro-actions {
    flex-wrap: wrap;
  }

  .finish-scores {
    grid-template-columns: 1fr 1fr;
  }

  .finish-panel,
  .exam-intro {
    padding: 24px 18px;
  }
}

@media (max-width: 410px) {
  .take-side-card {
    column-gap: 6px;
  }

  .index-toggle :deep(.el-icon) {
    display: none;
  }

  .index-toggle :deep(.el-icon + span) {
    margin-left: 0;
  }
}
</style>

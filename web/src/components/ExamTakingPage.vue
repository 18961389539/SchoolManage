<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ArrowLeft, Check, Clock, Document, Grid, Refresh } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, dateTime, messageOf, post } from '../api'
import { registerLeaveGuard } from '../leaveGuard'
import {
  attemptStatusLabel,
  attemptStatusType,
  countdownText,
  examQuestionTypeLabel,
  examQuestionTypeOptions,
  optionLetters
} from '../exam'
import { durationText } from '../teaching'
import { parseApiDate, rangeText, schoolTimeZone } from '../time'
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
const unansweredOnly = ref(false)
const indexToggle = ref<HTMLButtonElement | null>(null)
const questionIndex = ref<HTMLElement | null>(null)
const finishHeading = ref<HTMLElement | null>(null)
const starting = ref(false)
const submitting = ref(false)
const leaving = ref(false)
const reviewVisible = ref(false)
const reviewError = ref('')
const completed = ref<ExamSubmitResult | null>(null)
const saveState = ref<'' | 'pending' | 'saving' | 'saved' | 'error'>('')
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
let deployPoll: number | undefined
const refreshingStatus = ref(false)
let debounceTimer: number | undefined
let serverOffset = 0
let activeSave: Promise<void> | undefined
let reviewTarget: ExamTakingQuestion | undefined
let disposed = false
const saveTimeFormat = new Intl.DateTimeFormat('zh-CN', {
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hourCycle: 'h23',
  timeZone: schoolTimeZone
})

const questions = computed<ExamTakingQuestion[]>(() => detail.value?.questions || [])
const questionNumbers = computed(
  () => new Map(questions.value.map((question, index) => [question.paperQuestionId, index + 1]))
)
const practicalQuestion = computed(() => questions.value.find(item => item.paperQuestionId === practicalId.value))
const inProgress = computed(
  () => !completed.value && detail.value?.attempt?.status === 'inProgress' && questions.value.length > 0
)
const answerLocked = computed(() => submitting.value || leaving.value)
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
const indexQuestions = computed(() =>
  unansweredOnly.value
    ? questions.value.filter(question => !answeredIds.value.has(question.paperQuestionId))
    : questions.value
)
const progressPercent = computed(() =>
  questions.value.length ? Math.round((answeredCount.value * 100) / questions.value.length) : 0
)
const saveText = computed(() => {
  if (saveState.value === 'saving') return '正在保存…'
  if (saveState.value === 'pending') return '有更改待保存…'
  if (saveState.value === 'error') return '自动保存失败'
  if (saveState.value === 'saved' && lastSavedAt.value) return `已保存 ${saveTimeFormat.format(lastSavedAt.value)}`
  return '答案将自动保存'
})
const attemptSummary = computed(() => detail.value?.attempt || null)
const finishedAttempt = computed(() =>
  attemptSummary.value && attemptSummary.value.status !== 'inProgress' ? attemptSummary.value : null
)

function questionNumber(question: ExamTakingQuestion): number {
  return questionNumbers.value.get(question.paperQuestionId) ?? 0
}

function waitingForDeploy(item = detail.value) {
  return (
    !!item &&
    item.status === 'open' &&
    item.deploymentStatus !== 'notRequired' &&
    item.deploymentStatus !== 'deployed' &&
    !item.canStart
  )
}

function stopDeployPoll() {
  if (deployPoll !== undefined) {
    window.clearInterval(deployPoll)
    deployPoll = undefined
  }
}

function syncDeployPoll() {
  stopDeployPoll()
  if (!disposed && waitingForDeploy()) {
    deployPoll = window.setInterval(() => {
      void refreshHall(true).catch(() => {})
    }, 15000)
  }
}

async function refreshHall(silent = false) {
  if (!silent) refreshingStatus.value = true
  const wasWaiting = waitingForDeploy()
  try {
    const data = await api<ExamHallDetail>(`/exam-hall/${props.examId}`)
    if (disposed) return
    serverOffset = parseApiDate(data.serverNow).getTime() - Date.now()
    detail.value = data
    if (data.attempt?.status === 'inProgress' && data.questions) {
      stopClocks()
      await hydrate(data.questions)
      syncRemaining()
      if (!disposed) startClocks()
    } else if (data.attempt) {
      remaining.value = 0
    }
    if (wasWaiting && data.canStart) ElMessage.success('实操环境已就绪，可以开始作答')
  } catch (failure) {
    if (!silent) error.value = messageOf(failure)
    else throw failure
  } finally {
    if (!silent) refreshingStatus.value = false
    syncDeployPoll()
  }
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    await refreshHall(true)
    if (disposed) return
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
  unansweredOnly.value = false
  autoTriggered = false
  dirty = false
  saveState.value = ''
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
  clearSaveTimer()
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
      selected: [...(answers[question.paperQuestionId]?.selected || [])],
      text: answers[question.paperQuestionId]?.text || ''
    }))
}

function scheduleSave() {
  clearSaveTimer()
  debounceTimer = window.setTimeout(() => {
    debounceTimer = undefined
    void saveNow()
  }, 800)
}

function clearSaveTimer() {
  if (debounceTimer === undefined) return
  window.clearTimeout(debounceTimer)
  debounceTimer = undefined
}

function saveNow() {
  if (disposed || activeSave || !dirty || !inProgress.value || submitting.value) return activeSave
  clearSaveTimer()
  dirty = false
  saveState.value = 'saving'
  activeSave = persistAnswers().finally(() => {
    activeSave = undefined
    if (!disposed && dirty && saveState.value !== 'error' && inProgress.value && !submitting.value) scheduleSave()
  })
  return activeSave
}

async function persistAnswers() {
  try {
    await post(`/exam-hall/${props.examId}/answers`, { answers: payload() })
    if (disposed) return
    lastSavedAt.value = new Date()
    saveState.value = dirty ? 'pending' : 'saved'
  } catch (failure) {
    if (disposed || completed.value) return
    dirty = true
    saveState.value = 'error'
    const status = (failure as { status?: number })?.status
    if (status === 409 && !submitting.value) {
      ElMessage.warning(messageOf(failure))
      await load()
      if (!inProgress.value && !disposed) closeAfterSubmit()
    }
  }
}

function dismissIndex(event: KeyboardEvent) {
  if (!indexOpen.value || !indexToggle.value?.offsetParent) return
  event.stopPropagation()
  indexOpen.value = false
  indexToggle.value.focus({ preventScroll: true })
}

watch(
  [indexOpen, currentId, unansweredOnly],
  () => {
    const list = questionIndex.value
    const active = list?.querySelector<HTMLElement>('[aria-current="true"]')
    if (!list?.clientHeight || !active) return
    const viewport = list.getBoundingClientRect()
    const item = active.getBoundingClientRect()
    if (item.top < viewport.top) list.scrollTop += item.top - viewport.top
    else if (item.bottom > viewport.bottom) list.scrollTop += item.bottom - viewport.bottom
  },
  { flush: 'post' }
)

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

function nextUnanswered() {
  const index = questions.value.findIndex(question => question.paperQuestionId === currentId.value)
  const ordered = [...questions.value.slice(index + 1), ...questions.value.slice(0, index + 1)]
  return ordered.find(question => !answeredIds.value.has(question.paperQuestionId))
}

function jumpToUnanswered() {
  const question = nextUnanswered()
  if (question) void scrollToQuestion(question)
}

function openReview() {
  reviewError.value = ''
  reviewVisible.value = true
}

function reviewUnanswered() {
  reviewTarget = nextUnanswered()
  reviewVisible.value = false
}

function afterReviewClosed() {
  if (completed.value || finishedAttempt.value) void nextTick(focusReceipt)
  else if (reviewTarget && inProgress.value) void scrollToQuestion(reviewTarget)
  reviewTarget = undefined
}

function focusReceipt() {
  if (disposed) return
  finishHeading.value?.focus({ preventScroll: true })
  window.scrollTo(0, 0)
}

function closeAfterSubmit() {
  practicalVisible.value = false
  if (reviewVisible.value) reviewVisible.value = false
  else void nextTick(focusReceipt)
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
  submitting.value = true
  autoTriggered = true
  reviewError.value = ''
  clearSaveTimer()
  try {
    const result = await post<ExamSubmitResult>(`/exam-hall/${props.examId}/submit`, { answers: payload() })
    if (disposed) return
    completed.value = result
    stopClocks()
    closeAfterSubmit()
  } catch (failure) {
    if (disposed) return
    const status = (failure as { status?: number })?.status
    if (auto || status === 409) {
      ElMessage.warning(messageOf(failure))
      await load()
      if (!inProgress.value && !disposed) closeAfterSubmit()
    } else {
      reviewError.value = messageOf(failure)
      autoTriggered = false
    }
  } finally {
    submitting.value = false
    if (!disposed && dirty && inProgress.value) scheduleSave()
  }
}

function beforeUnload(event: BeforeUnloadEvent) {
  if (!inProgress.value) return
  event.preventDefault()
  event.returnValue = ''
}

watch(
  answers,
  () => {
    if (hydrating || !inProgress.value || submitting.value) return
    dirty = true
    if (!activeSave) saveState.value = 'pending'
    scheduleSave()
  },
  { deep: true }
)

watch(activeType, () => {
  if (filteredQuestions.value.length) currentId.value = filteredQuestions.value[0].paperQuestionId
})

let leaveApproved = false
const releaseLeaveGuard = registerLeaveGuard(async () => {
  if (leaveApproved || !inProgress.value) return true
  if (submitting.value || leaving.value) return false
  if (activeSave) await activeSave
  if (dirty) await saveNow()
  const unsaved = dirty || saveState.value === 'error'
  try {
    await ElMessageBox.confirm(
      unsaved
        ? '还有答案没有保存成功，离开后这些修改可能丢失。仍要离开吗？'
        : '考试仍在进行，离开后倒计时不会暂停。已作答内容会保留，可从「我的考试」继续。',
      '离开考试',
      {
        type: 'warning',
        confirmButtonText: unsaved ? '仍要离开' : '离开考试',
        cancelButtonText: '继续作答'
      }
    )
  } catch {
    return false
  }
  leaveApproved = true
  return true
})

onMounted(() => {
  void load()
  window.addEventListener('beforeunload', beforeUnload)
})

onBeforeUnmount(() => {
  releaseLeaveGuard()
  // Last-chance write for paths that bypass the guard (session expiry, forced sign-out).
  if (dirty && inProgress.value && !submitting.value) void persistAnswers()
  disposed = true
  stopDeployPoll()
  stopClocks()
  window.removeEventListener('beforeunload', beforeUnload)
})

async function goBack() {
  if (submitting.value || leaving.value) return
  if (inProgress.value) {
    // Flush before the shared leave guard asks, so leaving never drops the last debounce window.
    leaving.value = true
    try {
      if (activeSave) await activeSave
      if (dirty) await saveNow()
    } finally {
      leaving.value = false
    }
  }
  if (!disposed) emit('navigate', 'my-exams')
}

function goRecords() {
  emit('navigate', 'exam-records')
}
</script>

<template>
  <div v-loading="loading" class="exam-taking-page">
    <section v-if="error" class="surface exam-intro">
      <el-result icon="error" title="考试信息加载失败" :sub-title="error">
        <template #extra>
          <el-button @click="goBack">返回我的考试</el-button>
          <el-button type="primary" :icon="Refresh" @click="load">重试</el-button>
        </template>
      </el-result>
    </section>

    <template v-if="detail && !error">
      <section class="take-heading">
        <el-button link :icon="ArrowLeft" :loading="leaving" :disabled="submitting" @click="goBack"
          >返回我的考试</el-button
        >
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
        <h2 ref="finishHeading" tabindex="-1">
          {{ completed.autoSubmitted ? '考试时间已到，试卷已自动提交' : '试卷提交成功' }}
        </h2>
        <p v-if="completed.pendingPractical">文字实操题待批阅，最终成绩将在批阅后显示。</p>
        <p v-else>自动评分已完成。可在考试记录中查看成绩与复盘。</p>
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
              >{{ completed.totalScore }}<small> / {{ completed.maxScore }}</small></strong
            >
          </div>
          <div>
            <span>状态</span
            ><el-tag :type="attemptStatusType(completed.status)">{{ attemptStatusLabel(completed.status) }}</el-tag>
          </div>
        </div>
        <div class="finish-actions">
          <el-button @click="goBack">返回我的考试</el-button>
          <el-button type="primary" @click="goRecords">查看考试记录</el-button>
        </div>
      </section>

      <template v-else-if="inProgress">
        <div class="take-layout">
          <aside class="take-side" aria-label="考试状态和导航" @keydown.esc="dismissIndex">
            <div class="surface take-side-card">
              <div class="side-clock" :class="{ urgent: remaining <= 300 }">
                <el-icon><Clock /></el-icon>
                <span>剩余时间</span>
                <strong role="timer" aria-label="考试剩余时间">{{ countdownText(remaining) }}</strong>
              </div>
              <div
                class="side-save"
                :class="{ 'save-error': saveState === 'error', 'save-done': saveState === 'saved' }"
              >
                <i aria-hidden="true" />
                <span role="status">{{ saveText }}</span>
                <el-button v-if="saveState === 'error'" link type="primary" :icon="Refresh" @click="saveNow">
                  重试
                </el-button>
              </div>
              <div class="side-progress">
                <span>已作答</span><strong>{{ answeredCount }} / {{ questions.length }}</strong>
                <el-progress :percentage="progressPercent" :stroke-width="4" :show-text="false" aria-hidden="true" />
              </div>
              <button
                ref="indexToggle"
                type="button"
                class="index-toggle"
                :aria-expanded="indexOpen"
                aria-controls="exam-question-index"
                @click="indexOpen = !indexOpen"
              >
                <el-icon aria-hidden="true"><Grid /></el-icon>答题卡
              </button>
              <div id="exam-question-index" class="index-panel" :class="{ 'is-open': indexOpen }">
                <div class="index-heading">
                  <h2>答题卡</h2>
                  <span>未答 {{ unanswered }} 题</span>
                </div>
                <div class="index-actions">
                  <el-checkbox v-model="unansweredOnly">只看未答</el-checkbox>
                  <el-button link type="primary" :disabled="!unanswered" @click="jumpToUnanswered"
                    >下一道未答</el-button
                  >
                </div>
                <div ref="questionIndex" class="question-index" role="group" aria-label="答题卡题号">
                  <button
                    v-for="question in indexQuestions"
                    :key="question.paperQuestionId"
                    type="button"
                    class="index-dot"
                    :class="{
                      answered: answeredIds.has(question.paperQuestionId),
                      current: currentId === question.paperQuestionId
                    }"
                    :aria-current="currentId === question.paperQuestionId ? 'true' : undefined"
                    :aria-description="answeredIds.has(question.paperQuestionId) ? '已作答' : '未作答'"
                    :title="`第 ${questionNumber(question)} 题 · ${answeredIds.has(question.paperQuestionId) ? '已作答' : '未作答'}`"
                    @click="scrollToQuestion(question)"
                  >
                    {{ questionNumber(question) }}
                  </button>
                </div>
                <p v-if="!indexQuestions.length" class="index-empty">所有题目均已作答</p>
                <div v-else class="index-legend">
                  <span><i class="legend-dot" />未作答</span>
                  <span><i class="legend-dot answered" />已作答</span>
                  <span><i class="legend-dot current" />当前题</span>
                </div>
                <dl class="take-side-info">
                  <div>
                    <dt>
                      <el-icon><Document /></el-icon>考试时间
                    </dt>
                    <dd>{{ rangeText(detail.startAt, detail.endAt) }}</dd>
                  </div>
                  <div>
                    <dt>
                      <el-icon><Clock /></el-icon>截止时间
                    </dt>
                    <dd>{{ dateTime(detail.attempt?.deadline) }}</dd>
                  </div>
                </dl>
              </div>
              <el-button
                type="primary"
                class="side-submit"
                :loading="submitting"
                :disabled="leaving"
                @click="openReview"
                >交卷</el-button
              >
            </div>
          </aside>
          <div class="take-main">
            <h2 class="visually-hidden">试卷题目</h2>
            <div class="take-toolbar">
              <el-radio-group v-model="activeType" size="small" aria-label="按题型筛选">
                <el-radio-button v-for="tab in typeTabs" :key="tab.value" :value="tab.value">
                  {{ tab.label }}
                </el-radio-button>
              </el-radio-group>
              <span class="filtered-count" v-if="activeType !== 'all'">显示 {{ filteredQuestions.length }} 题</span>
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
                :aria-label="`第 ${questionNumber(question)} 题选项`"
                :disabled="answerLocked"
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
                :aria-label="`第 ${questionNumber(question)} 题选项`"
                :disabled="answerLocked"
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
                <el-button type="primary" plain :disabled="answerLocked" @click="openPractical(question)"
                  >进入实操环境</el-button
                >
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
                  :aria-label="`第 ${questionNumber(question)} 题答案`"
                  :disabled="answerLocked"
                  placeholder="请按题目要求分点作答，教师将根据要点评分"
                />
              </template>
            </article>

            <div class="take-bottom">
              <span>到时系统按已保存答案自动交卷。</span>
              <el-button type="primary" size="large" :loading="submitting" :disabled="leaving" @click="openReview"
                >提交试卷</el-button
              >
            </div>
          </div>
        </div>
      </template>

      <section v-else-if="finishedAttempt" class="surface finish-panel">
        <div class="finish-icon done">
          <el-icon><Check /></el-icon>
        </div>
        <h2 ref="finishHeading" tabindex="-1">本次考试已提交</h2>
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
        </div>
        <h2>作答须知</h2>
        <p v-if="detail.description" class="intro-desc">{{ detail.description }}</p>
        <dl class="intro-meta">
          <div>
            <dt>考试时间</dt>
            <dd>{{ rangeText(detail.startAt, detail.endAt) }}</dd>
          </div>
          <div v-if="detail.deploymentStatus !== 'notRequired'">
            <dt>考试环境</dt>
            <dd>{{ detail.deploymentMessage || '尚未部署，请联系教师' }}</dd>
          </div>
          <div>
            <dt>作答说明</dt>
            <dd>
              选择题和文字答案自动保存。<template v-if="detail.deploymentStatus !== 'notRequired'">
                模拟实操配置请在实操窗口中保存。</template
              >
              到时按已保存答案自动交卷。
            </dd>
          </div>
        </dl>
        <div class="intro-actions">
          <el-button @click="goBack">返回我的考试</el-button>
          <el-button v-if="detail.canStart" type="primary" size="large" :loading="starting" @click="start">
            开始考试
          </el-button>
          <el-button v-else-if="detail.status === 'scheduled'" disabled size="large">开考后可进入</el-button>
          <el-button
            v-else-if="
              detail.status === 'open' &&
              detail.deploymentStatus !== 'notRequired' &&
              detail.deploymentStatus !== 'deployed'
            "
            disabled
            size="large"
            >等待部署实操环境</el-button
          >
          <el-button
            v-if="
              detail.status === 'open' &&
              detail.deploymentStatus !== 'notRequired' &&
              detail.deploymentStatus !== 'deployed'
            "
            :loading="refreshingStatus"
            @click="refreshHall(false)"
          >
            刷新状态
          </el-button>
          <el-button
            v-if="
              !detail.canStart &&
              detail.status !== 'scheduled' &&
              (detail.status !== 'open' ||
                detail.deploymentStatus === 'notRequired' ||
                detail.deploymentStatus === 'deployed')
            "
            disabled
            size="large"
            >当前不可作答</el-button
          >
        </div>
      </section>
    </template>
    <el-dialog
      v-model="reviewVisible"
      title="提交试卷"
      width="min(480px, calc(100vw - 24px))"
      :show-close="!submitting"
      :close-on-press-escape="!submitting"
      :close-on-click-modal="false"
      append-to-body
      @closed="afterReviewClosed"
    >
      <dl class="review-counts">
        <div>
          <dt>已作答</dt>
          <dd>{{ answeredCount }} 题</dd>
        </div>
        <div :class="{ 'has-unanswered': unanswered }">
          <dt>未作答</dt>
          <dd>{{ unanswered }} 题</dd>
        </div>
      </dl>
      <p class="review-note">
        {{
          unanswered ? `还有 ${unanswered} 道题未作答，交卷后不能修改答案。` : '所有题目均已作答，交卷后不能修改答案。'
        }}
      </p>
      <el-alert v-if="reviewError" :title="reviewError" type="error" show-icon :closable="false" />
      <template #footer>
        <div class="review-actions">
          <el-button :disabled="submitting" @click="reviewVisible = false">继续作答</el-button>
          <el-button v-if="unanswered" :disabled="submitting" @click="reviewUnanswered">查看未答</el-button>
          <el-button type="primary" :loading="submitting" @click="submit()">确认交卷</el-button>
        </div>
      </template>
    </el-dialog>
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
      @closed="(completed || finishedAttempt) && focusReceipt()"
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
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 8px 12px;
  margin-bottom: 12px;
  padding: 10px 12px;
  background: #f8fbfb;
  border: 1px solid var(--el-border-color);
  border-radius: 10px;
}

.take-toolbar .el-radio-group {
  row-gap: 6px;
}

.filtered-count {
  color: var(--text-muted);
  font-size: 12px;
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
  border-color: #94c7b8;
  box-shadow: 0 3px 14px #1d654612;
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
  border-radius: 8px;
  background: #eef6f4;
  color: #385a70;
  font-size: 12px;
}

.take-question.current .take-question-number {
  background: #dcefe6;
  color: #145b4f;
  font-weight: 650;
}

.take-question-score {
  color: #5d6f80;
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
  border: 1px solid #dce7eb;
  border-radius: 8px;
  white-space: normal;
  transition:
    border-color 0.15s ease,
    background 0.15s ease;
}

.take-option:hover {
  border-color: #a9cbcd;
  background: #f3f8f6;
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
  background: #e8f3ef;
  color: #145a50;
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
  color: #56697c;
  font-size: 12px;
}

.take-side {
  grid-area: side;
  position: sticky;
  top: var(--take-sticky-top);
  z-index: 12;
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
  gap: 12px;
  padding: 15px;
  border-color: var(--el-border-color);
}

.side-clock {
  grid-area: clock;
  display: flex;
  align-items: baseline;
  gap: 6px;
  padding: 10px;
  border: 1px solid #d4eae1;
  border-radius: 9px;
  background: #edf7f2;
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

.side-clock.urgent {
  border-color: #eed6d5;
  background: #fff1ef;
}

.side-save {
  grid-area: save;
  display: flex;
  align-items: center;
  gap: 6px;
  min-height: 18px;
}

.side-save > i {
  width: 6px;
  height: 6px;
  flex: none;
  border-radius: 50%;
  background: #6c8292;
}

.side-save.save-done > i {
  background: #25826b;
}

.side-save.save-error > i {
  background: var(--el-color-danger);
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

.index-heading,
.index-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 3px 8px;
  color: var(--text-muted);
  font-size: 12px;
}

.index-heading {
  margin-top: 3px;
  padding-top: 10px;
  border-top: 1px solid var(--surface-border);
}

.index-heading h2 {
  margin: 0;
  font-size: 15px;
}

.index-actions {
  margin: 4px 0;
}

.index-actions .el-checkbox {
  margin-right: 0;
}

.index-actions :deep(.el-checkbox__label),
.index-actions .el-button {
  font-size: 12px;
}

.index-empty {
  margin: 4px 0;
  font-size: 12px;
}

.question-index {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(37px, 1fr));
  gap: 6px;
  max-height: min(33vh, 256px);
  padding: 3px;
  overflow: auto;
  overscroll-behavior: contain;
  scrollbar-width: thin;
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
  color: #56697c;
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
  background: #f3f8f6;
  border: 1px solid #e4eeea;
  border-radius: 8px;
}

.finish-scores span {
  color: #56697c;
  font-size: 12px;
}

.finish-scores strong {
  color: #2a465f;
  font-size: 22px;
}

.finish-scores strong small {
  color: #5d6f80;
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

.finish-panel h2:focus-visible {
  outline: 2px solid var(--el-color-primary);
  outline-offset: 3px;
}

.review-counts {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px;
  margin: 0 0 10px;
}

.review-counts > div {
  padding: 12px;
  border: 1px solid var(--surface-border);
  border-radius: 9px;
  background: #f5f9f8;
}

.review-counts > .has-unanswered {
  border-color: #ebd8b6;
  background: #fff7e9;
}

.review-counts dt {
  color: var(--text-muted);
  font-size: 12px;
}

.review-counts dd {
  margin: 5px 0 0;
  color: #264558;
  font-size: 19px;
  font-weight: 650;
  font-variant-numeric: tabular-nums;
}

.review-note {
  margin: 0;
  font-size: 14px;
}

.review-note + .el-alert {
  margin-top: 10px;
}

.review-actions {
  display: flex;
  justify-content: flex-end;
  flex-wrap: wrap;
  gap: 8px;
}

.review-actions .el-button + .el-button {
  margin-left: 0;
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
  background: #f3f8f6;
  border: 1px solid #e4eeea;
  border-radius: 9px;
}

.intro-meta > div {
  display: grid;
  grid-template-columns: 76px minmax(0, 1fr);
  gap: 10px;
}

.intro-meta dt {
  color: #56697c;
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
    display: block;
  }

  .take-side {
    margin-bottom: 12px;
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

  .side-clock {
    min-width: 0;
    padding: 0;
    border: 0;
    background: transparent;
  }

  .side-clock.urgent {
    background: transparent;
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
    align-items: center;
    justify-content: center;
    gap: 5px;
    border: 1px solid var(--el-border-color);
    border-radius: 7px;
    background: #fff;
    color: #29495d;
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
    max-height: min(57dvh, 340px);
    overflow: auto;
    overscroll-behavior: contain;
    padding-bottom: 5px;
  }

  .question-index {
    max-height: min(27dvh, 164px);
    padding-top: 5px;
  }

  .index-heading h2 {
    display: none;
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

  .index-toggle .el-icon {
    display: none;
  }

  .review-actions > .el-button {
    padding-inline: 8px;
  }
}
</style>

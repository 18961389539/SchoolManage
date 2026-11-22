<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Clock, Grid, Promotion, Refresh, Select } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, messageOf, post } from '../api'
import { campAttemptLabel, campAttemptStatusType } from '../camp'
import { countdownText, examQuestionTypeLabel, optionLetters } from '../exam'
import { durationText } from '../teaching'
import { parseApiDate } from '../time'
import type {
  CampPositionLearnerDetail,
  CampSaveAnswer,
  CampStartResponse,
  CampSubmitResponse,
  CampTakingQuestion
} from '../types'
import CampEnvironmentPanel from './CampEnvironmentPanel.vue'
import CampRecordDrawer from './CampRecordDrawer.vue'
import CampReportDrawer from './CampReportDrawer.vue'
import ExamPracticalPanel from './ExamPracticalPanel.vue'

const props = defineProps<{ positionId: string }>()
const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()

const detail = ref<CampPositionLearnerDetail | null>(null)
const loading = ref(false)
const error = ref('')
const starting = ref(false)
const submitting = ref(false)
const autoSubmitting = ref(false)
const activeTab = ref('objective')
const now = ref(Date.now())
const saveState = ref('')
const reportVisible = ref(false)
const recordVisible = ref(false)
const practicalQuestionId = ref('')
const questionIndexOpen = ref(false)
const saving = ref(false)
let ticker: number | undefined
let saveTimer: number | undefined
let saveQueued = false
let saveRequested = false
const autoSubmittedAttempts = new Set<string>()

const questions = computed(() => detail.value?.questions || [])
const objectiveQuestions = computed(() => questions.value.filter(item => item.type !== 'practical'))
const practicalQuestions = computed(() => questions.value.filter(item => item.type === 'practical'))
const practicalQuestion = computed(() => practicalQuestions.value.find(item => item.id === practicalQuestionId.value))
const practicalVisible = computed({
  get: () => !!practicalQuestionId.value,
  set: open => {
    if (!open) practicalQuestionId.value = ''
  }
})
const inProgress = computed(() => detail.value?.attempt?.status === 'inProgress')
const graded = computed(() => detail.value?.attempt?.status === 'graded')
const environmentUrl = computed(() => `/camp/positions/${props.positionId}/environment`)
const remaining = computed(() => {
  const attempt = detail.value?.attempt
  if (!attempt || attempt.status !== 'inProgress') return 0
  return Math.max(0, Math.ceil((parseApiDate(attempt.deadline).getTime() - now.value) / 1000))
})
const answeredCount = computed(() => questions.value.filter(answered).length)
const answeredPercent = computed(() =>
  questions.value.length ? Math.round((answeredCount.value / questions.value.length) * 100) : 0
)
const attemptId = computed(() => detail.value?.attempt?.id || '')
const percent = computed(() => {
  const attempt = detail.value?.attempt
  if (!attempt || attempt.maxScore <= 0) return 0
  return Math.round((attempt.totalScore / attempt.maxScore) * 100)
})

function answered(question: CampTakingQuestion): boolean {
  if (question.type !== 'practical') return question.selected.length > 0
  return question.automaticPractical ? question.hasWork : question.textAnswer.trim().length > 0
}

function questionNumber(question: CampTakingQuestion): number {
  return questions.value.findIndex(item => item.id === question.id) + 1
}

function pickSingle(question: CampTakingQuestion, value: string | number | boolean | undefined) {
  question.selected = value === undefined || value === '' ? [] : [Number(value)]
  scheduleSave()
}

function toggleMultiple(question: CampTakingQuestion, index: number) {
  question.selected = question.selected.includes(index)
    ? question.selected.filter(item => item !== index)
    : [...question.selected, index].sort((left, right) => left - right)
  scheduleSave()
}

function onPracticalInput() {
  scheduleSave(1200)
}

async function load(silent = false) {
  if (!silent) loading.value = true
  error.value = ''
  try {
    const result = await api<CampPositionLearnerDetail>(`/camp/positions/${props.positionId}`)
    detail.value = result
    now.value = Date.now()
    if (result.attempt?.status === 'inProgress') activeTab.value = 'objective'
  } catch (failure) {
    if (!silent) error.value = messageOf(failure)
  } finally {
    if (!silent) loading.value = false
  }
}

async function start() {
  if (!detail.value || starting.value) return
  try {
    await ElMessageBox.confirm(
      `测评时长 ${durationText(detail.value.durationMinutes)}，开始后立即计时且不可暂停。确认开始测评吗？`,
      '开始岗位测评',
      { type: 'warning', confirmButtonText: '开始测评', cancelButtonText: '稍后再做' }
    )
  } catch {
    return
  }
  starting.value = true
  error.value = ''
  try {
    await post<CampStartResponse>(`/camp/positions/${props.positionId}/start`, {})
    ElMessage.success('测评已开始，计时同步启动')
    await load(true)
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    starting.value = false
  }
}

function collectAnswers(): CampSaveAnswer[] {
  return questions.value.map(question => ({
    questionId: question.id,
    selected: question.selected,
    text: question.textAnswer
  }))
}

function scheduleSave(delay = 800) {
  if (!inProgress.value) return
  saveRequested = true
  saveState.value = '待保存'
  if (saveTimer !== undefined) window.clearTimeout(saveTimer)
  saveTimer = window.setTimeout(() => void flushSave(), delay)
}

async function flushSave(announce = false): Promise<void> {
  if (saveTimer !== undefined) {
    window.clearTimeout(saveTimer)
    saveTimer = undefined
  }
  if (!inProgress.value || !detail.value) return
  if (saving.value) {
    saveQueued = true
    return
  }
  saving.value = true
  saveRequested = false
  saveState.value = '保存中…'
  try {
    await post(`/camp/positions/${props.positionId}/answers`, { answers: collectAnswers() })
    saveState.value = '答案已自动保存'
    if (announce) ElMessage.success('答案已保存')
  } catch (failure) {
    saveState.value = '保存失败'
    if (announce) ElMessage.error(messageOf(failure))
  } finally {
    saving.value = false
    if (saveQueued || saveRequested) {
      saveQueued = false
      void flushSave()
    }
  }
}

async function submit(auto = false) {
  const attempt = detail.value?.attempt
  if (!attempt || attempt.status !== 'inProgress' || submitting.value) return
  if (auto) {
    if (autoSubmittedAttempts.has(attempt.id)) return
    autoSubmittedAttempts.add(attempt.id)
  } else {
    const unanswered = questions.value.length - answeredCount.value
    try {
      await ElMessageBox.confirm(
        unanswered > 0
          ? `还有 ${unanswered} 道题未作答。提交后不可修改答案，确认提交吗？`
          : '提交后不可修改答案，提交后将立即完成判分。确认提交吗？',
        '提交岗位测评',
        { type: 'warning', confirmButtonText: '提交测评', cancelButtonText: '再检查一下' }
      )
    } catch {
      return
    }
  }
  submitting.value = true
  autoSubmitting.value = auto
  saveState.value = ''
  if (saveTimer !== undefined) {
    window.clearTimeout(saveTimer)
    saveTimer = undefined
  }
  try {
    const result = await post<CampSubmitResponse>(`/camp/positions/${props.positionId}/submit`, {
      answers: collectAnswers()
    })
    ElMessage[auto ? 'warning' : 'success'](
      auto ? '测评时间已到，系统已自动提交本次作答' : `测评已提交，得分 ${result.totalScore} / ${result.maxScore}`
    )
    await load(true)
  } catch (failure) {
    ElMessage.error(messageOf(failure))
    await load(true)
  } finally {
    submitting.value = false
    autoSubmitting.value = false
  }
}

function switchTab(question: CampTakingQuestion) {
  activeTab.value = question.type === 'practical' ? 'practical' : 'objective'
  questionIndexOpen.value = false
  void nextTick(() => {
    const target = document.getElementById(`camp-question-${question.id}`)
    const behavior = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'
    target?.focus({ preventScroll: true })
    target?.scrollIntoView({ behavior, block: 'start' })
  })
}

async function reviewReport() {
  if (!saveRequested) await flushSave()
  reportVisible.value = true
}

watch(remaining, value => {
  if (value <= 0 && inProgress.value && !submitting.value) {
    practicalVisible.value = false
    void submit(true)
  }
})
watch(inProgress, active => {
  if (!active) practicalVisible.value = false
})

onMounted(() => {
  void load()
  ticker = window.setInterval(() => {
    now.value = Date.now()
  }, 1000)
})

onBeforeUnmount(() => {
  if (ticker !== undefined) window.clearInterval(ticker)
  if (saveTimer !== undefined) window.clearTimeout(saveTimer)
  if (inProgress.value) void flushSave()
})
</script>

<template>
  <section class="page-heading">
    <div>
      <div class="eyebrow">岗位训练营 / 测评作答</div>
      <h1>{{ detail?.name || '岗位测评' }}</h1>
      <p v-if="detail">{{ detail.description || '按岗位要求完成技能测评。' }}</p>
      <p v-else>正在加载岗位信息…</p>
    </div>
    <div class="heading-actions">
      <el-button @click="emit('navigate', 'my-camp')">返回岗位列表</el-button>
      <el-button v-if="inProgress" :icon="Refresh" :loading="loading" @click="load()">刷新</el-button>
    </div>
  </section>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />

  <div v-if="loading && !detail" class="surface camp-loading">
    <el-skeleton :rows="6" animated />
  </div>

  <section v-else-if="detail && !inProgress" class="surface camp-overview">
    <div class="overview-head">
      <div>
        <div class="overview-tags">
          <el-tag v-if="detail.attempt" :type="campAttemptStatusType(detail.attempt.status)" size="small">
            {{ campAttemptLabel(detail.attempt.status) }}
          </el-tag>
          <el-tag v-else size="small" type="info" effect="plain">未开始</el-tag>
          <el-tag v-if="detail.attempt?.autoSubmitted" size="small" type="warning" effect="plain">超时自动提交</el-tag>
        </div>
        <h2>测评说明</h2>
        <p>{{ detail.description || '按岗位要求完成技能测评，系统将自动判分并生成能力报告。' }}</p>
      </div>
      <el-button
        v-if="!detail.attempt"
        type="primary"
        size="large"
        :icon="Promotion"
        :loading="starting"
        :disabled="!detail.canStart"
        @click="start"
      >
        开始测评
      </el-button>
    </div>
    <dl class="overview-meta">
      <div>
        <dt>技能维度</dt>
        <dd>{{ detail.skillCount }}</dd>
      </div>
      <div>
        <dt>测评题量</dt>
        <dd>{{ detail.questionCount }} 题</dd>
      </div>
      <div>
        <dt>实操题</dt>
        <dd>{{ detail.practicalCount }} 题</dd>
      </div>
      <div>
        <dt>满分</dt>
        <dd>{{ detail.maxScore }} 分</dd>
      </div>
      <div>
        <dt>限时</dt>
        <dd>{{ durationText(detail.durationMinutes) }}</dd>
      </div>
    </dl>

    <div v-if="graded && detail.attempt" class="result-panel">
      <div>
        <span class="result-label">本次测评得分</span>
        <strong
          >{{ detail.attempt.totalScore }}<em>/ {{ detail.attempt.maxScore }}</em></strong
        >
        <span class="result-percent">得分率 {{ percent }}%</span>
      </div>
      <dl>
        <div>
          <dt>客观题</dt>
          <dd>{{ detail.attempt.objectiveScore }} 分</dd>
        </div>
        <div>
          <dt>实操题</dt>
          <dd>{{ detail.attempt.practicalScore }} 分</dd>
        </div>
        <div>
          <dt>提交时间</dt>
          <dd>{{ detail.attempt.submittedAt ? new Date(detail.attempt.submittedAt).toLocaleString('zh-CN') : '—' }}</dd>
        </div>
      </dl>
      <div class="result-actions">
        <el-button type="primary" :icon="Select" @click="reviewReport">查看能力报告</el-button>
        <el-button @click="recordVisible = true">查看作答记录</el-button>
      </div>
    </div>

    <el-alert v-if="!detail.attempt" type="info" :closable="false" show-icon>
      <template #title>测评须知</template>
      <p>开始后立即计时且不可暂停；作答内容自动保存，到时系统自动提交。每个岗位仅有一次测评机会。</p>
    </el-alert>
  </section>

  <section v-if="detail && inProgress" class="take-layout">
    <aside class="surface take-nav" aria-label="测评进度">
      <div class="take-toolbar">
        <div class="take-status">
          <div class="take-countdown" :class="{ urgent: remaining < 300 }" role="timer">
            <el-icon aria-hidden="true"><Clock /></el-icon>
            <span>剩余 {{ countdownText(remaining) }}</span>
          </div>
          <p class="nav-progress">
            已作答 <strong>{{ answeredCount }}</strong> / {{ questions.length }} 题
          </p>
        </div>
        <el-progress :percentage="answeredPercent" :stroke-width="5" :show-text="false" aria-label="作答进度" />
        <div class="take-actions">
          <el-button
            class="question-index-toggle"
            :icon="Grid"
            :aria-expanded="questionIndexOpen"
            aria-controls="camp-answer-index"
            @click="questionIndexOpen = !questionIndexOpen"
          >
            答题卡
          </el-button>
          <el-button :loading="saving" :disabled="submitting" @click="flushSave(true)">保存答案</el-button>
          <el-button type="primary" :loading="submitting && !autoSubmitting" @click="submit(false)">提交测评</el-button>
        </div>
        <span class="save-state" :class="{ failed: saveState === '保存失败' }" role="status">
          {{ saveState || '自动保存已开启' }}
        </span>
      </div>
      <div id="camp-answer-index" class="nav-panel" :class="{ 'is-expanded': questionIndexOpen }">
        <h2>答题卡</h2>
        <div class="nav-grid">
          <button
            v-for="question in questions"
            :key="question.id"
            type="button"
            class="nav-chip"
            :class="{ answered: answered(question), practical: question.type === 'practical' }"
            :title="answered(question) ? '已作答' : '未作答'"
            @click="switchTab(question)"
          >
            {{ questionNumber(question) }}
          </button>
        </div>
        <p class="nav-legend"><span class="dot answered"></span>已作答<span class="dot"></span>未作答</p>
      </div>
    </aside>
    <div class="surface take-main">
      <el-tabs v-model="activeTab">
        <el-tab-pane label="客观题" name="objective">
          <p v-if="!objectiveQuestions.length" class="empty-hint">本岗位没有客观题，请完成实操题。</p>
          <article
            v-for="question in objectiveQuestions"
            :id="`camp-question-${question.id}`"
            :key="question.id"
            class="take-question"
            tabindex="-1"
          >
            <header>
              <span class="question-index">第 {{ questionNumber(question) }} 题</span>
              <el-tag size="small" effect="plain">{{ examQuestionTypeLabel(question.type) }}</el-tag>
              <el-tag size="small" type="info" effect="plain">{{ question.skillName }}</el-tag>
              <span class="question-score">{{ question.score }} 分</span>
            </header>
            <p class="question-stem">{{ question.stem }}</p>
            <el-radio-group
              v-if="question.type !== 'multiple'"
              :model-value="question.selected[0] ?? -1"
              class="option-list"
              @update:model-value="value => pickSingle(question, value)"
            >
              <el-radio v-for="(option, index) in question.options" :key="index" :value="index" class="option-item">
                {{ optionLetters[index] }}. {{ option }}
              </el-radio>
            </el-radio-group>
            <div v-else class="option-list">
              <el-checkbox
                v-for="(option, index) in question.options"
                :key="index"
                :model-value="question.selected.includes(index)"
                class="option-item"
                @update:model-value="() => toggleMultiple(question, index)"
              >
                {{ optionLetters[index] }}. {{ option }}
              </el-checkbox>
            </div>
          </article>
        </el-tab-pane>

        <el-tab-pane :label="`实操题 (${practicalQuestions.length})`" name="practical">
          <p v-if="!practicalQuestions.length" class="empty-hint">本岗位没有实操题。</p>
          <article
            v-for="question in practicalQuestions"
            :id="`camp-question-${question.id}`"
            :key="question.id"
            class="take-question"
            tabindex="-1"
          >
            <header>
              <span class="question-index">第 {{ questionNumber(question) }} 题</span>
              <el-tag size="small" type="warning" effect="plain">实操题</el-tag>
              <el-tag size="small" type="info" effect="plain">{{ question.skillName }}</el-tag>
              <span class="question-score">{{ question.score }} 分</span>
            </header>
            <p class="question-stem">{{ question.stem }}</p>
            <el-button type="primary" plain @click="practicalQuestionId = question.id">
              {{ answered(question) ? '继续实操作答' : '进入实操作答' }}
            </el-button>
          </article>
        </el-tab-pane>

        <el-tab-pane label="模拟环境" name="environment">
          <CampEnvironmentPanel
            :source-url="environmentUrl"
            :attempt-id="attemptId"
            empty-text="岗位未配置实训节点时，可直接作答理论题。"
          />
        </el-tab-pane>
      </el-tabs>
    </div>
  </section>

  <el-drawer
    v-model="practicalVisible"
    title="岗位测评实操作答"
    class="camp-practical-drawer"
    size="min(1060px, 100vw)"
    destroy-on-close
  >
    <template #header="{ titleId, titleClass }">
      <div class="practical-heading">
        <h2 :id="titleId" :class="titleClass">岗位测评实操作答</h2>
        <div class="take-countdown" :class="{ urgent: remaining < 300 }" role="timer">
          <el-icon aria-hidden="true"><Clock /></el-icon>
          <span>剩余 {{ countdownText(remaining) }}</span>
        </div>
      </div>
    </template>
    <div v-if="practicalQuestion" class="practical-sheet">
      <div class="practical-answer">
        <el-tag type="warning">第 {{ questionNumber(practicalQuestion) }} 题 · {{ practicalQuestion.score }} 分</el-tag>
        <p class="question-stem">{{ practicalQuestion.stem }}</p>
        <ExamPracticalPanel
          v-if="practicalQuestion.automaticPractical"
          :key="practicalQuestion.id"
          :url="`/camp/positions/${positionId}/practical/${practicalQuestion.id}`"
          :locked="submitting || remaining === 0"
          @saved="
            value => {
              if (practicalQuestion) practicalQuestion.hasWork = value
            }
          "
        />
        <el-input
          v-else
          v-model="practicalQuestion.textAnswer"
          type="textarea"
          :rows="8"
          maxlength="5000"
          aria-label="实操作答"
          show-word-limit
          placeholder="填写实操步骤或结果"
          @input="onPracticalInput"
        />
      </div>
      <section class="practical-resources">
        <h3>答题资源</h3>
        <CampEnvironmentPanel :source-url="environmentUrl" :attempt-id="attemptId" />
      </section>
    </div>
    <template #footer>
      <el-button @click="practicalVisible = false">返回测评</el-button>
      <el-button
        v-if="practicalQuestion && !practicalQuestion.automaticPractical"
        type="primary"
        :loading="saving"
        :disabled="submitting"
        @click="flushSave(true)"
      >
        保存答案
      </el-button>
    </template>
  </el-drawer>

  <el-dialog
    v-model="autoSubmitting"
    title="测评时间已到"
    width="min(24rem, 90vw)"
    :close-on-click-modal="false"
    :show-close="false"
  >
    <p>正在自动提交本次作答并完成判分，请稍候…</p>
  </el-dialog>

  <CampReportDrawer v-model="reportVisible" :attempt-id="attemptId" />
  <CampRecordDrawer v-model="recordVisible" :attempt-id="attemptId" />
</template>

<style scoped>
:global(.camp-practical-drawer .el-drawer__header) {
  margin-bottom: 0;
  padding: 16px 20px;
  border-bottom: 1px solid #e5edf5;
}

:global(.camp-practical-drawer .el-drawer__body) {
  min-height: 0;
  overscroll-behavior: contain;
  padding: 18px 20px;
}

:global(.camp-practical-drawer .el-drawer__footer) {
  padding: 12px 20px;
  border-top: 1px solid #e5edf5;
}

.practical-heading {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px 16px;
  min-width: 0;
}

.practical-heading h2 {
  flex: none;
  margin: 0;
  color: #23405c;
  font-size: 16px;
}

.practical-sheet {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1.2fr);
  gap: 16px;
  align-items: start;
}

.practical-answer,
.practical-resources {
  min-width: 0;
  padding: 14px;
  border: 1px solid #e5edf5;
  border-radius: 9px;
}

.practical-answer > .el-tag {
  margin-bottom: 8px;
}

.practical-resources :deep(.camp-environment) {
  grid-template-columns: minmax(0, 1fr);
}

@media (max-width: 760px) {
  :global(.camp-practical-drawer .el-drawer__header),
  :global(.camp-practical-drawer .el-drawer__body),
  :global(.camp-practical-drawer .el-drawer__footer) {
    padding-inline: 12px;
  }

  .practical-sheet {
    grid-template-columns: minmax(0, 1fr);
  }
}

.heading-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.camp-loading {
  padding: 24px;
}

.camp-overview {
  display: grid;
  gap: 18px;
  padding: 24px;
}

.overview-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 16px;
}

.overview-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-bottom: 8px;
}

.overview-head h2 {
  margin: 0 0 6px;
  color: #1c3955;
  font-size: 18px;
}

.overview-head p {
  max-width: 640px;
  margin: 0;
  color: #5d7288;
  font-size: 13px;
  line-height: 1.65;
}

.overview-meta {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
  gap: 12px;
  margin: 0;
  padding: 16px;
  background: #f6f9fc;
  border: 1px solid #e8eef6;
  border-radius: 10px;
}

.overview-meta dt {
  color: #7a8da1;
  font-size: 12px;
}

.overview-meta dd {
  margin: 4px 0 0;
  color: #23405c;
  font-size: 17px;
  font-weight: 600;
}

.result-panel {
  display: grid;
  gap: 14px;
  padding: 18px;
  background: linear-gradient(135deg, #eff8f4, #f3f8ff);
  border: 1px solid #d8e9e1;
  border-radius: 10px;
}

.result-label {
  display: block;
  color: #6c8377;
  font-size: 12px;
}

.result-panel strong {
  display: block;
  color: #22685a;
  font-size: 34px;
  line-height: 1.2;
}

.result-panel strong em {
  margin-left: 4px;
  color: #7a978e;
  font-size: 15px;
  font-style: normal;
  font-weight: 400;
}

.result-percent {
  color: #4f7d6f;
  font-size: 13px;
}

.result-panel dl {
  display: flex;
  flex-wrap: wrap;
  gap: 10px 26px;
  margin: 0;
}

.result-panel dt {
  color: #7a8da1;
  font-size: 11px;
}

.result-panel dd {
  margin: 3px 0 0;
  color: #2d4a63;
  font-size: 13px;
}

.result-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.take-layout {
  --assessment-sticky-top: 76px;
  display: grid;
  grid-template-columns: minmax(0, 1fr) 285px;
  align-items: start;
  gap: 16px;
  overflow-anchor: none;
}

.take-main {
  grid-column: 1;
  grid-row: 1;
  min-width: 0;
  padding: 14px 22px 18px;
}

.take-toolbar {
  display: grid;
  gap: 9px;
}

.take-status {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 2px 12px;
}

.take-countdown {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  color: #28695d;
  font-size: 15px;
  font-weight: 650;
  font-variant-numeric: tabular-nums;
}

.take-countdown.urgent {
  color: #a73c17;
}

.save-state {
  color: var(--text-muted, #586d84);
  font-size: 12px;
}

.save-state.failed {
  color: #a73c17;
}

.take-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.take-actions :deep(.el-button) {
  margin: 0;
}

.question-index-toggle {
  display: none;
}

.take-question {
  padding: 16px 0 20px;
  border-bottom: 1px dashed #e6edf5;
  scroll-margin-top: calc(var(--assessment-sticky-top) + 8px);
}

.take-question:focus-visible {
  outline: 2px solid var(--el-color-primary);
  outline-offset: 2px;
}

.take-question:last-child {
  border-bottom: 0;
}

.take-question header {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 10px;
}

.question-index {
  color: #2a4a67;
  font-size: 14px;
  font-weight: 600;
}

.question-score {
  margin-left: auto;
  color: #8093a6;
  font-size: 12px;
}

.question-stem {
  margin: 0 0 12px;
  color: #223d57;
  font-size: 14px;
  line-height: 1.7;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.option-list {
  display: grid;
  gap: 8px;
  width: 100%;
}

.option-item {
  display: flex;
  align-items: flex-start;
  height: auto;
  padding: 8px 10px;
  margin: 0;
  border: 1px solid #e8eef6;
  border-radius: 7px;
  white-space: normal;
}

.option-item :deep(.el-radio__label),
.option-item :deep(.el-checkbox__label) {
  padding-left: 8px;
  line-height: 1.55;
  overflow-wrap: anywhere;
}

.take-nav {
  grid-column: 2;
  grid-row: 1;
  position: sticky;
  top: var(--assessment-sticky-top);
  z-index: 3;
  display: grid;
  gap: 12px;
  min-width: 0;
  padding: 16px;
  box-shadow: 0 4px 14px #1c39550f;
}

.nav-panel {
  display: grid;
  gap: 8px;
  padding-top: 12px;
  border-top: 1px solid #edf2f7;
}

.nav-panel h2 {
  margin: 0;
  color: #23405c;
  font-size: 15px;
}

.nav-progress {
  margin: 0;
  color: var(--text-muted, #586d84);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.nav-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(34px, 1fr));
  gap: 6px;
  max-height: min(30vh, 230px);
  overflow-y: auto;
  overscroll-behavior: contain;
  padding: 2px;
}

.nav-chip {
  height: 34px;
  color: #64778b;
  font-size: 12px;
  background: #f5f8fb;
  border: 1px solid #e2eaf3;
  border-radius: 6px;
  cursor: pointer;
}

.nav-chip.answered {
  color: #fff;
  background: #357a6e;
  border-color: #357a6e;
}

.nav-chip.practical {
  font-weight: 600;
}

.nav-legend {
  display: flex;
  align-items: center;
  gap: 5px;
  margin: 0;
  color: var(--text-muted, #586d84);
  font-size: 12px;
}

.dot {
  width: 9px;
  height: 9px;
  margin-left: 6px;
  background: #f5f8fb;
  border: 1px solid #d7e1ec;
  border-radius: 50%;
}

.dot.answered {
  background: #357a6e;
  border-color: #357a6e;
}

.empty-hint {
  max-width: 420px;
  margin: 18px auto;
  color: #718396;
  font-size: 13px;
  text-align: center;
}

@media (max-width: 1050px) {
  .take-layout {
    grid-template-columns: minmax(0, 1fr);
    gap: 10px;
  }

  .take-main {
    grid-row: 2;
  }

  .take-nav {
    grid-column: 1;
    padding: 9px 12px;
  }

  .take-toolbar {
    gap: 5px;
  }

  .take-actions .question-index-toggle {
    display: inline-flex;
    margin-inline-end: auto;
  }

  .nav-panel {
    display: none;
  }

  .nav-panel.is-expanded {
    display: grid;
  }

  .nav-panel h2 {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip-path: inset(50%);
  }

  .nav-grid {
    grid-template-columns: repeat(auto-fill, minmax(40px, 1fr));
    max-height: min(28vh, 220px);
  }

  .nav-chip {
    height: 40px;
  }

  .take-question {
    scroll-margin-top: calc(var(--assessment-sticky-top) + 124px);
  }
}

@media (max-width: 800px) {
  .take-layout {
    --assessment-sticky-top: 70px;
  }
}

@media (max-width: 540px) {
  .take-main {
    padding: 8px 12px 14px;
  }

  .take-actions :deep(.el-button) {
    padding-inline: 9px;
  }
}
</style>

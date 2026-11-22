<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, onMounted, reactive, ref, watch } from 'vue'
import {
  ArrowDown,
  ArrowLeft,
  ArrowRight,
  Check,
  Clock,
  Collection,
  DataAnalysis,
  Document,
  EditPen,
  Monitor,
  Reading,
  Refresh
} from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { api, dateTime, messageOf, post } from '../api'
import { downloadFile } from '../exam'
import {
  classroomStatusOptions,
  classroomStatusType,
  courseTypeOptions,
  labelOf,
  questionAnswerText,
  questionTypeOptions,
  recordStatusLabels,
  recordStatusType,
  resourceKindOptions
} from '../teaching'
import type {
  ClassroomChapter,
  ClassroomContentResponse,
  ClassroomPracticeResult,
  ClassroomResource,
  ClassroomStatus,
  CourseQuestion,
  CourseResourceFile,
  LearningRecord
} from '../types'
import ClassroomCaseWork from './ClassroomCaseWork.vue'
const TerminalDialog = defineAsyncComponent(() => import('./TerminalDialog.vue'))

const props = defineProps<{ classroomId: string; resourceId?: string; backKey: string }>()
const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()

const data = ref<ClassroomContentResponse | null>(null)
const loading = ref(true)
const error = ref('')
const actionError = ref('')
const selectedResourceId = ref('')
const practices = reactive(new Map<string, { answers: Record<string, number[]>; result?: ClassroomPracticeResult }>())
const submitting = ref(false)
const completing = ref(false)
const selectedFileId = ref('')
const outlineExpanded = ref(false)
const outlineToggle = ref<HTMLButtonElement | null>(null)
const outlineNavigation = ref<HTMLElement | null>(null)
const lessonTitle = ref<HTMLElement | null>(null)
const attachmentPreview = ref<HTMLElement | null>(null)
let previewTrigger: HTMLElement | null = null
const pendingStarts = new Map<string, Promise<void>>()

const resourceIcons = { theory: Reading, theoryPractice: EditPen, case: Document, casePractice: Monitor }

const chapters = computed<ClassroomChapter[]>(() => data.value?.tree || [])
const allResources = computed(() =>
  chapters.value.flatMap(chapter => chapter.sections.flatMap(section => section.resources))
)
const resourceCount = computed(() => allResources.value.length)
const selectedIndex = computed(() => allResources.value.findIndex(resource => resource.id === selectedResourceId.value))
const selectedResource = computed(() => allResources.value[selectedIndex.value])
const previousResource = computed(() => allResources.value[selectedIndex.value - 1])
const nextResource = computed(() => allResources.value[selectedIndex.value + 1])
const activePractice = computed(() => practices.get(selectedResourceId.value))
const answers = computed(() => activePractice.value?.answers ?? {})
const result = computed(() => activePractice.value?.result)
const savingProgress = computed(() => submitting.value || completing.value)
const selectedLocation = computed(() => {
  for (const chapter of chapters.value) {
    const section = chapter.sections.find(item =>
      item.resources.some(resource => resource.id === selectedResourceId.value)
    )
    if (section) return [chapter.name, section.name].filter(Boolean).join(' / ')
  }
  return ''
})
const preview = computed(() => !!data.value?.isPreview)
const canLearn = computed(() => !!data.value?.canLearn)
const records = computed(() => new Map(data.value?.records.map(record => [record.resourceId, record])))
const selectedRecord = computed(() => records.value.get(selectedResourceId.value))
const isPractice = computed(() => selectedResource.value?.kind === 'theoryPractice')
const isCasePractice = computed(() => selectedResource.value?.kind === 'casePractice')
const isLecture = computed(() => selectedResource.value?.kind === 'theory' || selectedResource.value?.kind === 'case')
const selectedFile = computed(() => selectedResource.value?.files.find(file => file.id === selectedFileId.value))
const selectedFileUrl = computed(() => `/api${fileRoute(selectedFileId.value)}?preview=true`)
const selectedQuestions = computed(() => selectedResource.value?.questions || [])
const resourceUrl = computed(() => `/classrooms/${props.classroomId}/resources/${selectedResourceId.value}`)
type CabinetDevice = { id: string; name: string; cabinetName: string; isSimulated: boolean; connectionType: string }
const cabinetDevices = ref<CabinetDevice[]>([])
const terminalDevice = ref<CabinetDevice | null>(null)
const cabinetError = ref('')

watch([resourceUrl, canLearn, isCasePractice], async ([url, allowed, practical], _, onCleanup) => {
  let cancelled = false
  onCleanup(() => {
    cancelled = true
  })
  cabinetDevices.value = []
  cabinetError.value = ''
  terminalDevice.value = null
  if (!allowed || !practical) return
  try {
    const devices = await api<CabinetDevice[]>(`${url}/cabinet-devices`)
    if (!cancelled) cabinetDevices.value = devices
  } catch (failure) {
    if (!cancelled) cabinetError.value = messageOf(failure)
  }
})
const completedResources = computed(
  () =>
    new Set(
      allResources.value
        .filter(resource => {
          const record = records.value.get(resource.id)
          return record && (record.status === 'completed' || record.attemptCount > 0)
        })
        .map(resource => resource.id)
    )
)
const completedCount = computed(() => completedResources.value.size)
const chapterProgress = computed(
  () =>
    new Map(
      chapters.value.map(chapter => {
        const resources = chapter.sections.flatMap(section => section.resources)
        const completed = resources.filter(resource => completedResources.value.has(resource.id)).length
        return [
          chapter.id,
          {
            completed,
            total: resources.length
          }
        ]
      })
    )
)
const averageScore = computed(() => {
  const scores = (data.value?.records || []).filter(record => record.attemptCount > 0).map(record => record.bestScore)
  if (!scores.length) return null
  return Math.round((scores.reduce((sum, score) => sum + score, 0) / scores.length) * 10) / 10
})
const resultMap = computed(() => new Map((result.value?.questions || []).map(item => [item.questionId, item])))

watch(
  [selectedResourceId, outlineExpanded],
  () => {
    const outline = outlineNavigation.value
    const current = outline?.querySelector<HTMLElement>('[aria-current="true"]')
    if (!outline?.clientHeight || !current) return
    const visible = outline.getBoundingClientRect()
    const item = current.getBoundingClientRect()
    if (item.top < visible.top) outline.scrollTop += item.top - visible.top
    else if (item.bottom > visible.bottom) outline.scrollTop += item.bottom - visible.bottom
  },
  { flush: 'post' }
)

function initialResource() {
  const requested = allResources.value.find(resource => resource.id === props.resourceId)
  if (requested) return requested
  if (canLearn.value) {
    const unfinished = allResources.value.filter(resource => !completedResources.value.has(resource.id))
    const inProgress = unfinished.find(resource => records.value.get(resource.id)?.status === 'inProgress')
    if (inProgress || unfinished.length) return inProgress || unfinished[0]
  }
  return allResources.value[0]
}

async function load(preserve = false) {
  if (!props.classroomId) {
    error.value = '课堂参数缺失，请从课堂列表重新进入。'
    loading.value = false
    return
  }
  if (!preserve) loading.value = true
  error.value = ''
  const keepId = preserve ? selectedResourceId.value : ''
  try {
    const next = await api<ClassroomContentResponse>(`/classrooms/${props.classroomId}/content`)
    data.value = next
  } catch (failure) {
    if (!preserve) data.value = null
    error.value = messageOf(failure)
    return
  } finally {
    loading.value = false
  }
  if (preserve && keepId && allResources.value.some(resource => resource.id === keepId)) {
    selectedResourceId.value = keepId
    return
  }
  const target = initialResource()
  if (target) chooseResource(target, !preserve)
  else selectedResourceId.value = ''
}

function chooseResource(resource: ClassroomResource, autoStart = true) {
  selectedResourceId.value = resource.id
  selectedFileId.value = ''
  previewTrigger = null
  actionError.value = ''
  if (resource.kind === 'theoryPractice' && !practices.has(resource.id)) {
    const entries = resource.questions.flatMap(question => (question.id ? [[question.id, []]] : []))
    practices.set(resource.id, { answers: Object.fromEntries(entries) })
  }
  if (autoStart && canLearn.value && resource.kind !== 'casePractice' && !result.value)
    void startLearning(resource.id, resource.kind === 'theoryPractice')
}

function focusInView(element: HTMLElement | null) {
  element?.focus({ preventScroll: true })
  element?.scrollIntoView({ block: 'nearest' })
}

async function selectResource(resource: ClassroomResource) {
  if (savingProgress.value) return
  if (resource.id !== selectedResourceId.value) chooseResource(resource)
  outlineExpanded.value = false
  await nextTick()
  focusInView(lessonTitle.value)
}

function dismissOutline(event: KeyboardEvent) {
  if (!outlineExpanded.value || !outlineToggle.value?.offsetParent) return
  event.stopPropagation()
  outlineExpanded.value = false
  outlineToggle.value.focus({ preventScroll: true })
}

async function openFilePreview(file: CourseResourceFile, event: MouseEvent) {
  previewTrigger = event.currentTarget instanceof HTMLElement ? event.currentTarget : null
  selectedFileId.value = file.id
  await nextTick()
  focusInView(attachmentPreview.value)
}

async function closeFilePreview() {
  selectedFileId.value = ''
  await nextTick()
  focusInView(previewTrigger)
  previewTrigger = null
}

function fileRoute(fileId: string) {
  return `/classrooms/${props.classroomId}/files/${fileId}`
}

async function downloadAttachment(file: CourseResourceFile) {
  try {
    await downloadFile(fileRoute(file.id), file.name)
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  }
}

function upsertRecord(record: LearningRecord) {
  if (!data.value) return
  const index = data.value.records.findIndex(item => item.resourceId === record.resourceId)
  if (index >= 0) data.value.records[index] = record
  else data.value.records.push(record)
}

function startLearning(resourceId: string, isTheoryPractice: boolean) {
  const existing = records.value.get(resourceId)
  if (!isTheoryPractice && existing && existing.status !== 'notStarted') return
  if (pendingStarts.has(resourceId)) return
  const pending = post<LearningRecord>(`/classrooms/${props.classroomId}/resources/${resourceId}/start`)
    .then(upsertRecord)
    .catch(failure => {
      if (selectedResourceId.value === resourceId) actionError.value = messageOf(failure)
    })
    .finally(() => pendingStarts.delete(resourceId))
  pendingStarts.set(resourceId, pending)
}

async function completeLearning() {
  const resourceId = selectedResource.value?.id
  if (!resourceId || savingProgress.value) return
  completing.value = true
  actionError.value = ''
  try {
    await pendingStarts.get(resourceId)
    const record = await post<LearningRecord>(`/classrooms/${props.classroomId}/resources/${resourceId}/complete`)
    upsertRecord(record)
    actionError.value = ''
    ElMessage.success('已标记为完成')
  } catch (failure) {
    actionError.value = messageOf(failure)
  } finally {
    completing.value = false
  }
}

function setSingleAnswer(question: CourseQuestion, value: unknown) {
  if (!question.id) return
  const number = Number(value)
  if (activePractice.value) activePractice.value.answers[question.id] = Number.isFinite(number) ? [number] : []
}

async function submitPractice() {
  const practice = activePractice.value
  if (!selectedResource.value || !selectedQuestions.value.length || !practice || savingProgress.value) return
  const submittingResourceId = selectedResourceId.value
  const missing = selectedQuestions.value.filter(
    question => !question.id || !practice.answers[question.id]?.length
  ).length
  if (missing) {
    ElMessage.warning(`还有 ${missing} 道题未作答，请完成后再提交。`)
    return
  }
  submitting.value = true
  actionError.value = ''
  const submittedAnswers = selectedQuestions.value.map(question => ({
    questionId: question.id,
    selected: [...(practice.answers[question.id || ''] || [])]
  }))
  try {
    await pendingStarts.get(submittingResourceId)
    const submitted = await post<ClassroomPracticeResult>(
      `/classrooms/${props.classroomId}/resources/${submittingResourceId}/submit`,
      { answers: submittedAnswers }
    )
    practice.result = submitted
    actionError.value = ''
    ElMessage.success(`练习已提交，得分 ${submitted.score} / ${submitted.maxScore}`)
    await load(true)
  } catch (failure) {
    actionError.value = messageOf(failure)
  } finally {
    submitting.value = false
  }
}

async function retryPractice() {
  if (!selectedResource.value) return
  practices.delete(selectedResourceId.value)
  chooseResource(selectedResource.value)
  await nextTick()
  focusInView(lessonTitle.value)
}

function optionLetter(index: number) {
  return String.fromCharCode(65 + index)
}

function isCorrectOption(question: CourseQuestion, index: number) {
  return !!question.answers?.includes(index)
}

function questionId(question: CourseQuestion) {
  return question.id || ''
}

function statusText(status: string | undefined) {
  return recordStatusLabels[status || 'notStarted'] || '未开始'
}

function outlineResourceDetail(resource: ClassroomResource) {
  const kind = labelOf(resourceKindOptions, resource.kind)
  if (preview.value) return `${kind}，${resource.completedCount} 人完成`
  const status = completedResources.value.has(resource.id) ? 'completed' : records.value.get(resource.id)?.status
  return `${kind}，${statusText(status)}`
}

const backLabels: Record<string, string> = {
  'skill-graph': '返回技能图谱',
  'student-home': '返回学习首页',
  'teacher-home': '返回教学首页',
  'classroom-statistics': '返回课堂统计'
}
const backLabel = computed(() => backLabels[props.backKey] || '返回课堂列表')

function back() {
  if (props.backKey === 'classroom-statistics') emit('navigate', props.backKey, { classroomId: props.classroomId })
  else emit('navigate', props.backKey)
}

function openStatistics() {
  emit('navigate', 'classroom-statistics', { classroomId: props.classroomId, backKey: 'classroom-content' })
}

onMounted(() => load())
</script>

<template>
  <section class="content-heading">
    <button type="button" class="back-link" @click="back">
      <el-icon><ArrowLeft /></el-icon>{{ backLabel }}
    </button>
    <div v-if="data" class="content-heading-main">
      <div>
        <h1>{{ data.classroom.name }}</h1>
        <p>
          {{ data.course.name }}
          <span class="meta-dot">·</span>
          {{ labelOf(courseTypeOptions, data.course.type) }}
          <template v-if="data.classroom.teacherName">
            <span class="meta-dot">·</span>
            {{ data.classroom.teacherName }}
          </template>
        </p>
      </div>
      <div class="content-heading-tags">
        <el-tag :type="classroomStatusType(data.classroom.status as ClassroomStatus)" effect="light">
          {{ labelOf(classroomStatusOptions, data.classroom.status) }}
        </el-tag>
        <el-tag v-if="preview" type="info" effect="plain">教师预览</el-tag>
        <el-button v-if="!preview" size="small" :icon="DataAnalysis" @click="openStatistics">课堂统计</el-button>
      </div>
    </div>
    <div v-if="data" class="content-time">
      <el-icon><Clock /></el-icon>
      <span
        ><time :datetime="data.classroom.openAt">{{ dateTime(data.classroom.openAt) }}</time> 至
        <time :datetime="data.classroom.closeAt">{{ dateTime(data.classroom.closeAt) }}</time></span
      >
    </div>
  </section>

  <section v-if="loading" class="surface content-loading">
    <el-skeleton :rows="7" animated />
  </section>
  <section v-else-if="error" class="surface content-loading">
    <el-result icon="error" title="课堂内容加载失败" :sub-title="error">
      <template #extra><el-button type="primary" :icon="Refresh" @click="load()">重试</el-button></template>
    </el-result>
  </section>

  <template v-else-if="data">
    <el-alert
      v-if="!canLearn && !preview && data.classroom.status === 'scheduled'"
      title="课堂尚未开放，可先查看课程内容；开课后才能记录学习进度和提交练习。"
      type="info"
      show-icon
      :closable="false"
      class="section-notice"
    />
    <el-alert
      v-else-if="!canLearn && !preview && data.classroom.status === 'closed'"
      title="课堂已结课，学习记录和练习成绩已归档，当前为只读查看。"
      type="info"
      show-icon
      :closable="false"
      class="section-notice"
    />
    <div v-if="preview" class="preview-note">
      <el-icon><Reading /></el-icon>预览模式：显示全部答案与解析，不会产生学习记录。
    </div>

    <section v-if="!preview" class="study-progress surface">
      <div class="study-progress-copy">
        <span>我的学习进度</span>
        <strong>{{ completedCount }} / {{ resourceCount }}</strong>
      </div>
      <el-progress
        :percentage="resourceCount ? Math.round((completedCount / resourceCount) * 100) : 0"
        :stroke-width="9"
        aria-label="已完成的课堂资源比例"
      />
      <div class="study-progress-score">
        <span>练习最佳均分</span><strong>{{ averageScore ?? '—' }}</strong>
      </div>
    </section>

    <div v-if="!resourceCount" class="surface content-loading">
      <el-empty description="课程暂未配置学习资源">
        <p class="empty-hint">请联系管理员在课程管理中补充章节、小节和资源。</p>
      </el-empty>
    </div>

    <div v-else class="content-layout">
      <aside class="surface content-outline" aria-label="课堂资源" @keydown.esc="dismissOutline">
        <div class="outline-heading">
          <h2>
            <el-icon aria-hidden="true"><Collection /></el-icon>课程目录
          </h2>
          <span>{{ resourceCount }} 个资源</span>
        </div>
        <button
          ref="outlineToggle"
          type="button"
          class="outline-toggle"
          :aria-expanded="outlineExpanded"
          aria-controls="classroom-course-outline"
          @click="outlineExpanded = !outlineExpanded"
        >
          <el-icon aria-hidden="true"><Collection /></el-icon>
          <strong>课程目录</strong>
          <span v-if="selectedResource" class="outline-position"
            >第 {{ selectedIndex + 1 }} / {{ resourceCount }} 项</span
          >
          <el-icon class="outline-chevron" aria-hidden="true"><ArrowDown /></el-icon>
        </button>
        <nav
          id="classroom-course-outline"
          ref="outlineNavigation"
          class="outline-navigation"
          :class="{ 'outline-open': outlineExpanded }"
          aria-label="课程目录"
        >
          <section v-for="chapter in chapters" :key="chapter.id" class="outline-chapter">
            <h3>{{ chapter.name }}</h3>
            <p v-if="!preview && chapterProgress.get(chapter.id)?.total" class="chapter-progress">
              已完成 {{ chapterProgress.get(chapter.id)?.completed }} / {{ chapterProgress.get(chapter.id)?.total }}
            </p>
            <div v-for="section in chapter.sections" :key="section.id" class="outline-section">
              <div class="outline-section-title">
                <span>{{ section.name }}</span>
                <small v-if="section.durationMinutes">{{ section.durationMinutes }} 分钟</small>
              </div>
              <button
                v-for="resource in section.resources"
                :key="resource.id"
                type="button"
                class="outline-resource"
                :class="{ active: resource.id === selectedResourceId }"
                :aria-label="resource.name"
                :aria-description="outlineResourceDetail(resource)"
                :aria-current="resource.id === selectedResourceId ? 'true' : undefined"
                :disabled="savingProgress"
                @click="selectResource(resource)"
              >
                <span class="outline-resource-icon" aria-hidden="true"
                  ><el-icon><component :is="resourceIcons[resource.kind]" /></el-icon
                ></span>
                <span class="outline-resource-copy">
                  <span class="outline-resource-name">{{ resource.name }}</span>
                  <small>{{ labelOf(resourceKindOptions, resource.kind) }}</small>
                </span>
                <el-icon v-if="completedResources.has(resource.id)" class="done-icon" aria-hidden="true"
                  ><Check
                /></el-icon>
                <small v-else-if="preview">{{ resource.completedCount }} 人完成</small>
                <span
                  v-else-if="records.get(resource.id)?.status === 'inProgress'"
                  class="outline-in-progress"
                  aria-hidden="true"
                />
              </button>
              <p v-if="!section.resources.length" class="outline-empty">本节暂无资源</p>
            </div>
            <p v-if="!chapter.sections.length" class="outline-empty">本章暂无小节</p>
          </section>
        </nav>
      </aside>

      <section v-if="selectedResource" class="surface resource-view">
        <header class="resource-view-heading">
          <div class="resource-heading-text">
            <p v-if="selectedLocation" class="resource-location">{{ selectedLocation }}</p>
            <div class="resource-kind-line">
              <el-tag size="small" effect="plain">{{ labelOf(resourceKindOptions, selectedResource.kind) }}</el-tag>
              <span v-if="selectedResource.durationMinutes"
                ><el-icon aria-hidden="true"><Clock /></el-icon>{{ selectedResource.durationMinutes }} 分钟</span
              >
              <span v-if="isPractice">满分 {{ selectedResource.maxScore }} 分</span>
            </div>
            <h2 ref="lessonTitle" tabindex="-1">{{ selectedResource.name }}</h2>
          </div>
          <div class="resource-view-status">
            <span class="resource-view-index">第 {{ selectedIndex + 1 }} / {{ resourceCount }} 项</span>
            <el-tag v-if="!preview" :type="recordStatusType(selectedRecord?.status)" size="small">
              {{ statusText(selectedRecord?.status) }}
            </el-tag>
          </div>
        </header>

        <el-alert
          v-if="actionError"
          :title="actionError"
          type="error"
          show-icon
          :closable="false"
          class="section-notice"
        />

        <div class="resource-body lecture-layout" :class="{ 'lecture-split': isLecture && selectedFile }">
          <div class="lecture-content">
            <h3>学习内容</h3>
            <div v-if="selectedResource.content" class="rich-text">{{ selectedResource.content }}</div>
            <p v-else class="muted-text">该资源暂未填写学习内容。</p>
            <template v-if="selectedResource.files.length">
              <h3 class="attachment-title">学习资料</h3>
              <ul class="lecture-files">
                <li v-for="file in selectedResource.files" :key="file.id">
                  <span class="attachment-name">{{ file.name }}</span>
                  <div class="attachment-actions">
                    <el-button
                      v-if="isLecture && file.canPreview"
                      link
                      type="primary"
                      :aria-label="`分屏预览 ${file.name}`"
                      :aria-expanded="selectedFileId === file.id"
                      @click="openFilePreview(file, $event)"
                      >分屏预览</el-button
                    >
                    <el-button link type="primary" :aria-label="`下载 ${file.name}`" @click="downloadAttachment(file)">
                      下载
                    </el-button>
                  </div>
                </li>
              </ul>
            </template>
          </div>
          <section
            v-if="isLecture && selectedFile"
            ref="attachmentPreview"
            class="lecture-preview"
            aria-label="附件预览"
            tabindex="-1"
          >
            <header>
              <strong>{{ selectedFile.name }}</strong>
              <el-button link @click="closeFilePreview">关闭分屏</el-button>
            </header>
            <iframe :key="selectedFile.id" :src="selectedFileUrl" :title="selectedFile.name" />
          </section>
        </div>

        <div v-if="selectedResource.topology" class="resource-body">
          <h3>实训拓扑</h3>
          <pre class="topology-block">{{ selectedResource.topology }}</pre>
        </div>

        <div v-if="isCasePractice && canLearn" class="resource-body">
          <el-alert v-if="cabinetError" type="error" :title="cabinetError" :closable="false" />
          <section v-if="cabinetDevices.length">
            <h3>小组分配机柜</h3>
            <el-table :data="cabinetDevices" row-key="id" size="small">
              <el-table-column prop="cabinetName" label="机柜" min-width="130" />
              <el-table-column prop="name" label="设备" min-width="130" />
              <el-table-column label="操作" width="100">
                <template #default="{ row }">
                  <el-button link type="primary" @click="terminalDevice = row as CabinetDevice">连接终端</el-button>
                </template>
              </el-table-column>
            </el-table>
          </section>
          <h3>模拟实操环境</h3>
          <ClassroomCaseWork
            :key="selectedResourceId"
            :url="`${resourceUrl}/case-work`"
            :start-url="`${resourceUrl}/start`"
            :has-record="!!selectedRecord"
            @started="upsertRecord"
            @saved="emit('navigate', backKey)"
            @finished="load(true)"
          />
        </div>

        <div v-if="selectedResource.questionCount" class="resource-body">
          <div class="question-heading">
            <h3>练习题目</h3>
            <span>{{ selectedResource.questionCount }} 题 · 满分 {{ selectedResource.maxScore }} 分</span>
          </div>

          <el-alert
            v-if="result"
            :title="`本次得分 ${result.score} / ${result.maxScore}，${result.passed ? '已达及格线' : '未达及格线'}。`"
            :type="result.passed ? 'success' : 'warning'"
            show-icon
            :closable="false"
            class="practice-result"
          />
          <dl v-if="result" class="practice-summary">
            <div class="practice-time">
              <dt>开始时间</dt>
              <dd>
                <time :datetime="result.startedAt">{{ dateTime(result.startedAt) }}</time>
              </dd>
            </div>
            <div class="practice-time">
              <dt>结束时间</dt>
              <dd>
                <time :datetime="result.submittedAt">{{ dateTime(result.submittedAt) }}</time>
              </dd>
            </div>
            <div>
              <dt>练习次数</dt>
              <dd>{{ result.attemptCount }}</dd>
            </div>
            <div>
              <dt>平均分</dt>
              <dd>{{ result.averageScore }}</dd>
            </div>
            <div>
              <dt>最高得分</dt>
              <dd>{{ result.bestScore }}</dd>
            </div>
          </dl>

          <article
            v-for="(question, questionIndex) in selectedQuestions"
            :key="questionId(question)"
            class="question-card"
          >
            <div class="question-title">
              <span class="question-number">{{ questionIndex + 1 }}</span>
              <div>
                <strong>{{ question.stem }}</strong>
                <small>{{ labelOf(questionTypeOptions, question.type) }} · {{ question.score }} 分</small>
              </div>
            </div>

            <div v-if="preview" class="option-list">
              <div
                v-for="(option, optionIndex) in question.options"
                :key="optionIndex"
                class="option-line"
                :class="{ correct: isCorrectOption(question, optionIndex) }"
              >
                <span class="option-letter">{{ optionLetter(optionIndex) }}</span>
                <span>{{ option }}</span>
                <span v-if="isCorrectOption(question, optionIndex)" class="correct-label">正确答案</span>
              </div>
              <p v-if="question.analysis" class="analysis-text">解析：{{ question.analysis }}</p>
            </div>

            <div v-else class="answer-area">
              <el-radio-group
                v-if="question.type === 'single' || question.type === 'judge'"
                :model-value="answers[questionId(question)]?.[0]"
                :disabled="!!result || submitting || !canLearn"
                class="answer-options"
                @update:model-value="(value: unknown) => setSingleAnswer(question, value)"
              >
                <el-radio
                  v-for="(option, optionIndex) in question.options"
                  :key="optionIndex"
                  :value="optionIndex"
                  class="answer-option"
                >
                  {{ optionLetter(optionIndex) }}. {{ option }}
                </el-radio>
              </el-radio-group>
              <el-checkbox-group
                v-else
                v-model="answers[questionId(question)]"
                :disabled="!!result || submitting || !canLearn"
                class="answer-options"
              >
                <el-checkbox
                  v-for="(option, optionIndex) in question.options"
                  :key="optionIndex"
                  :value="optionIndex"
                  class="answer-option"
                >
                  {{ optionLetter(optionIndex) }}. {{ option }}
                </el-checkbox>
              </el-checkbox-group>

              <div
                v-if="resultMap.get(questionId(question))"
                class="question-feedback"
                :class="{ ok: resultMap.get(questionId(question))?.correct }"
              >
                <strong>{{ resultMap.get(questionId(question))?.correct ? '回答正确' : '回答错误' }}</strong>
                <span
                  >本次作答：{{
                    questionAnswerText({
                      options: question.options,
                      answers: resultMap.get(questionId(question))?.selected
                    })
                  }}</span
                >
                <span
                  >正确答案：{{
                    questionAnswerText({
                      options: question.options,
                      answers: resultMap.get(questionId(question))?.correctAnswers
                    })
                  }}</span
                >
                <p v-if="resultMap.get(questionId(question))?.analysis">
                  解析：{{ resultMap.get(questionId(question))?.analysis }}
                </p>
              </div>
            </div>
          </article>
        </div>

        <footer v-if="canLearn && !preview && !isCasePractice" class="resource-actions">
          <el-button
            v-if="!isPractice"
            type="primary"
            :icon="Check"
            :loading="completing"
            :disabled="selectedRecord?.status === 'completed'"
            @click="completeLearning"
          >
            {{ selectedRecord?.status === 'completed' ? '已完成' : '标记完成' }}
          </el-button>
          <el-button
            v-if="isPractice && !result"
            type="primary"
            :loading="submitting"
            :disabled="!selectedQuestions.length"
            @click="submitPractice"
            >提交练习</el-button
          >
          <el-button v-if="isPractice && result" type="primary" @click="retryPractice">再练一次</el-button>
          <span v-if="selectedRecord?.attemptCount" class="record-hint">
            已作答 {{ selectedRecord.attemptCount }} 次，最佳 {{ selectedRecord.bestScore }} 分
          </span>
        </footer>
        <nav v-if="resourceCount > 1" class="resource-pagination" aria-label="切换学习资源">
          <button
            v-if="previousResource"
            type="button"
            class="resource-page-button"
            :disabled="savingProgress"
            @click="selectResource(previousResource)"
          >
            <el-icon aria-hidden="true"><ArrowLeft /></el-icon>
            <span
              ><small>上一项</small><strong>{{ previousResource.name }}</strong></span
            >
          </button>
          <button
            v-if="nextResource"
            type="button"
            class="resource-page-button next-resource"
            :disabled="savingProgress"
            @click="selectResource(nextResource)"
          >
            <span
              ><small>下一项</small><strong>{{ nextResource.name }}</strong></span
            >
            <el-icon aria-hidden="true"><ArrowRight /></el-icon>
          </button>
        </nav>
      </section>
    </div>
  </template>
  <TerminalDialog v-if="terminalDevice" :device="terminalDevice" @close="terminalDevice = null" />
</template>

<style scoped>
.chapter-progress {
  margin: 0 0 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.lecture-split {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 20px;
}

.lecture-content {
  min-inline-size: 0;
}

.lecture-content .attachment-title {
  margin-top: 20px;
}

.lecture-files {
  display: grid;
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.lecture-preview:focus-visible,
.resource-view-heading h2:focus-visible {
  outline: 2px solid var(--el-color-primary);
  outline-offset: 3px;
}

.lecture-files li,
.lecture-preview header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 4px 12px;
}

.lecture-files li {
  padding: 6px 12px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 8px;
  background: var(--el-fill-color-light);
}

.attachment-name,
.lecture-preview strong {
  min-inline-size: 0;
  overflow-wrap: anywhere;
}

.attachment-actions {
  display: flex;
  flex: none;
  gap: 12px;
}

.attachment-actions :deep(.el-button + .el-button) {
  margin-inline-start: 0;
}

.lecture-preview {
  align-self: start;
  min-inline-size: 0;
  border: 1px solid var(--el-border-color);
  border-radius: 9px;
  background: var(--el-bg-color);
  scroll-margin-block-start: 84px;
}

.lecture-preview header {
  padding: 8px 12px;
}

.lecture-preview iframe {
  display: block;
  inline-size: 100%;
  block-size: clamp(240px, 52vh, 600px);
  border: 0;
  border-top: 1px solid var(--el-border-color-lighter);
  border-end-start-radius: 8px;
  border-end-end-radius: 8px;
}

@container classroom-resource (min-width: 650px) {
  .lecture-split {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .lecture-preview {
    position: sticky;
    top: 84px;
  }
}

.content-heading {
  margin-bottom: 18px;
}

.back-link {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 0;
  margin-bottom: 10px;
  background: none;
  border: 0;
  color: var(--workspace-accent);
  font-size: 13px;
  cursor: pointer;
}

.back-link:hover {
  color: #104d48;
}

.content-heading-main {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 16px;
}

.content-heading h1 {
  margin: 0;
  color: #173350;
  font-size: 26px;
  overflow-wrap: anywhere;
}

.content-heading-main p {
  margin: 6px 0 0;
  overflow-wrap: anywhere;
  color: #5a7188;
  font-size: 13px;
}

.meta-dot {
  margin: 0 4px;
  color: #64788a;
}

.content-heading-tags {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  padding-top: 3px;
}

.content-time {
  display: flex;
  align-items: baseline;
  gap: 6px;
  margin-top: 7px;
  color: #56697c;
  font-size: 12px;
}

.content-time .el-icon {
  flex: none;
}

.content-loading {
  padding: 28px;
}

.preview-note {
  display: flex;
  align-items: center;
  gap: 7px;
  padding: 10px 14px;
  margin-bottom: 14px;
  background: #edf4fd;
  border: 1px solid #cfe0f4;
  border-radius: 8px;
  color: #315d8d;
  font-size: 13px;
}

.study-progress {
  display: grid;
  grid-template-columns: minmax(160px, 0.9fr) minmax(180px, 2fr) minmax(120px, 0.7fr);
  align-items: center;
  gap: 18px;
  padding: 16px 20px;
  margin-bottom: 16px;
  background: linear-gradient(115deg, #fff 50%, #eef8f4);
}

.study-progress-copy span,
.study-progress-score span {
  display: block;
  color: #56697c;
  font-size: 12px;
}

.study-progress-copy strong,
.study-progress-score strong {
  color: #173b5c;
  font-size: 20px;
}

.study-progress-score {
  text-align: right;
}

.content-layout {
  display: grid;
  grid-template-columns: minmax(220px, 26%) minmax(0, 1fr);
  gap: 18px;
  align-items: start;
}

.content-outline {
  position: sticky;
  top: 84px;
  display: grid;
  grid-template-rows: auto minmax(0, 1fr);
  max-block-size: calc(100dvh - 104px);
  min-inline-size: 0;
}

.outline-navigation {
  overflow: auto;
  overscroll-behavior: contain;
  padding-bottom: 10px;
  scrollbar-width: thin;
}

.outline-toggle {
  display: none;
}

.outline-heading {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
  padding: 17px 17px 12px;
  border-bottom: 1px solid var(--surface-border);
  background: #f6f9fa;
}

.outline-heading h2 {
  display: flex;
  align-items: center;
  gap: 7px;
  margin: 0;
  color: #203f5c;
  font-size: 15px;
}

.outline-heading h2 .el-icon,
.outline-toggle > .el-icon:first-child {
  color: var(--workspace-accent);
}

.outline-heading span {
  color: #64788a;
  font-size: 11px;
}

.outline-chapter {
  padding: 12px 10px 0;
}

.outline-chapter + .outline-chapter {
  border-top: 1px solid #eaf0f3;
}

.outline-chapter h3 {
  margin: 6px 7px;
  overflow-wrap: anywhere;
  color: #29495d;
  font-size: 13px;
  font-weight: 650;
  letter-spacing: 0.02em;
}

.outline-section {
  margin-bottom: 7px;
}

.outline-section-title {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  padding: 5px 7px;
  color: #415c76;
  font-size: 12px;
}

.outline-section-title span {
  min-inline-size: 0;
  overflow-wrap: anywhere;
}

.outline-section-title small {
  color: #56697c;
  white-space: nowrap;
}

.outline-resource {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-block-size: 49px;
  padding: 7px 8px;
  background: none;
  border: 1px solid transparent;
  border-radius: 8px;
  color: #526a81;
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.outline-resource:hover {
  background: #f0f6f5;
}

.outline-resource.active {
  border-color: #d3e9e0;
  background: #e7f4ee;
  color: #145a50;
  font-weight: 600;
}

.outline-resource:focus-visible {
  outline-offset: -2px;
}

.outline-resource:disabled {
  cursor: default;
  opacity: 0.7;
}

.outline-resource-icon {
  display: grid;
  place-items: center;
  flex: none;
  width: 28px;
  height: 28px;
  border-radius: 7px;
  background: #edf2f5;
  color: #45667a;
  font-size: 15px;
}

.outline-resource.active .outline-resource-icon {
  background: #d7ede3;
  color: var(--workspace-accent);
}

.outline-resource-copy {
  display: grid;
  gap: 3px;
  flex: 1;
  min-inline-size: 0;
}

.outline-resource-name {
  overflow-wrap: anywhere;
}

.outline-resource small {
  color: #56697c;
  font-size: 11px;
  font-weight: 400;
  white-space: nowrap;
}

.done-icon {
  flex: none;
  color: var(--workspace-accent);
  font-size: 15px;
}

.outline-in-progress {
  flex: none;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: #a87324;
}

.outline-empty {
  padding: 0 8px;
  margin: 4px 0;
  color: #5d6f80;
  font-size: 11px;
}

.resource-view {
  container: classroom-resource / inline-size;
  padding: 22px 24px 18px;
  min-width: 0;
}

.resource-view-heading {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 14px;
  padding-bottom: 16px;
  border-bottom: 1px solid #edf2f7;
}

.resource-heading-text {
  min-inline-size: 0;
}

.resource-location {
  margin: 0 0 6px;
  color: var(--el-text-color-regular);
  font-size: 13px;
  overflow-wrap: anywhere;
}

.resource-kind-line {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 10px;
  color: #56697c;
  font-size: 12px;
}

.resource-kind-line span {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.resource-view-heading h2 {
  margin: 9px 0 0;
  color: #193a59;
  font-size: clamp(20px, 2.2vw, 26px);
  overflow-wrap: anywhere;
  scroll-margin-block-start: 84px;
}

.resource-view-status {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 7px;
  flex: none;
  color: var(--text-muted);
  font-size: 12px;
  white-space: nowrap;
}

.resource-body {
  margin-top: 20px;
}

.resource-body h3 {
  margin: 0 0 9px;
  color: #2b4a67;
  font-size: 14px;
}

.rich-text {
  max-inline-size: 72ch;
  color: #354b60;
  font-size: 15px;
  line-height: 1.8;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.muted-text {
  color: #56697c;
  font-size: 13px;
}

.topology-block {
  padding: 14px 16px;
  margin: 0;
  overflow: auto;
  background: #f6f9fc;
  border: 1px solid #e5edf5;
  border-radius: 8px;
  color: #3c566e;
  font-family: 'Cascadia Code', Consolas, monospace;
  font-size: 12px;
  line-height: 1.65;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.question-heading {
  display: flex;
  justify-content: space-between;
  align-items: baseline;
  gap: 10px;
  margin-bottom: 10px;
}

.question-heading span {
  color: #56697c;
  font-size: 12px;
}

.practice-result {
  margin-bottom: 12px;
}

.practice-summary {
  display: grid;
  grid-template-columns: repeat(6, minmax(0, 1fr));
  gap: 1px;
  overflow: hidden;
  margin: 0 0 12px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 8px;
  background: var(--el-border-color-lighter);
}

.practice-summary > div {
  grid-column: span 2;
  min-inline-size: 0;
  padding: 9px 12px;
  background: var(--el-fill-color-light);
}

.practice-summary .practice-time {
  grid-column: span 3;
}

.practice-summary dt {
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.practice-summary dd {
  margin: 4px 0 0;
  color: var(--el-text-color-primary);
  font-size: 14px;
  font-variant-numeric: tabular-nums;
  overflow-wrap: anywhere;
}

.question-card {
  padding: 16px;
  margin-bottom: 12px;
  background: #fbfcfe;
  border: 1px solid #e6edf5;
  border-radius: 9px;
}

.question-title {
  display: flex;
  gap: 10px;
  align-items: flex-start;
}

.question-number {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex: 0 0 24px;
  height: 24px;
  background: #e8f2fc;
  border-radius: 6px;
  color: #22669e;
  font-size: 12px;
  font-weight: 700;
}

.question-title strong {
  display: block;
  color: #2c455d;
  font-size: 14px;
  line-height: 1.6;
  font-weight: 600;
  overflow-wrap: anywhere;
}

.question-title small {
  display: block;
  margin-top: 3px;
  color: #56697c;
  font-size: 11px;
}

.option-list,
.answer-options {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 8px;
  padding-left: 34px;
  margin-top: 12px;
}

.option-line {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 7px 10px;
  background: #fff;
  border: 1px solid #edf1f6;
  border-radius: 6px;
  color: #4e657b;
  font-size: 13px;
}

.option-line > span:nth-child(2) {
  min-inline-size: 0;
  overflow-wrap: anywhere;
}

.option-line.correct {
  background: #eef9f3;
  border-color: #bfe5d1;
  color: #24714e;
}

.option-letter {
  color: #56697c;
  font-weight: 700;
}

.correct-label {
  margin-left: auto;
  color: #12735f;
  font-size: 11px;
  white-space: nowrap;
}

.analysis-text {
  margin: 7px 0 0;
  padding: 8px 10px;
  background: #f5f9fd;
  border-radius: 6px;
  color: #536b82;
  font-size: 12px;
  line-height: 1.65;
  white-space: pre-wrap;
}

.answer-option {
  display: flex;
  align-items: flex-start;
  height: auto;
  margin-right: 0;
  padding: 7px 10px;
  border: 1px solid #e9eff6;
  border-radius: 6px;
  background: #fff;
  white-space: normal;
}

.answer-option :deep(.el-radio__label),
.answer-option :deep(.el-checkbox__label) {
  min-inline-size: 0;
  padding-left: 8px;
  color: #4e657b;
  font-size: 13px;
  line-height: 1.5;
  white-space: normal;
  overflow-wrap: anywhere;
}

.question-feedback {
  display: grid;
  gap: 4px;
  padding: 9px 11px;
  margin-top: 10px;
  background: #fff6f5;
  border: 1px solid #f2d5d0;
  border-radius: 6px;
  color: #90584e;
  font-size: 12px;
  overflow-wrap: anywhere;
}

.question-feedback.ok {
  background: #eef9f3;
  border-color: #c3e7d4;
  color: #2d7453;
}

.question-feedback strong {
  font-size: 12px;
}

.question-feedback p {
  margin: 2px 0 0;
  line-height: 1.6;
}

.resource-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 12px;
  padding-top: 16px;
  margin-top: 20px;
  border-top: 1px solid #edf2f8;
}

.record-hint {
  color: #56697c;
  font-size: 12px;
}

.resource-pagination {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px;
  margin-top: 20px;
  padding-top: 14px;
  border-top: 1px solid var(--surface-border);
}

.resource-page-button {
  display: flex;
  align-items: center;
  gap: 9px;
  min-block-size: 60px;
  padding: 9px 12px;
  border: 1px solid var(--surface-border);
  border-radius: 8px;
  background: #f7fafb;
  color: #264558;
  text-align: left;
}

.resource-page-button.next-resource {
  grid-column: 2;
  justify-content: flex-end;
  text-align: right;
}

.resource-page-button:hover:not(:disabled) {
  background: #ecf7f1;
  border-color: #beddd1;
}

.resource-page-button:disabled {
  cursor: default;
  opacity: 0.7;
}

.resource-page-button .el-icon {
  flex: none;
  color: var(--workspace-accent);
}

.resource-page-button span {
  display: grid;
  gap: 4px;
  min-inline-size: 0;
}

.resource-page-button small {
  color: var(--text-muted);
  font-size: 11px;
}

.resource-page-button strong {
  display: -webkit-box;
  overflow: hidden;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
  overflow-wrap: anywhere;
  font-size: 13px;
}

.empty-hint {
  color: #56697c;
  font-size: 13px;
}

@media (max-width: 1050px) {
  .study-progress {
    grid-template-columns: minmax(0, 1fr) auto;
    gap: 8px 12px;
    padding: 12px 14px;
  }

  .study-progress :deep(.el-progress) {
    grid-area: 2 / 1 / 3 / -1;
  }

  .study-progress-score {
    grid-area: 1 / 2;
  }

  .content-layout {
    display: block;
  }

  .content-outline {
    top: 68px;
    z-index: 12;
    max-block-size: none;
    margin-bottom: 14px;
  }

  .outline-heading {
    display: none;
  }

  .outline-toggle {
    display: flex;
    align-items: center;
    gap: 9px;
    min-block-size: 48px;
    padding: 10px 15px;
    border: 0;
    border-radius: inherit;
    background: #fff;
    color: var(--el-text-color-primary);
    font-size: 14px;
    text-align: left;
  }

  .outline-toggle:focus-visible {
    outline-offset: -3px;
  }

  .outline-toggle .outline-position {
    margin-inline-start: auto;
    color: var(--el-text-color-regular);
    font-size: 12px;
  }

  .outline-toggle[aria-expanded='true'] .outline-chevron {
    transform: rotate(180deg);
  }

  .outline-navigation {
    display: none;
  }

  .outline-navigation.outline-open {
    display: block;
    max-block-size: min(52dvh, 24rem);
    border-top: 1px solid var(--el-border-color-lighter);
  }

  .resource-view-index {
    display: none;
  }

  .resource-view-heading h2,
  .lecture-preview {
    scroll-margin-block-start: 136px;
  }
}

@media (max-width: 800px) {
  .content-outline {
    top: 62px;
  }
}

@media (max-width: 640px) {
  .content-heading-main {
    flex-direction: column;
    gap: 8px;
  }

  .content-heading h1 {
    font-size: 23px;
  }

  .resource-view {
    padding: 16px 14px;
  }

  .resource-view-heading {
    flex-wrap: wrap;
    gap: 8px;
  }

  .lecture-files li {
    align-items: flex-start;
    flex-direction: column;
  }

  .practice-summary .practice-time {
    grid-column: 1 / -1;
  }

  .question-card {
    padding: 12px;
  }

  .option-list,
  .answer-options {
    padding-left: 0;
  }

  .answer-option {
    min-block-size: 38px;
  }

  .resource-actions {
    align-items: stretch;
    flex-direction: column;
    gap: 8px;
  }

  .resource-actions :deep(.el-button) {
    min-block-size: 42px;
    margin-left: 0;
  }

  .resource-pagination {
    gap: 8px;
  }

  .resource-page-button {
    gap: 6px;
    padding-inline: 8px;
  }

  .resource-page-button:only-child {
    grid-column: 1 / -1;
  }
}
</style>

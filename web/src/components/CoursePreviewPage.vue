<script setup lang="ts">
import { computed, defineAsyncComponent, reactive, ref, watch } from 'vue'
import { ArrowLeft, Download, Monitor, Refresh, VideoPlay } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { api, dateTime, downloadFile, messageOf, post } from '../api'
import { selectedText } from '../exam'
import { courseStatusOptions, courseTypeOptions, durationText, labelOf, resourceKindOptions } from '../teaching'
import type {
  CoursePreviewEnvironment,
  CoursePreviewPracticeResponse,
  CoursePreviewResource,
  CoursePreviewResponse
} from '../types'
import TableEmpty from './TableEmpty.vue'
import VmConsoleDialog from './VmConsoleDialog.vue'
import CaseTopologyView from './CaseTopologyView.vue'

const TerminalDialog = defineAsyncComponent(() => import('./TerminalDialog.vue'))
const DeviceWebConsole = defineAsyncComponent(() => import('./DeviceWebConsole.vue'))

const props = defineProps<{ courseId: string; backKey: string }>()
const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()

const data = ref<CoursePreviewResponse | null>(null)
const loading = ref(false)
const error = ref('')
const activeId = ref('')

const practiceAnswers = reactive<Record<string, number[]>>({})
const practiceSubmitting = ref(false)
const practiceError = ref('')
const practiceResult = ref<CoursePreviewPracticeResponse | null>(null)

const environment = ref<CoursePreviewEnvironment | null>(null)
const environmentLoading = ref(false)
const environmentError = ref('')
const environmentResourceId = ref('')
const terminalDevice = ref<Record<string, unknown> | null>(null)
const webDevice = ref<{ id: string; name: string } | null>(null)
const machine = ref<{ id: string; name: string } | null>(null)
const machineVisible = ref(false)

function connectMachine(row: { id: string; name: string }) {
  machine.value = row
  machineVisible.value = true
}

const resources = computed(
  () => data.value?.tree.flatMap(chapter => chapter.sections.flatMap(section => section.resources)) || []
)
const active = computed(() => resources.value.find(resource => resource.id === activeId.value) || null)
const isPractice = computed(() => active.value?.kind === 'theoryPractice')
const isCasePractice = computed(() => active.value?.kind === 'casePractice')
const showAnswers = computed(() => !!data.value?.showAnswers)
const resultByQuestion = computed(() => {
  const map = new Map<string, CoursePreviewPracticeResponse['questions'][number]>()
  for (const row of practiceResult.value?.questions || []) map.set(row.questionId, row)
  return map
})
const answeredCount = computed(
  () => (active.value?.questions || []).filter(question => (practiceAnswers[question.id] || []).length > 0).length
)

async function load() {
  if (!props.courseId) return
  loading.value = true
  error.value = ''
  try {
    data.value = await api<CoursePreviewResponse>(`/courses/${props.courseId}/preview`)
    if (!resources.value.some(resource => resource.id === activeId.value)) {
      activeId.value = resources.value[0]?.id || ''
    } else {
      openResource(activeId.value)
    }
  } catch (failure) {
    data.value = null
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

watch(
  () => props.courseId,
  () => {
    activeId.value = ''
    void load()
  },
  { immediate: true }
)

watch(active, resource => {
  practiceResult.value = null
  practiceError.value = ''
  for (const key of Object.keys(practiceAnswers)) delete practiceAnswers[key]
  if (!resource) return
  for (const question of resource.questions) practiceAnswers[question.id] = []
  if (resource.kind === 'casePractice') void loadEnvironment(resource)
})

function openResource(id: string) {
  activeId.value = id
}

async function download(file: { id: string; name: string }) {
  try {
    await downloadFile(`/course-files/${file.id}/download`, file.name)
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  }
}

function selection(questionId: string): number[] {
  return practiceAnswers[questionId] || []
}

function setSingle(questionId: string, value: string | number | boolean | undefined) {
  const index = Number(value)
  practiceAnswers[questionId] = Number.isInteger(index) && index >= 0 ? [index] : []
}

function optionLetter(index: number) {
  return ['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J'][index] || String(index + 1)
}

async function submitPractice() {
  if (!active.value || !props.courseId) return
  practiceSubmitting.value = true
  practiceError.value = ''
  try {
    practiceResult.value = await post<CoursePreviewPracticeResponse>(
      `/courses/${props.courseId}/resources/${active.value.id}/preview-practice`,
      {
        answers: active.value.questions.map(question => ({
          questionId: question.id,
          selected: practiceAnswers[question.id] || []
        }))
      }
    )
    ElMessage.success('已自动评分，本次结果不会计入学习记录')
  } catch (failure) {
    practiceError.value = messageOf(failure)
  } finally {
    practiceSubmitting.value = false
  }
}

async function loadEnvironment(resource: CoursePreviewResource) {
  if (!props.courseId) return
  if (environmentResourceId.value === resource.id && environment.value) return
  environmentLoading.value = true
  environmentError.value = ''
  environment.value = null
  try {
    environment.value = await api<CoursePreviewEnvironment>(
      `/courses/${props.courseId}/resources/${resource.id}/environment`
    )
    environmentResourceId.value = resource.id
  } catch (failure) {
    environmentError.value = messageOf(failure)
  } finally {
    environmentLoading.value = false
  }
}

function refreshEnvironment() {
  if (active.value?.kind === 'casePractice') {
    environmentResourceId.value = ''
    void loadEnvironment(active.value)
  }
}

function openTerminal(device: { id: string; name: string }) {
  terminalDevice.value = { ...device, connectionType: 'simulated', isSimulated: true }
}

function machineStatus(status: string) {
  return status === 'running' ? '运行中' : status === 'stopped' ? '已停止' : status || '未知'
}

function previewMachineReason(row: { canConnect: boolean; status?: string }): string | undefined {
  if (row.canConnect) return undefined
  return row.status && row.status !== 'running' ? '虚拟机未运行，无法连接' : '当前无法连接'
}

function answerText(resource: CoursePreviewResource, questionId: string) {
  const question = resource.questions.find(item => item.id === questionId)
  return question ? selectedText({ options: question.options, answers: practiceAnswers[questionId] }) : '未作答'
}

function correctAnswerText(resource: CoursePreviewResource, questionId: string) {
  const question = resource.questions.find(item => item.id === questionId)
  const correct = resultByQuestion.value.get(questionId)?.correctAnswers || question?.answers || []
  return question ? selectedText({ options: question.options, answers: correct }) : '—'
}

function resultTagType(questionId: string): 'success' | 'danger' {
  return resultByQuestion.value.get(questionId)?.correct ? 'success' : 'danger'
}
</script>

<template>
  <section class="page-heading preview-heading">
    <div>
      <h1>{{ data?.course.name || '课程预览' }}</h1>
      <p>这是课程预览，不是正式学习。练习提交与实操连接仅供体验，不计入学习成绩或进度；正式学习请到「我的课堂」。</p>
    </div>
    <div class="heading-actions">
      <el-button :icon="ArrowLeft" @click="emit('navigate', props.backKey)">返回</el-button>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
    </div>
  </section>

  <section v-if="loading && !data" class="surface preview-state">
    <el-skeleton :rows="8" animated />
  </section>
  <section v-else-if="error" class="surface preview-state">
    <el-result icon="error" title="课程预览加载失败" :sub-title="error">
      <template #extra><el-button type="primary" @click="load">重试</el-button></template>
    </el-result>
  </section>
  <template v-else-if="data">
    <el-alert type="info" show-icon :closable="false" class="preview-notice">
      <template #title>
        预览模式 · {{ labelOf(courseTypeOptions, data.course.type) }} ·
        {{ labelOf(courseStatusOptions, data.course.status) }}
        · 练习与实操不计入学习记录
        <span v-if="!showAnswers"> · 提交后可查看本次得分与解析</span>
      </template>
    </el-alert>
    <div class="preview-layout">
      <aside class="surface outline-panel">
        <div class="outline-heading">
          <span>课程目录</span>
          <small>{{ resources.length }} 个资源</small>
        </div>
        <el-empty v-if="!data.tree.length" description="课程暂无章节" :image-size="70">
          <p class="empty-hint">
            {{
              backKey === 'courses'
                ? '此课程尚未配置目录。这是预览，不会记学习成绩；正式学习请到课堂。'
                : '此课程尚未配置目录。请联系管理员修改课程。这是预览，不会记学习成绩；正式学习请到课堂。'
            }}
          </p>
          <el-button v-if="backKey === 'courses'" type="primary" plain @click="emit('navigate', 'courses')">
            返回课程管理配置目录
          </el-button>
        </el-empty>
        <div v-for="(chapter, chapterIndex) in data.tree" :key="chapter.id" class="outline-chapter">
          <h3>第 {{ chapterIndex + 1 }} 章 · {{ chapter.name }}</h3>
          <div v-for="section in chapter.sections" :key="section.id" class="outline-section">
            <div class="outline-section-title">
              <span>{{ section.name }}</span>
              <small>{{ durationText(section.durationMinutes) }}</small>
            </div>
            <button
              v-for="resource in section.resources"
              :key="resource.id"
              type="button"
              class="outline-resource"
              :class="{ active: resource.id === activeId }"
              @click="openResource(resource.id)"
            >
              <el-tag size="small" effect="plain" :type="resource.kind === 'casePractice' ? 'warning' : 'info'">
                {{ labelOf(resourceKindOptions, resource.kind) }}
              </el-tag>
              <span>{{ resource.name }}</span>
            </button>
            <p v-if="!section.resources.length" class="outline-empty">本节尚未添加资源</p>
          </div>
        </div>
      </aside>

      <article class="surface preview-content">
        <el-empty v-if="!data.tree.length" description="课程暂无章节" :image-size="80">
          <p class="empty-hint">
            {{
              backKey === 'courses'
                ? '配置课程目录后，可在这里预览讲义、练习和实操环境。预览不会记学习成绩。'
                : '请联系管理员修改课程、配置目录后，可在这里预览。预览不会记学习成绩。'
            }}
          </p>
          <el-button v-if="backKey === 'courses'" type="primary" plain @click="emit('navigate', 'courses')">
            返回课程管理配置目录
          </el-button>
        </el-empty>
        <el-empty v-else-if="!active" description="请选择左侧资源开始预览">
          <p class="empty-hint">从左侧目录选择一个资源后，可预览讲义、练习和实操环境。</p>
        </el-empty>
        <template v-else>
          <header class="content-head">
            <div>
              <div class="content-tags">
                <el-tag size="small" effect="plain">{{ labelOf(resourceKindOptions, active.kind) }}</el-tag>
                <span>{{ durationText(active.durationMinutes) }}</span>
                <span v-if="active.questionCount">{{ active.questionCount }} 题 · 满分 {{ active.maxScore }}</span>
                <span v-if="active.startupNodeId">启动节点：{{ active.startupNodeName || active.startupNodeId }}</span>
              </div>
              <h2>{{ active.name }}</h2>
            </div>
          </header>

          <div v-if="active.content" class="content-body">{{ active.content }}</div>
          <p v-else class="content-empty">该资源暂未填写正文。</p>

          <section v-if="active.files.length" class="content-block">
            <h3>教学附件</h3>
            <ul class="file-list">
              <li v-for="file in active.files" :key="file.id">
                <el-icon><Download /></el-icon>
                <span>{{ file.name }}</span>
                <small>{{ dateTime(file.createdAt) }}</small>
                <el-button link type="primary" @click="download(file)">下载</el-button>
              </li>
            </ul>
          </section>

          <template v-if="active.topology">
            <section class="content-block">
              <h3>拓扑说明</h3>
              <p class="content-body topology">{{ active.topology }}</p>
            </section>
          </template>

          <section v-if="isPractice" class="content-block">
            <div class="block-heading">
              <div>
                <h3>理论练习</h3>
                <p>预览提交后由服务端即时评分，不计入学习记录。</p>
              </div>
              <span class="record-count">已作答 {{ answeredCount }} / {{ active.questions.length }}</span>
            </div>
            <el-alert v-if="practiceError" :title="practiceError" type="error" show-icon :closable="false" />
            <div v-for="(question, index) in active.questions" :key="question.id" class="practice-question">
              <div class="question-title">
                <strong>第 {{ index + 1 }} 题</strong>
                <el-tag size="small" effect="plain">{{ question.score }} 分</el-tag>
                <el-tag v-if="practiceResult" size="small" :type="resultTagType(question.id)">
                  {{ resultByQuestion.get(question.id)?.correct ? '正确' : '错误' }}
                </el-tag>
              </div>
              <p class="question-stem">{{ question.stem }}</p>
              <el-radio-group
                v-if="question.type !== 'multiple'"
                :model-value="selection(question.id)[0]"
                class="option-group"
                @change="(value: string | number | boolean | undefined) => setSingle(question.id, value)"
              >
                <el-radio v-for="(option, optionIndex) in question.options" :key="optionIndex" :value="optionIndex">
                  {{ optionLetter(optionIndex) }}. {{ option }}
                </el-radio>
              </el-radio-group>
              <el-checkbox-group v-else v-model="practiceAnswers[question.id]" class="option-group">
                <el-checkbox v-for="(option, optionIndex) in question.options" :key="optionIndex" :value="optionIndex">
                  {{ optionLetter(optionIndex) }}. {{ option }}
                </el-checkbox>
              </el-checkbox-group>
              <p v-if="showAnswers" class="answer-line">
                正确答案：{{ correctAnswerText(active, question.id) }}
                <span v-if="question.analysis"> · {{ question.analysis }}</span>
              </p>
              <p v-else-if="practiceResult" class="answer-line">
                正确答案：{{ correctAnswerText(active, question.id) }} · 你的选择：{{ answerText(active, question.id) }}
                <span v-if="question.analysis"> · {{ question.analysis }}</span>
              </p>
            </div>
            <div class="practice-actions">
              <el-button type="primary" :icon="VideoPlay" :loading="practiceSubmitting" @click="submitPractice">
                提交并自动评分
              </el-button>
              <el-tag v-if="practiceResult" :type="practiceResult.passed ? 'success' : 'warning'" size="large">
                本次得分 {{ practiceResult.score }} / {{ practiceResult.maxScore }} ·
                {{ practiceResult.passed ? '达到预览合格线（得分 60%）' : '未达到预览合格线（得分 60%）' }}
              </el-tag>
            </div>
          </section>

          <section v-if="isCasePractice" class="content-block">
            <div class="block-heading">
              <div>
                <h3>实操环境</h3>
                <p>模拟设备与虚拟机由平台分配，实际学员需使用个人账号已分配的节点资源。</p>
              </div>
              <el-button :icon="Refresh" :loading="environmentLoading" @click="refreshEnvironment">刷新环境</el-button>
            </div>
            <el-alert v-if="environmentError" :title="environmentError" type="error" show-icon :closable="false" />
            <div v-else-if="environmentLoading" class="tree-loading"><el-skeleton :rows="3" animated /></div>
            <template v-else-if="environment">
              <div class="environment-node">
                <span>实训节点</span>
                <strong>{{ environment.node?.name || '未配置启动节点' }}</strong>
                <el-tag v-if="environment.node" :type="environment.node.enabled ? 'success' : 'info'" size="small">
                  {{ environment.node.enabled ? '已启用' : '已停用' }}
                </el-tag>
              </div>
              <CaseTopologyView
                v-if="environment.resource.topologyBlueprint"
                :topology="environment.resource.topologyBlueprint"
                :devices="environment.topologyDevices"
              />
              <el-table :data="environment.devices" row-key="id" empty-text="">
                <template #empty>
                  <TableEmpty
                    title="当前节点暂无可用模拟设备"
                    hint="预览环境由平台分配。节点关联模拟设备后，可在这里打开终端或 Web 管理。"
                  />
                </template>
                <el-table-column prop="name" label="设备" min-width="170" show-overflow-tooltip />
                <el-table-column prop="cabinetName" label="机柜" min-width="120" show-overflow-tooltip />
                <el-table-column label="操作" width="220">
                  <template #default="{ row }">
                    <el-button
                      link
                      type="primary"
                      :icon="Monitor"
                      @click="openTerminal(row as { id: string; name: string })"
                    >
                      打开终端
                    </el-button>
                    <el-button link type="primary" @click="webDevice = row as { id: string; name: string }"
                      >Web 管理</el-button
                    >
                  </template>
                </el-table-column>
              </el-table>
              <el-table :data="environment.virtualMachines" row-key="id" empty-text="">
                <template #empty>
                  <TableEmpty
                    title="当前节点暂无虚拟机"
                    hint="预览环境由平台分配。节点配置虚拟机后，可在这里查看状态并连接。"
                  />
                </template>
                <el-table-column prop="name" label="虚拟机" min-width="160" show-overflow-tooltip />
                <el-table-column label="状态" width="105">
                  <template #default="{ row }">
                    <el-tag :type="row.status === 'running' ? 'success' : 'info'" size="small">
                      {{ machineStatus(row.status) }}
                    </el-tag>
                  </template>
                </el-table-column>
                <el-table-column label="配置" min-width="180">
                  <template #default="{ row }"
                    >{{ row.cpuCount }} 核 · {{ row.memoryMb }} MB · {{ row.diskGb }} GB</template
                  >
                </el-table-column>
                <el-table-column label="操作" width="92">
                  <template #default="{ row }">
                    <el-tooltip
                      :disabled="!previewMachineReason(row as { canConnect: boolean; status?: string })"
                      :content="previewMachineReason(row as { canConnect: boolean; status?: string })"
                      placement="top"
                    >
                      <span class="disabled-action-wrap">
                        <el-button
                          link
                          type="primary"
                          :disabled="!row.canConnect"
                          @click="connectMachine(row as { id: string; name: string })"
                        >
                          连接
                        </el-button>
                      </span>
                    </el-tooltip>
                  </template>
                </el-table-column>
              </el-table>
            </template>
          </section>
        </template>
      </article>
    </div>
  </template>

  <TerminalDialog
    v-if="terminalDevice"
    :key="String(terminalDevice.id)"
    :device="terminalDevice"
    @close="terminalDevice = null"
  />
  <DeviceWebConsole v-if="webDevice" :device="webDevice" @close="webDevice = null" />
  <VmConsoleDialog
    v-if="machine"
    v-model="machineVisible"
    scope="machine"
    :target-id="machine.id"
    :machine-name="machine.name"
  />
</template>

<style scoped>
.preview-heading {
  align-items: flex-start;
}

.preview-notice {
  margin-bottom: 16px;
}

.preview-state {
  padding: 24px;
}

.preview-layout {
  display: grid;
  grid-template-columns: minmax(260px, 320px) minmax(0, 1fr);
  align-items: start;
  gap: 18px;
}

.outline-panel {
  position: sticky;
  top: 12px;
  max-height: calc(100vh - 140px);
  overflow: auto;
  padding: 14px;
}

.outline-heading {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
  padding-bottom: 10px;
  border-bottom: 1px solid #dce7eb;
  color: #1c334e;
  font-weight: 600;
}

.outline-heading small {
  color: #56697c;
  font-size: 11px;
  font-weight: 400;
}

.outline-chapter {
  margin-top: 14px;
}

.outline-chapter h3 {
  margin: 0 0 8px;
  color: #2a4a67;
  font-size: 13px;
}

.outline-section {
  padding-left: 8px;
  margin-bottom: 10px;
  border-left: 2px solid #dce7eb;
}

.outline-section-title {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
  margin-bottom: 6px;
  color: #52697f;
  font-size: 12px;
}

.outline-section-title small {
  color: #56697c;
  font-size: 10px;
}

.outline-resource {
  display: flex;
  align-items: center;
  width: 100%;
  gap: 7px;
  padding: 6px 7px;
  margin-bottom: 3px;
  color: #3d566e;
  font-size: 12px;
  text-align: left;
  background: transparent;
  border: 1px solid transparent;
  border-radius: 6px;
  cursor: pointer;
}

.outline-resource span {
  flex: 1;
  min-width: 0;
  overflow-wrap: anywhere;
}

.outline-resource:hover {
  background: #eef6f4;
}

.outline-resource.active {
  color: #145a50;
  background: #e3f3ee;
  border-color: #cfe6df;
}

.outline-empty {
  margin: 2px 0 0 4px;
  color: #5d6f80;
  font-size: 11px;
}

.empty-hint {
  max-width: 36ch;
  margin: 8px auto 12px;
  color: #56697c;
  font-size: 12px;
  line-height: 1.5;
  text-align: center;
}

.preview-content {
  min-width: 0;
  padding: 22px;
}

.content-head {
  display: flex;
  justify-content: space-between;
  gap: 16px;
  padding-bottom: 14px;
  border-bottom: 1px solid #dce7eb;
}

.content-tags {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  color: #56697c;
  font-size: 12px;
}

.content-head h2 {
  margin: 9px 0 0;
  color: #1c334e;
  font-size: 21px;
  overflow-wrap: anywhere;
}

.content-body {
  margin: 16px 0 0;
  color: #334e67;
  font-size: 14px;
  line-height: 1.85;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.content-body.topology {
  padding: 10px 12px;
  background: #f5f9f8;
  border: 1px solid #e4eeea;
  border-radius: 8px;
  font-size: 13px;
}

.content-empty {
  margin: 16px 0 0;
  color: #56697c;
  font-size: 13px;
}

.content-block {
  margin-top: 22px;
  padding-top: 16px;
  border-top: 1px solid #dce7eb;
}

.content-block h3 {
  margin: 0 0 10px;
  color: #1c334e;
  font-size: 16px;
}

.file-list {
  padding: 0;
  margin: 0;
  list-style: none;
  display: grid;
  gap: 6px;
}

.file-list li {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  padding: 8px 10px;
  background: #f5f9f8;
  border: 1px solid #e4eeea;
  border-radius: 7px;
  color: #3e566f;
  font-size: 12px;
}

.file-list li span {
  flex: 1;
  min-width: 120px;
  overflow-wrap: anywhere;
}

.file-list small {
  color: #56697c;
  font-size: 10px;
}

.block-heading {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 10px;
  margin-bottom: 12px;
}

.block-heading p {
  margin: 3px 0 0;
  color: #56697c;
  font-size: 12px;
}

.practice-question {
  padding: 14px;
  margin-bottom: 12px;
  background: #f6faf8;
  border: 1px solid #dce7eb;
  border-radius: 9px;
}

.question-title {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  color: #274763;
}

.question-stem {
  margin: 10px 0;
  color: #334e67;
  font-size: 14px;
  line-height: 1.65;
  white-space: pre-wrap;
}

.option-group {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 4px;
}

.option-group :deep(.el-radio),
.option-group :deep(.el-checkbox) {
  height: auto;
  margin: 0;
  white-space: normal;
}

.option-group :deep(.el-radio__label),
.option-group :deep(.el-checkbox__label) {
  white-space: normal;
  line-height: 1.5;
}

.answer-line {
  margin: 9px 0 0;
  color: #2f6d52;
  font-size: 12px;
  line-height: 1.6;
}

.practice-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 12px;
  margin-top: 14px;
}

.environment-node {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 9px;
  padding: 9px 12px;
  margin-bottom: 10px;
  color: #56697c;
  font-size: 12px;
  background: #f5f9f8;
  border: 1px solid #e4eeea;
  border-radius: 7px;
}

.environment-node strong {
  color: #2a4a67;
  font-size: 13px;
}

.tree-loading {
  padding: 10px 0;
}

@media (max-width: 900px) {
  .preview-layout {
    grid-template-columns: 1fr;
  }

  .outline-panel {
    position: static;
    max-height: 340px;
  }
}
</style>

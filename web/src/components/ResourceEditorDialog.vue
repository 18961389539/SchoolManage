<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { Delete, Plus, UploadFilled } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { api, listItems, messageOf, post, put } from '../api'
import { durationText, questionTypeOptions, resourceKindOptions } from '../teaching'
import type { CourseQuestion, CourseResourceDetail, NodeRow, QuestionType, ResourceKind } from '../types'
import QuestionImportDialog from './QuestionImportDialog.vue'
import CaseRubricEditor from './CaseRubricEditor.vue'
import CaseTopologyEditor from './CaseTopologyEditor.vue'
import type { CaseRubricItem, CaseTopologyBlueprint } from '../caseWork'
import { focusFirstField } from '../focusField'

const visible = defineModel<boolean>({ required: true })
const props = defineProps<{ sectionId: string; resourceId?: string }>()
const emit = defineEmits<{ saved: [] }>()

interface EditableQuestion {
  type: QuestionType
  stem: string
  options: string[]
  answers: number[]
  score: number
  analysis: string
}

const letters = 'ABCDEFGHIJ'
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const nodes = ref<NodeRow[]>([])
const nodesLoaded = ref(false)
const form = reactive({
  name: '',
  kind: 'theory' as ResourceKind,
  durationMinutes: 15,
  content: '',
  topology: '',
  topologyBlueprint: null as CaseTopologyBlueprint | null,
  startupNodeId: '' as string | null,
  questions: [] as EditableQuestion[],
  caseRubric: [] as CaseRubricItem[]
})
const importVisible = ref(false)
const selectedQuestions = ref<EditableQuestion[]>([])
const topologyId = ref('')
const topologies = ref<{ id: string; name: string; topology: string }[]>([])
const environmentDevices = ref<{ id: string; name: string; interfaces: string[] }[]>([])
const environmentError = ref('')
let environmentRequest = 0

const title = computed(() => (props.resourceId ? '编辑资源' : '新增资源'))
const resourceForm = ref<{ $el?: HTMLElement }>()
function focusResourceForm() {
  if (loading.value) return
  focusFirstField(resourceForm.value?.$el)
}
const isPractice = computed(() => form.kind === 'theoryPractice')
const isCasePractice = computed(() => form.kind === 'casePractice')
const needsContent = computed(() => form.kind === 'theory' || form.kind === 'case')
const enabledNodes = computed(() => nodes.value.filter(node => node.enabled !== false))

watch([visible, isCasePractice, () => form.startupNodeId], async ([open, practical, nodeId]) => {
  const request = ++environmentRequest
  topologyId.value = ''
  topologies.value = []
  environmentDevices.value = []
  environmentError.value = ''
  if (!open || !practical) return
  try {
    const [saved, devices] = await Promise.all([
      api<typeof topologies.value>(`/course-topologies${nodeId ? `?nodeId=${encodeURIComponent(nodeId)}` : ''}`),
      nodeId ? api<typeof environmentDevices.value>(`/nodes/${nodeId}/case-devices`) : Promise.resolve([])
    ])
    if (request !== environmentRequest) return
    topologies.value = saved.filter(item => item.id !== props.resourceId)
    environmentDevices.value = devices
  } catch (failure) {
    if (request === environmentRequest) environmentError.value = messageOf(failure)
  }
})

function applyTopology() {
  const saved = topologies.value.find(item => item.id === topologyId.value)
  if (saved) form.topology = saved.topology
}

function newQuestion(): EditableQuestion {
  return {
    type: 'single',
    stem: '',
    options: ['选项 A', '选项 B', '选项 C', '选项 D'],
    answers: [0],
    score: 10,
    analysis: ''
  }
}

function resetForm() {
  selectedQuestions.value = []
  form.name = ''
  form.kind = 'theory'
  form.durationMinutes = 15
  form.content = ''
  form.topology = ''
  form.topologyBlueprint = null
  form.startupNodeId = null
  form.questions = []
  form.caseRubric = []
}

function fromQuestion(question: CourseQuestion): EditableQuestion {
  return {
    type: question.type,
    stem: question.stem,
    options: question.options.length ? [...question.options] : ['选项 A', '选项 B'],
    answers: question.answers?.length ? [...question.answers] : [0],
    score: question.score,
    analysis: question.analysis || ''
  }
}

async function loadNodes() {
  if (nodesLoaded.value) return
  try {
    const data = await api<{ items?: NodeRow[] }>('/nodes?page=1&pageSize=1000')
    nodes.value = listItems<NodeRow>(data)
    nodesLoaded.value = true
  } catch {
    nodes.value = []
  }
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    void loadNodes()
    if (!props.resourceId) {
      resetForm()
      return
    }
    const detail = await api<CourseResourceDetail>(`/resources/${props.resourceId}`)
    form.name = detail.name
    form.kind = detail.kind
    form.durationMinutes = detail.durationMinutes
    form.content = detail.content
    form.topology = detail.topology
    form.topologyBlueprint = detail.topologyBlueprint
    form.startupNodeId = detail.startupNodeId || null
    form.questions = detail.questions.map(fromQuestion)
    form.caseRubric = detail.caseRubric
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    loading.value = false
    if (visible.value) focusResourceForm()
  }
}

watch(
  () => [visible.value, props.resourceId, props.sectionId],
  () => {
    if (visible.value) void load()
  },
  { immediate: true }
)

function setKind(value: string) {
  const kind = value as ResourceKind
  form.kind = kind
  if (kind === 'theoryPractice') {
    if (!form.questions.length) form.questions.push(newQuestion())
  } else {
    form.questions = []
  }
}

function setQuestionType(question: EditableQuestion, value: string) {
  const type = value as QuestionType
  question.type = type
  if (type === 'judge') {
    question.options = ['正确', '错误']
    question.answers = [0]
    return
  }
  if (question.options.length < 2) question.options = ['选项 A', '选项 B']
  if (type !== 'multiple') question.answers = question.answers.slice(0, 1)
  question.answers = question.answers.filter(index => index < question.options.length)
  if (!question.answers.length) question.answers = [0]
}

function addQuestion() {
  if (form.questions.length >= 100) {
    ElMessage.warning('单个练习最多 100 道题目')
    return
  }
  form.questions.push(newQuestion())
}

function onQuestionsImported(rows: Omit<CourseQuestion, 'id' | 'sortOrder'>[]) {
  const room = 100 - form.questions.length
  if (room <= 0) {
    ElMessage.warning('当前题目已达 100 道上限，请先删除部分题目')
    return
  }
  const accepted = rows.slice(0, room).map(row => ({
    type: row.type,
    stem: row.stem,
    options: row.options.length ? [...row.options] : ['选项 A', '选项 B'],
    answers: row.answers?.length ? [...row.answers] : [0],
    score: row.score,
    analysis: row.analysis || ''
  }))
  form.questions.push(...accepted)
  if (rows.length > accepted.length)
    ElMessage.warning(`已导入 ${accepted.length} 道题目，超出 100 道上限的 ${rows.length - accepted.length} 道未加入`)
  else ElMessage.success(`已追加 ${accepted.length} 道题目，保存资源后生效`)
}

function removeQuestion(index: number) {
  selectedQuestions.value = selectedQuestions.value.filter(question => question !== form.questions[index])
  form.questions.splice(index, 1)
}

function removeSelectedQuestions() {
  const selected = new Set(selectedQuestions.value)
  form.questions = form.questions.filter(question => !selected.has(question))
  selectedQuestions.value = []
}

function toggleQuestion(question: EditableQuestion, selected: boolean) {
  if (selected) selectedQuestions.value.push(question)
  else selectedQuestions.value = selectedQuestions.value.filter(item => item !== question)
}

function addOption(question: EditableQuestion) {
  if (question.options.length >= 10) return
  question.options.push('')
}

function removeOption(question: EditableQuestion, index: number) {
  if (question.options.length <= 2) return
  question.options.splice(index, 1)
  question.answers = question.answers
    .filter(answer => answer !== index)
    .map(answer => (answer > index ? answer - 1 : answer))
  if (!question.answers.length) question.answers = [0]
}

function setSingleAnswer(question: EditableQuestion, value: string | number | boolean | undefined) {
  const index = Number(value)
  if (Number.isInteger(index) && index >= 0) question.answers = [index]
}

function optionLetter(index: number) {
  return letters[index] || String(index + 1)
}

function answerText(question: EditableQuestion, index: number) {
  return `${optionLetter(index)}. ${question.options[index]?.trim() || '（空选项）'}`
}

function validateQuestion(question: EditableQuestion, index: number): string {
  const number = index + 1
  if (!question.stem.trim()) return `第 ${number} 题题干不能为空`
  if (question.stem.trim().length > 500) return `第 ${number} 题题干不能超过 500 个字符`
  if (question.options.some(option => !option.trim())) return `第 ${number} 题存在空选项`
  if (!question.answers.length) return `第 ${number} 题请设置正确答案`
  if (question.type !== 'multiple' && question.answers.length !== 1) return `第 ${number} 题只能设置一个正确答案`
  if (question.answers.some(answer => answer < 0 || answer >= question.options.length))
    return `第 ${number} 题正确答案超出选项范围`
  if (!Number.isInteger(question.score) || question.score < 1 || question.score > 100)
    return `第 ${number} 题分值应在 1–100 之间`
  return ''
}

function validate(): string {
  if (!form.name.trim()) return '请填写资源名称'
  if (form.name.trim().length > 100) return '资源名称不能超过 100 个字符'
  if (!Number.isInteger(form.durationMinutes) || form.durationMinutes < 0 || form.durationMinutes > 600)
    return '资源时长应在 0–600 分钟之间'
  if (needsContent.value && !form.content.trim()) return '理论知识和案例教学资源需要填写正文'
  if (!isPractice.value) return ''
  if (!form.questions.length) return '理论练习至少需要 1 道题目'
  for (const [index, question] of form.questions.entries()) {
    const issue = validateQuestion(question, index)
    if (issue) return issue
  }
  return ''
}

async function save() {
  const issue = validate()
  if (issue) {
    error.value = issue
    return
  }
  saving.value = true
  error.value = ''
  try {
    const payload = {
      name: form.name.trim(),
      kind: form.kind,
      durationMinutes: form.durationMinutes,
      content: form.content.trim(),
      topology: isCasePractice.value ? form.topology.trim() : '',
      startupNodeId: isCasePractice.value && form.startupNodeId ? form.startupNodeId : null,
      caseRubric: isCasePractice.value ? form.caseRubric : [],
      topologyBlueprint: isCasePractice.value ? form.topologyBlueprint : null,
      questions: isPractice.value
        ? form.questions.map(question => ({
            type: question.type,
            stem: question.stem.trim(),
            options: question.options.map(option => option.trim()),
            answers: [...question.answers].sort((left, right) => left - right),
            score: question.score,
            analysis: question.analysis.trim()
          }))
        : []
    }
    if (props.resourceId) await put(`/resources/${props.resourceId}`, payload)
    else await post(`/sections/${props.sectionId}/resources`, payload)
    ElMessage.success(props.resourceId ? '资源已更新' : '资源已创建')
    emit('saved')
    visible.value = false
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <el-dialog v-model="visible" :title="title" width="min(920px, 96vw)" destroy-on-close @opened="focusResourceForm">
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
    <div v-if="loading" class="resource-skeleton" aria-live="polite">
      <el-skeleton :rows="6" animated />
    </div>
    <el-form v-else ref="resourceForm" label-position="top" class="entity-form resource-form">
      <div class="resource-form-grid">
        <el-form-item label="资源名称" required>
          <el-input v-model="form.name" maxlength="100" show-word-limit placeholder="例如：OSI 七层模型" />
        </el-form-item>
        <el-form-item label="资源类型" required>
          <el-select :model-value="form.kind" @change="setKind">
            <el-option
              v-for="option in resourceKindOptions"
              :key="option.value"
              :label="option.label"
              :value="option.value"
            />
          </el-select>
        </el-form-item>
        <el-form-item label="预计时长（分钟）">
          <el-input-number v-model="form.durationMinutes" :min="0" :max="600" controls-position="right" />
          <span class="field-hint">{{ durationText(form.durationMinutes) }}</span>
        </el-form-item>
      </div>

      <el-form-item label="正文 / 教学说明" :required="needsContent">
        <el-input
          v-model="form.content"
          type="textarea"
          :rows="6"
          maxlength="20000"
          show-word-limit
          placeholder="填写知识点、案例背景或实操步骤"
        />
      </el-form-item>

      <template v-if="isCasePractice">
        <el-form-item label="选择已有拓扑">
          <el-select
            v-model="topologyId"
            clearable
            filterable
            placeholder="按当前节点复用案例拓扑"
            @change="applyTopology"
          >
            <el-option v-for="item in topologies" :key="item.id" :label="item.name" :value="item.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="拓扑说明">
          <el-input
            v-model="form.topology"
            type="textarea"
            :rows="3"
            maxlength="2000"
            show-word-limit
            placeholder="描述实训拓扑、设备连接或分组方式"
          />
        </el-form-item>
        <el-form-item label="启动节点">
          <el-select
            v-model="form.startupNodeId"
            clearable
            filterable
            placeholder="可选：指定实操使用的实训节点"
            @change="form.topologyBlueprint = null"
          >
            <el-option v-for="node in enabledNodes" :key="node.id" :label="node.name" :value="node.id" />
          </el-select>
        </el-form-item>
        <CaseTopologyEditor
          v-if="form.startupNodeId"
          :key="form.startupNodeId"
          v-model="form.topologyBlueprint"
          :node-id="form.startupNodeId"
          :devices="environmentDevices"
        />
        <el-form-item v-if="form.startupNodeId" label="环境预览">
          <el-alert v-if="environmentError" :title="environmentError" type="error" :closable="false" />
          <el-table v-else :data="environmentDevices" row-key="id" empty-text="所选节点暂无模拟设备" size="small">
            <el-table-column prop="name" label="设备" min-width="120" />
            <el-table-column label="接口" min-width="160">
              <template #default="{ row }">{{ row.interfaces.join('、') || '—' }}</template>
            </el-table-column>
          </el-table>
        </el-form-item>
        <CaseRubricEditor v-model="form.caseRubric" :node-id="form.startupNodeId" />
      </template>

      <template v-if="isPractice">
        <div class="question-heading">
          <div>
            <h3>练习题目</h3>
            <p>支持单选、多选和判断题，提交后由服务端自动评分。</p>
          </div>
          <div class="question-heading-actions">
            <el-button type="danger" plain :disabled="!selectedQuestions.length" @click="removeSelectedQuestions">
              删除所选题目
            </el-button>
            <el-button :icon="UploadFilled" @click="importVisible = true">批量导入</el-button>
            <el-button type="primary" :icon="Plus" @click="addQuestion">添加题目</el-button>
          </div>
        </div>
        <el-empty v-if="!form.questions.length" description="至少添加 1 道题目" />
        <div v-for="(question, questionIndex) in form.questions" :key="questionIndex" class="question-card">
          <div class="question-card-head">
            <el-checkbox
              :model-value="selectedQuestions.includes(question)"
              :label="`第 ${questionIndex + 1} 题`"
              @change="value => toggleQuestion(question, value === true)"
            />
            <div class="question-card-tools">
              <el-select
                :model-value="question.type"
                size="small"
                class="question-type-select"
                @change="(value: unknown) => setQuestionType(question, String(value))"
              >
                <el-option
                  v-for="option in questionTypeOptions"
                  :key="option.value"
                  :label="option.label"
                  :value="option.value"
                />
              </el-select>
              <el-input-number v-model="question.score" :min="1" :max="100" size="small" controls-position="right" />
              <span class="score-unit">分</span>
              <el-button
                link
                type="danger"
                :icon="Delete"
                :aria-label="`删除第${questionIndex + 1}题`"
                @click="removeQuestion(questionIndex)"
              >
                删除
              </el-button>
            </div>
          </div>
          <el-form-item label="题干" required>
            <el-input
              v-model="question.stem"
              type="textarea"
              :rows="2"
              maxlength="500"
              show-word-limit
              placeholder="输入题目内容"
            />
          </el-form-item>
          <div class="option-editor">
            <span class="option-editor-label">选项</span>
            <div v-for="(option, optionIndex) in question.options" :key="optionIndex" class="option-row">
              <span class="option-letter">{{ optionLetter(optionIndex) }}</span>
              <el-input
                v-model="question.options[optionIndex]"
                :disabled="question.type === 'judge'"
                maxlength="200"
                placeholder="选项内容"
              />
              <el-button
                v-if="question.type !== 'judge'"
                link
                type="danger"
                :icon="Delete"
                :disabled="question.options.length <= 2"
                :aria-label="`删除选项${optionLetter(optionIndex)}`"
                @click="removeOption(question, optionIndex)"
              />
            </div>
            <el-button
              v-if="question.type !== 'judge'"
              link
              type="primary"
              :icon="Plus"
              :disabled="question.options.length >= 10"
              @click="addOption(question)"
            >
              添加选项
            </el-button>
          </div>
          <div class="answer-editor">
            <span class="option-editor-label">正确答案</span>
            <el-radio-group
              v-if="question.type !== 'multiple'"
              :model-value="question.answers[0]"
              @change="(value: string | number | boolean | undefined) => setSingleAnswer(question, value)"
            >
              <el-radio v-for="(_, optionIndex) in question.options" :key="optionIndex" :value="optionIndex">
                {{ answerText(question, optionIndex) }}
              </el-radio>
            </el-radio-group>
            <el-checkbox-group v-else v-model="question.answers">
              <el-checkbox v-for="(_, optionIndex) in question.options" :key="optionIndex" :value="optionIndex">
                {{ answerText(question, optionIndex) }}
              </el-checkbox>
            </el-checkbox-group>
          </div>
          <el-form-item label="题目解析（可选）">
            <el-input
              v-model="question.analysis"
              type="textarea"
              :rows="2"
              maxlength="1000"
              show-word-limit
              placeholder="提交后向学生展示的解析"
            />
          </el-form-item>
        </div>
      </template>
    </el-form>
    <template #footer>
      <el-button @click="visible = false">取消</el-button>
      <el-button type="primary" :loading="saving" :disabled="loading" @click="save">
        {{ props.resourceId ? '保存修改' : '创建资源' }}
      </el-button>
    </template>
  </el-dialog>

  <QuestionImportDialog v-model="importVisible" @imported="onQuestionsImported" />
</template>

<style scoped>
.resource-skeleton {
  padding: 12px 0;
}

.resource-form-grid {
  display: grid;
  grid-template-columns: minmax(0, 1.5fr) minmax(160px, 0.8fr) minmax(170px, 0.8fr);
  gap: 0 16px;
}

.resource-form-grid :deep(.el-input-number) {
  width: 100%;
}

.field-hint,
.field-help {
  color: #61758b;
  font-size: 12px;
}

.field-hint {
  display: block;
  margin-top: 5px;
}

.field-help {
  margin: 5px 0 0;
}

.question-heading {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  padding: 18px 0 12px;
  border-top: 1px solid #dce7eb;
  margin-top: 8px;
}

.question-heading h3 {
  margin: 0;
  color: #1c334e;
  font-size: 16px;
}

.question-heading p {
  margin: 4px 0 0;
  color: #61758b;
  font-size: 12px;
}

.question-heading-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
}

.question-card {
  padding: 16px;
  margin-bottom: 14px;
  background: #f6faf8;
  border: 1px solid #dce7eb;
  border-radius: 10px;
}

.question-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
}

.question-card-tools {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 7px;
}

.question-type-select {
  width: 104px;
}

.score-unit {
  color: #61758b;
  font-size: 13px;
}

.option-editor,
.answer-editor {
  margin-bottom: 14px;
}

.option-editor-label {
  display: block;
  margin-bottom: 6px;
  color: #3e566f;
  font-size: 13px;
  font-weight: 600;
}

.option-row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 7px;
}

.option-letter {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  flex: 0 0 auto;
  color: #145a50;
  font-size: 12px;
  font-weight: 700;
  background: #e3f3ee;
  border-radius: 6px;
}

.answer-editor :deep(.el-radio),
.answer-editor :deep(.el-checkbox) {
  height: auto;
  align-items: flex-start;
  margin: 0 18px 7px 0;
  white-space: normal;
}

.answer-editor :deep(.el-radio__label),
.answer-editor :deep(.el-checkbox__label) {
  line-height: 1.45;
  white-space: normal;
}

@media (max-width: 720px) {
  .resource-form-grid {
    grid-template-columns: 1fr;
  }

  .question-card-head {
    align-items: flex-start;
    flex-direction: column;
  }
}
</style>

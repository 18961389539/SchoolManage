<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { Delete, EditPen, Plus, Refresh, Search } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, dateTime, del, listItems, messageOf, post, put } from '../api'
import { readListState, useListPersist } from '../listState'
import { durationText } from '../teaching'
import { examQuestionTypeOptions } from '../exam'
import CaseRubricEditor from './CaseRubricEditor.vue'
import type { CaseRubricItem } from '../caseWork'
import type {
  CampPositionAdminDetail,
  CampPositionAdminSummary,
  CampPositionInput,
  CampQuestionAdmin,
  CampRubricItemInput,
  ExamQuestionType,
  NodeRow
} from '../types'
import TableEmpty from './TableEmpty.vue'

interface EditableSkill {
  name: string
  subName: string
  ability: string
}

interface EditableRubric {
  item: string
  keyword: string
  score: number
}

interface EditableQuestion {
  type: ExamQuestionType
  stem: string
  options: string[]
  answers: number[]
  score: number
  analysis: string
  skillIndex: number
  rubric: EditableRubric[]
  practicalRubric: CaseRubricItem[]
  scoreMode: 'devices' | 'text'
}

const letters = 'ABCDEFGHIJ'
const items = ref<CampPositionAdminSummary[]>([])
const nodes = ref<NodeRow[]>([])
const loading = ref(false)
const error = ref('')
const search = ref(readListState('camp-positions').search)
useListPersist('camp-positions', () => ({ search: search.value, filters: {} }))

const dialogVisible = ref(false)
const saving = ref(false)
const formError = ref('')
const editingId = ref('')
const editingDetail = ref<CampPositionAdminDetail | null>(null)
const enabledNodes = computed(() => nodes.value.filter(node => node.enabled !== false))
const locked = computed(() => editingDetail.value?.canEditStructure === false)

const form = reactive({
  name: '',
  description: '',
  durationMinutes: 60,
  startupNodeId: '' as string | null,
  skills: [] as EditableSkill[],
  questions: [] as EditableQuestion[]
})

const filtered = computed(() => {
  const keyword = search.value.trim().toLowerCase()
  if (!keyword) return items.value
  return items.value.filter(item => [item.name, item.description].some(value => value.toLowerCase().includes(keyword)))
})

function newSkill(): EditableSkill {
  return { name: '', subName: '', ability: '' }
}

function newRubric(score = 10): EditableRubric {
  return { item: '', keyword: '', score }
}

function newQuestion(): EditableQuestion {
  return {
    type: 'single',
    stem: '',
    options: ['选项 A', '选项 B', '选项 C', '选项 D'],
    answers: [0],
    score: 10,
    analysis: '',
    skillIndex: 0,
    rubric: [newRubric(10)],
    practicalRubric: [],
    scoreMode: 'devices'
  }
}

function resetForm() {
  form.name = ''
  form.description = ''
  form.durationMinutes = 60
  form.startupNodeId = null
  form.skills = [newSkill()]
  form.questions = [newQuestion()]
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    const data = await api<{ items?: CampPositionAdminSummary[] }>('/camp/positions')
    items.value = listItems<CampPositionAdminSummary>(data)
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

async function loadNodes() {
  try {
    const data = await api<{ items?: NodeRow[] }>('/nodes?page=1&pageSize=1000')
    nodes.value = listItems<NodeRow>(data)
  } catch {
    nodes.value = []
  }
}

function openCreate() {
  editingId.value = ''
  editingDetail.value = null
  resetForm()
  formError.value = ''
  dialogVisible.value = true
}

function fromQuestion(question: CampQuestionAdmin): EditableQuestion {
  return {
    type: question.type,
    stem: question.stem,
    options:
      question.type === 'practical' ? [] : question.options.length ? [...question.options] : ['选项 A', '选项 B'],
    answers: question.type === 'practical' ? [] : question.answers.length ? [...question.answers] : [0],
    score: question.score,
    analysis: question.analysis,
    skillIndex: question.skillIndex >= 0 ? question.skillIndex : 0,
    practicalRubric: question.practicalRubric.map(item => ({ ...item })),
    scoreMode: question.rubric.length ? 'text' : 'devices',
    rubric: question.rubric.length
      ? question.rubric.map(item => ({ item: item.item, keyword: item.keyword, score: item.score }))
      : question.type === 'practical'
        ? [newRubric(question.score)]
        : []
  }
}

async function openEdit(position: CampPositionAdminSummary) {
  editingId.value = position.id
  editingDetail.value = null
  formError.value = ''
  dialogVisible.value = true
  try {
    const detail = await api<CampPositionAdminDetail>(`/camp/positions/${position.id}`)
    editingDetail.value = detail
    form.name = detail.name
    form.description = detail.description
    form.durationMinutes = detail.durationMinutes
    form.startupNodeId = detail.startupNodeId
    form.skills = detail.skills.length
      ? detail.skills.map(skill => ({ name: skill.name, subName: skill.subName, ability: skill.ability }))
      : [newSkill()]
    form.questions = detail.questions.map(fromQuestion)
  } catch (failure) {
    formError.value = messageOf(failure)
  }
}

function addSkill() {
  if (form.skills.length >= 100) {
    ElMessage.warning('技能项最多 100 条')
    return
  }
  form.skills.push(newSkill())
}

function removeSkill(index: number) {
  if (form.skills.length <= 1) {
    ElMessage.warning('至少保留 1 项技能')
    return
  }
  form.skills.splice(index, 1)
  for (const question of form.questions) {
    if (question.skillIndex === index) question.skillIndex = 0
    else if (question.skillIndex > index) question.skillIndex--
  }
}

function addQuestion() {
  if (form.questions.length >= 200) {
    ElMessage.warning('单个岗位最多 200 道测评题')
    return
  }
  form.questions.push(newQuestion())
}

function removeQuestion(index: number) {
  if (form.questions.length <= 1) {
    ElMessage.warning('至少保留 1 道测评题')
    return
  }
  form.questions.splice(index, 1)
}

function setQuestionType(question: EditableQuestion, value: string) {
  const type = value as ExamQuestionType
  question.type = type
  if (type === 'practical') {
    question.options = []
    question.answers = []
    if (!question.rubric.length) question.rubric = [newRubric(question.score)]
    return
  }
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

function addOption(question: EditableQuestion) {
  if (question.options.length < 10) question.options.push('')
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

function addRubric(question: EditableQuestion) {
  if (question.rubric.length >= 20) return
  question.rubric.push(newRubric(1))
}

function removeRubric(question: EditableQuestion, index: number) {
  if (question.rubric.length <= 1) return
  question.rubric.splice(index, 1)
}

function rubricTotal(question: EditableQuestion) {
  return question.rubric.reduce((total, item) => total + (Number(item.score) || 0), 0)
}

function optionLetter(index: number) {
  return letters[index] || String(index + 1)
}

function answerText(question: EditableQuestion, index: number) {
  return `${optionLetter(index)}. ${question.options[index]?.trim() || '（空选项）'}`
}

function validateBasics(): string {
  if (!form.name.trim()) return '请填写岗位名称'
  if (form.name.trim().length > 100) return '岗位名称不能超过 100 个字符'
  if (!Number.isInteger(form.durationMinutes) || form.durationMinutes < 1 || form.durationMinutes > 600)
    return '测评时长应在 1–600 分钟之间'
  return ''
}

function validateSkills(): string {
  if (!form.skills.length) return '请至少配置 1 项技能'
  for (const [index, skill] of form.skills.entries()) {
    if (!skill.name.trim()) return `第 ${index + 1} 项技能名称不能为空`
  }
  return ''
}

function validatePractical(question: EditableQuestion, label: string): string {
  if (question.scoreMode === 'devices') {
    if (!form.startupNodeId) return `${label}请选择模拟启动节点`
    if (!question.practicalRubric.length) return `${label}请选择要评分的设备配置项`
    return question.practicalRubric.reduce((sum, item) => sum + item.score, 0) === question.score
      ? ''
      : `${label}设备评分项分值合计必须等于题目分值`
  }
  if (!question.rubric.length) return `${label}实操题至少需要 1 条评分项`
  if (question.rubric.some(item => !item.item.trim())) return `${label}评分项名称不能为空`
  if (question.rubric.some(item => !item.keyword.trim())) return `${label}评分关键词不能为空`
  if (question.rubric.some(item => !Number.isInteger(item.score) || item.score < 1 || item.score > 100))
    return `${label}评分项分值应在 1–100 之间`
  if (rubricTotal(question) !== question.score)
    return `${label}评分项合计 ${rubricTotal(question)} 分，应等于题目分值 ${question.score} 分`
  return ''
}

function validateObjective(question: EditableQuestion, label: string): string {
  if (question.options.length < 2) return `${label}选择题至少需要 2 个选项`
  if (question.options.some(option => !option.trim())) return `${label}存在空选项`
  if (!question.answers.length) return `${label}请设置正确答案`
  if (question.type !== 'multiple' && question.answers.length !== 1) return `${label}只能设置一个正确答案`
  if (question.answers.some(answer => answer < 0 || answer >= question.options.length))
    return `${label}正确答案超出选项范围`
  return ''
}

function validateQuestion(question: EditableQuestion, label: string): string {
  if (!question.stem.trim()) return `${label}题干不能为空`
  if (!Number.isInteger(question.score) || question.score < 1 || question.score > 100)
    return `${label}分值应在 1–100 之间`
  if (question.skillIndex < 0 || question.skillIndex >= form.skills.length) return `${label}请选择对应技能`
  return question.type === 'practical' ? validatePractical(question, label) : validateObjective(question, label)
}

function validateStructure(): string {
  const skillIssue = validateSkills()
  if (skillIssue) return skillIssue
  if (!form.questions.length) return '请至少配置 1 道测评题'
  for (const [index, question] of form.questions.entries()) {
    const issue = validateQuestion(question, `第 ${index + 1} 题`)
    if (issue) return issue
  }
  return ''
}

function validation(): string {
  const basic = validateBasics()
  if (basic) return basic
  return locked.value ? '' : validateStructure()
}

function payload(): CampPositionInput {
  return {
    name: form.name.trim(),
    description: form.description.trim(),
    durationMinutes: form.durationMinutes,
    startupNodeId: form.startupNodeId || null,
    skills: form.skills.map(skill => ({
      name: skill.name.trim(),
      subName: skill.subName.trim(),
      ability: skill.ability.trim()
    })),
    questions: form.questions.map(question => ({
      type: question.type,
      stem: question.stem.trim(),
      options: question.type === 'practical' ? [] : question.options.map(option => option.trim()),
      answers: question.type === 'practical' ? [] : [...question.answers].sort((left, right) => left - right),
      score: question.score,
      analysis: question.analysis.trim(),
      skillIndex: question.skillIndex,
      practicalRubric:
        question.type === 'practical' && question.scoreMode === 'devices' ? question.practicalRubric : [],
      rubric:
        question.type === 'practical' && question.scoreMode === 'text'
          ? question.rubric.map<CampRubricItemInput>(item => ({
              item: item.item.trim(),
              keyword: item.keyword.trim(),
              score: item.score
            }))
          : []
    }))
  }
}

async function save() {
  const issue = validation()
  if (issue) {
    formError.value = issue
    return
  }
  saving.value = true
  formError.value = ''
  try {
    if (editingId.value) await put(`/camp/positions/${editingId.value}`, payload())
    else await post('/camp/positions', payload())
    ElMessage.success(editingId.value ? '岗位已更新' : '岗位已创建，发布后学生才能在岗位训练营中看到')
    dialogVisible.value = false
    await load()
  } catch (failure) {
    formError.value = messageOf(failure)
  } finally {
    saving.value = false
  }
}

async function remove(position: CampPositionAdminSummary) {
  try {
    await ElMessageBox.confirm(
      `确定删除岗位“${position.name}”吗？技能与题目会一并删除；已有测评记录的岗位不能删除。`,
      '删除岗位',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' }
    )
    await del(`/camp/positions/${position.id}`)
    ElMessage.success('岗位已删除')
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

async function setPublished(position: CampPositionAdminSummary, published: boolean) {
  try {
    if (published) {
      await ElMessageBox.confirm(
        `发布后，学生可在「岗位训练营」中看到岗位“${position.name}”并开始测评（需已配置题目）。确认发布？`,
        '发布岗位',
        { type: 'warning', confirmButtonText: '确认发布', cancelButtonText: '取消' }
      )
    } else {
      await ElMessageBox.confirm(
        `取消发布后，尚未开始的学生将不再看到岗位“${position.name}”。进行中的测评仍可继续。确认取消发布？`,
        '取消发布岗位',
        { type: 'warning', confirmButtonText: '确认取消发布', cancelButtonText: '取消' }
      )
    }
    await post(`/camp/positions/${position.id}/publish`, { published })
    ElMessage.success(published ? '岗位已发布，学生可在岗位训练营中看到' : '已取消发布，尚未开始的学生将不再看到该岗位')
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

onMounted(() => {
  void load()
  void loadNodes()
})
</script>

<template>
  <section class="page-heading">
    <div>
      <h1>岗位训练营</h1>
      <p>配置岗位技能、测评题库与实操评分项。新建后默认不发布，发布后学生才能在岗位训练营中看到。</p>
    </div>
    <div class="heading-actions">
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      <el-button type="primary" :icon="Plus" @click="openCreate">新建岗位</el-button>
    </div>
  </section>

  <section class="surface">
    <div class="table-toolbar">
      <el-input v-model="search" class="search-box" clearable placeholder="搜索岗位名称或描述" :prefix-icon="Search" />
      <span class="record-count">共 {{ loading && !filtered.length ? '—' : filtered.length }} 个岗位</span>
    </div>
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="table-error" />
    <el-table v-loading="loading" :data="filtered" row-key="id" empty-text="">
      <template #empty>
        <TableEmpty
          v-if="!loading"
          :title="error ? '岗位加载失败' : '暂无岗位'"
          :hint="
            error ? '请检查服务状态后刷新重试' : '点击「新建岗位」配置技能与测评题，发布后学生才能在岗位训练营中看到。'
          "
        />
      </template>
      <el-table-column prop="name" label="岗位" min-width="200">
        <template #default="{ row }">
          <div class="table-name">
            <strong>{{ row.name }}</strong>
            <el-tag v-if="row.attemptCount" size="small" type="info" effect="plain">已有测评</el-tag>
          </div>
          <p class="position-description" :title="row.description || '暂无描述'">{{ row.description || '暂无描述' }}</p>
        </template>
      </el-table-column>
      <el-table-column label="技能" width="80">
        <template #default="{ row }">{{ row.skillCount }}</template>
      </el-table-column>
      <el-table-column label="测评题" min-width="130">
        <template #default="{ row }">{{ row.questionCount }} 题 · 实操 {{ row.practicalCount }}</template>
      </el-table-column>
      <el-table-column label="满分" width="80">
        <template #default="{ row }">{{ row.maxScore }}</template>
      </el-table-column>
      <el-table-column label="时长" width="105">
        <template #default="{ row }">{{ durationText(row.durationMinutes) }}</template>
      </el-table-column>
      <el-table-column label="测评人数" width="90">
        <template #default="{ row }">{{ row.attemptCount }}</template>
      </el-table-column>
      <el-table-column label="发布状态" width="100">
        <template #default="{ row }">
          <el-tag :type="row.published ? 'success' : 'info'" size="small" effect="plain">
            {{ row.published ? '已发布' : '未发布' }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="更新时间" width="170">
        <template #default="{ row }">{{ dateTime(row.updatedAt) }}</template>
      </el-table-column>
      <el-table-column label="操作" width="220" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="setPublished(row as CampPositionAdminSummary, !row.published)">
            {{ row.published ? '取消发布' : '发布' }}
          </el-button>
          <el-button link type="primary" :icon="EditPen" @click="openEdit(row as CampPositionAdminSummary)"
            >编辑</el-button
          >
          <el-button link type="danger" :icon="Delete" @click="remove(row as CampPositionAdminSummary)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>
  </section>

  <el-dialog
    v-model="dialogVisible"
    :title="editingId ? '编辑岗位' : '新建岗位'"
    width="min(1080px, 97vw)"
    destroy-on-close
    top="4vh"
  >
    <el-alert v-if="formError" :title="formError" type="error" show-icon :closable="false" class="section-notice" />
    <el-alert
      v-if="locked"
      title="该岗位已有测评记录，只能修改名称与描述；技能、题目和时长已锁定。"
      type="warning"
      show-icon
      :closable="false"
      class="section-notice"
    />
    <el-form scroll-to-error label-position="top" class="entity-form">
      <div class="position-form-grid">
        <el-form-item label="岗位名称" required>
          <el-input v-model="form.name" maxlength="100" show-word-limit placeholder="例如：网络运维工程师" />
        </el-form-item>
        <el-form-item label="测评时长（分钟）" required>
          <el-input-number
            v-model="form.durationMinutes"
            :min="1"
            :max="600"
            :disabled="locked"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="启动节点">
          <el-select
            v-model="form.startupNodeId"
            clearable
            filterable
            :disabled="locked"
            placeholder="可选：指定实操环境节点"
          >
            <el-option v-for="node in enabledNodes" :key="node.id" :label="node.name" :value="node.id" />
          </el-select>
        </el-form-item>
      </div>
      <el-form-item label="岗位描述">
        <el-input
          v-model="form.description"
          type="textarea"
          :rows="3"
          maxlength="2000"
          show-word-limit
          placeholder="岗位职责、适用专业或测评目标"
        />
      </el-form-item>
    </el-form>

    <template v-if="!locked">
      <section class="editor-section">
        <div class="editor-heading">
          <div>
            <h3>岗位技能</h3>
            <p>技能名称用于能力雷达分组，子技能与具体能力用于报告明细。</p>
          </div>
          <el-button type="primary" :icon="Plus" @click="addSkill">添加技能</el-button>
        </div>
        <div v-for="(skill, index) in form.skills" :key="index" class="skill-row">
          <span class="skill-index">{{ index + 1 }}</span>
          <el-input v-model="skill.name" maxlength="100" placeholder="技能名称，例如：网络基础" />
          <el-input v-model="skill.subName" maxlength="100" placeholder="子技能，例如：路由交换" />
          <el-input v-model="skill.ability" maxlength="200" placeholder="具体能力描述" />
          <el-button
            link
            type="danger"
            :icon="Delete"
            :disabled="form.skills.length <= 1"
            @click="removeSkill(index)"
          />
        </div>
      </section>

      <section class="editor-section">
        <div class="editor-heading">
          <div>
            <h3>测评题</h3>
            <p>评分项合计必须等于题目分值。</p>
          </div>
          <el-button type="primary" :icon="Plus" @click="addQuestion">添加题目</el-button>
        </div>
        <div v-for="(question, questionIndex) in form.questions" :key="questionIndex" class="question-card">
          <div class="question-card-head">
            <strong>第 {{ questionIndex + 1 }} 题</strong>
            <div class="question-card-tools">
              <el-select
                :model-value="question.type"
                size="small"
                class="question-type-select"
                @change="(value: unknown) => setQuestionType(question, String(value))"
              >
                <el-option
                  v-for="option in examQuestionTypeOptions"
                  :key="option.value"
                  :label="option.label"
                  :value="option.value"
                />
              </el-select>
              <el-select v-model="question.skillIndex" size="small" class="skill-select" placeholder="对应技能">
                <el-option
                  v-for="(skill, skillIndex) in form.skills"
                  :key="skillIndex"
                  :label="skill.name || `技能 ${skillIndex + 1}`"
                  :value="skillIndex"
                />
              </el-select>
              <el-input-number v-model="question.score" :min="1" :max="100" size="small" controls-position="right" />
              <span class="score-unit">分</span>
              <el-button
                link
                type="danger"
                :icon="Delete"
                :disabled="form.questions.length <= 1"
                @click="removeQuestion(questionIndex)"
              >
                删除
              </el-button>
            </div>
          </div>
          <el-form-item label="题干" required>
            <el-input v-model="question.stem" type="textarea" :rows="2" maxlength="1000" show-word-limit />
          </el-form-item>
          <template v-if="question.type === 'practical'">
            <el-form-item label="评分方式">
              <el-radio-group v-model="question.scoreMode" :disabled="locked">
                <el-radio-button value="devices">设备配置自动评分</el-radio-button>
                <el-radio-button value="text">文字作答评分</el-radio-button>
              </el-radio-group>
            </el-form-item>
            <CaseRubricEditor
              v-if="question.scoreMode === 'devices'"
              v-model="question.practicalRubric"
              :node-id="form.startupNodeId"
            />
            <template v-else>
              <div class="rubric-heading">
                <div>
                  <span class="rubric-title">评分项</span>
                  <small>关键词支持“|”分隔多个同义表述，学员作答命中任一关键词即得分。</small>
                </div>
                <div class="rubric-total" :class="{ invalid: rubricTotal(question) !== question.score }">
                  合计 {{ rubricTotal(question) }} / {{ question.score }} 分
                  <el-button
                    link
                    type="primary"
                    :icon="Plus"
                    :disabled="question.rubric.length >= 20"
                    @click="addRubric(question)"
                  >
                    添加评分项
                  </el-button>
                </div>
              </div>
              <div v-for="(rubric, rubricIndex) in question.rubric" :key="rubricIndex" class="rubric-row">
                <el-input v-model="rubric.item" maxlength="100" placeholder="评分项名称，例如：VLAN 规划" />
                <el-input v-model="rubric.keyword" maxlength="200" placeholder="关键词，例如：VLAN|划分" />
                <el-input-number v-model="rubric.score" :min="1" :max="100" size="small" controls-position="right" />
                <span class="score-unit">分</span>
                <el-button
                  link
                  type="danger"
                  :icon="Delete"
                  :disabled="question.rubric.length <= 1"
                  @click="removeRubric(question, rubricIndex)"
                />
              </div>
            </template>
          </template>
          <template v-else>
            <div class="option-editor">
              <span class="option-editor-label">选项</span>
              <div v-for="(_, optionIndex) in question.options" :key="optionIndex" class="option-row">
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
          </template>
          <el-form-item label="题目解析（可选）">
            <el-input v-model="question.analysis" type="textarea" :rows="2" maxlength="2000" show-word-limit />
          </el-form-item>
        </div>
      </section>
    </template>
    <template v-else-if="editingDetail">
      <section class="editor-section">
        <h3>锁定内容概览</h3>
        <div class="locked-skills">
          <el-tag v-for="skill in editingDetail.skills" :key="skill.id" effect="plain">
            {{ skill.name }}{{ skill.subName ? ` / ${skill.subName}` : '' }}
          </el-tag>
        </div>
        <p class="locked-summary">
          共 {{ editingDetail.questionCount }} 道题（实操 {{ editingDetail.practicalCount }}） · 满分
          {{ editingDetail.maxScore }} · 时长 {{ durationText(editingDetail.durationMinutes) }} ·
          {{ editingDetail.attemptCount }} 人已参与
        </p>
      </section>
    </template>

    <template #footer>
      <el-button @click="dialogVisible = false">取消</el-button>
      <el-button type="primary" :loading="saving" @click="save">{{ editingId ? '保存修改' : '创建岗位' }}</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.position-description {
  margin: 4px 0 0;
  color: #5d6f80;
  font-size: 12px;
  line-height: 1.45;
  display: -webkit-box;
  overflow: hidden;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}

.position-form-grid {
  display: grid;
  grid-template-columns: minmax(0, 1.6fr) minmax(150px, 0.7fr) minmax(190px, 0.9fr);
  gap: 0 14px;
}

.editor-section {
  padding-top: 16px;
  margin-top: 8px;
  border-top: 1px solid #dce7eb;
}

.editor-heading {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 14px;
  margin-bottom: 12px;
}

.editor-heading h3 {
  margin: 0;
  color: #1c334e;
  font-size: 16px;
}

.editor-heading p {
  margin: 4px 0 0;
  color: #61758b;
  font-size: 12px;
}

.skill-row {
  display: grid;
  grid-template-columns: 28px minmax(0, 1fr) minmax(0, 1fr) minmax(0, 1.3fr) 32px;
  align-items: center;
  gap: 8px;
  padding: 8px;
  margin-bottom: 8px;
  background: #f6faf8;
  border: 1px solid #dce7eb;
  border-radius: 8px;
}

.skill-index {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  color: #145a50;
  font-size: 12px;
  font-weight: 700;
  background: #e3f3ee;
  border-radius: 6px;
}

.question-card {
  padding: 15px;
  margin-bottom: 14px;
  background: #f6faf8;
  border: 1px solid #dce7eb;
  border-radius: 10px;
}

.question-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 10px;
  margin-bottom: 12px;
}

.question-card-tools {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 7px;
}

.question-type-select {
  width: 110px;
}

.skill-select {
  width: 150px;
}

.score-unit {
  color: #61758b;
  font-size: 12px;
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

.rubric-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 8px;
  color: #3e566f;
}

.rubric-title {
  font-size: 13px;
  font-weight: 600;
}

.rubric-heading small {
  color: #56697c;
  font-size: 11px;
}

.rubric-total {
  display: flex;
  align-items: center;
  gap: 8px;
  color: #2f6d52;
  font-size: 12px;
}

.rubric-total.invalid {
  color: #94610f;
}

.rubric-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1.4fr) 110px auto 32px;
  align-items: center;
  gap: 8px;
  margin-bottom: 7px;
}

.locked-skills {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin: 10px 0;
}

.locked-summary {
  margin: 0;
  color: #61758b;
  font-size: 13px;
}

@media (max-width: 860px) {
  .position-form-grid {
    grid-template-columns: 1fr;
  }

  .skill-row {
    grid-template-columns: 28px minmax(0, 1fr) 32px;
  }

  .skill-row :deep(.el-input:nth-of-type(2)),
  .skill-row :deep(.el-input:nth-of-type(3)) {
    grid-column: 2;
  }

  .rubric-row {
    grid-template-columns: minmax(0, 1fr) 32px;
  }

  .rubric-row :deep(.el-input:nth-of-type(2)),
  .rubric-row :deep(.el-input-number) {
    grid-column: 1;
  }
}
</style>

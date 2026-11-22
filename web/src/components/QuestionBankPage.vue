<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import {
  ArrowLeft,
  Delete,
  Document,
  EditPen,
  Plus,
  Refresh,
  Search,
  UploadFilled,
  View
} from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ApiError, api, dateTime, del, limits, listItems, maxUploadMegabytes, messageOf, post, put } from '../api'
import { readListState, writeListState } from '../listState'
import { recoverPage } from '../paging'
import { examQuestionTypeLabel, examQuestionTypeOptions, optionLetters } from '../exam'
import { focusFirstField } from '../focusField'
import type {
  ExamQuestionDetail,
  ExamQuestionInput,
  ExamQuestionSummary,
  ExamQuestionType,
  ExamQuestionBank,
  NodeRow,
  Paged
} from '../types'
import type { CaseRubricItem } from '../caseWork'
import CaseRubricEditor from './CaseRubricEditor.vue'
import QuestionBankSelect from './QuestionBankSelect.vue'
import { bankAccepts, examQuestionBankLabels } from '../questionBanks'

interface ImportPreview {
  bankType: ExamQuestionBank['type']
  total: number
  validCount: number
  invalidCount: number
  items: {
    rowNumber: number
    type: ExamQuestionType
    stem: string
    score: number
    errors: { column: string; message: string }[]
  }[]
}

const props = defineProps<{ bank: ExamQuestionBank }>()
const emit = defineEmits<{ back: [] }>()
const scope = computed(() => new URLSearchParams({ bankId: props.bank.id }).toString())
const allowedQuestionTypes = computed(() =>
  examQuestionTypeOptions.filter(option => bankAccepts(props.bank.type, option.value))
)

const items = ref<ExamQuestionSummary[]>([])
const total = ref(0)
const listKey = `exam-questions:${props.bank.id}`
const savedList = readListState(listKey)
const page = ref(savedList.page)
const pageSize = ref(savedList.pageSize)
const loading = ref(false)
const error = ref('')
const search = ref(savedList.search)
const typeFilter = ref(savedList.filters.type || '')
const selected = ref<ExamQuestionSummary[]>([])
const deleting = ref(false)
let requestVersion = 0

const dialogVisible = ref(false)
const editing = ref<ExamQuestionSummary | null>(null)
const saving = ref(false)
const loadingPractical = ref(false)
const nodes = ref<NodeRow[]>([])
const formError = ref('')
const form = reactive({
  bankId: '',
  type: 'single' as ExamQuestionType,
  stem: '',
  options: ['', '', '', ''],
  answers: [] as number[],
  score: 5,
  analysis: '',
  practicalMode: 'automatic' as 'automatic' | 'manual',
  practicalNodeId: null as string | null,
  practicalRubric: [] as CaseRubricItem[],
  practicalResources: ''
})
const formBaseline = ref('')
const formDirty = computed(() => JSON.stringify(form) !== formBaseline.value)

const previewVisible = ref(false)
const preview = ref<ExamQuestionDetail | null>(null)
const previewLoading = ref(false)

const importVisible = ref(false)
const importBusy = ref(false)
const importError = ref('')
const importFile = ref<File | null>(null)
const importFileName = ref('')
const importPreview = ref<ImportPreview | null>(null)
const previewPage = ref(1)
const removedInvalidRows = ref(false)
const previewPageSize = 25
const fileInput = ref<HTMLInputElement>()
const questionForm = ref<{ $el?: HTMLElement }>()
const chooseFileButton = ref<{ ref?: HTMLElement; $el?: HTMLElement }>()
function focusQuestionForm() {
  focusFirstField(questionForm.value?.$el)
}
function focusChooseFile() {
  const button = chooseFileButton.value?.ref || chooseFileButton.value?.$el
  button?.focus?.()
}
function previewImportReason(): string | undefined {
  if (!importFile.value) return '请先选择要导入的文件'
}
function confirmImportReason(): string | undefined {
  if (importPreview.value && !importPreview.value.validCount) return '没有可导入的有效题目'
}

const isObjective = computed(() => form.type !== 'practical')

async function load() {
  const version = ++requestVersion
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams(scope.value)
    params.set('page', String(page.value))
    params.set('pageSize', String(pageSize.value))
    if (search.value.trim()) params.set('search', search.value.trim())
    if (typeFilter.value) params.set('type', typeFilter.value)
    const data = await api<Paged<ExamQuestionSummary>>(`/exam-questions?${params.toString()}`)
    if (version !== requestVersion) return
    items.value = data.items
    total.value = data.total
    const recovered = recoverPage(page.value, pageSize.value, total.value, items.value.length)
    if (recovered !== null && recovered !== page.value) {
      page.value = recovered
      return load()
    }
  } catch (failure) {
    if (version === requestVersion) error.value = messageOf(failure)
  } finally {
    if (version === requestVersion) loading.value = false
  }
}

function saveListState() {
  writeListState(listKey, {
    search: search.value,
    filters: { type: typeFilter.value },
    page: page.value,
    pageSize: pageSize.value
  })
}

function applyFilters() {
  page.value = 1
  saveListState()
  void load()
}

function resetFilters() {
  search.value = ''
  typeFilter.value = ''
  applyFilters()
}

watch([page, pageSize], saveListState)

function resetAnswerState() {
  form.answers = []
}

function changeType() {
  if (form.type === 'judge') {
    form.options = ['正确', '错误']
    resetAnswerState()
  } else if (form.type === 'practical') {
    form.options = []
    resetAnswerState()
    void loadNodes()
  } else {
    if (form.options.length < 2 || form.options.every(option => !option.trim())) form.options = ['', '', '', '']
    resetAnswerState()
  }
}

async function loadNodes() {
  if (nodes.value.length) return
  try {
    nodes.value = listItems<NodeRow>(await api('/nodes?pageSize=1000')).filter(node => node.provider === 'simulated')
  } catch (failure) {
    formError.value = messageOf(failure)
  }
}

function openCreate() {
  editing.value = null
  form.bankId = props.bank.id
  form.type = props.bank.type === 'practical' ? 'practical' : 'single'
  form.stem = ''
  form.options = ['', '', '', '']
  form.answers = []
  form.score = 5
  form.analysis = ''
  form.practicalMode = 'automatic'
  form.practicalNodeId = null
  form.practicalRubric = []
  form.practicalResources = ''
  loadingPractical.value = false
  formError.value = ''
  changeType()
  formBaseline.value = JSON.stringify(form)
  dialogVisible.value = true
}

async function openEdit(question: ExamQuestionSummary) {
  editing.value = question
  form.bankId = props.bank.id
  form.type = question.type
  form.stem = question.stem
  form.options = question.type === 'judge' ? ['正确', '错误'] : question.options.slice()
  form.answers = question.answers.slice()
  form.score = question.score
  form.analysis = question.analysis
  form.practicalMode = question.automaticPractical ? 'automatic' : 'manual'
  form.practicalNodeId = null
  form.practicalRubric = []
  form.practicalResources = ''
  loadingPractical.value = false
  formError.value = ''
  formBaseline.value = JSON.stringify(form)
  dialogVisible.value = true
  if (question.type !== 'practical') return
  void loadNodes()
  loadingPractical.value = true
  try {
    const detail = await api<ExamQuestionDetail>(`/exam-questions/${question.id}?${scope.value}`)
    if (editing.value?.id !== question.id) return
    form.practicalNodeId = detail.practicalNodeId || null
    form.practicalRubric = detail.practicalRubric
    form.practicalResources = detail.practicalResources
    formBaseline.value = JSON.stringify(form)
  } catch (failure) {
    if (editing.value?.id === question.id) formError.value = messageOf(failure)
  } finally {
    if (editing.value?.id === question.id) loadingPractical.value = false
  }
}

async function confirmDiscardQuestion() {
  if (saving.value) return false
  if (loadingPractical.value) return true
  if (!formDirty.value) return true
  try {
    await ElMessageBox.confirm('题目内容还没有保存，关闭后不会保留。', '放弃未保存的修改？', {
      type: 'warning',
      confirmButtonText: '放弃修改',
      cancelButtonText: '继续编辑'
    })
    return true
  } catch {
    return false
  }
}

async function handleQuestionBeforeClose(done: () => void) {
  if (await confirmDiscardQuestion()) done()
}

async function cancelQuestion() {
  if (await confirmDiscardQuestion()) dialogVisible.value = false
}

function addOption() {
  if (form.options.length >= 10) return
  form.options.push('')
}

function removeOption(index: number) {
  if (form.options.length <= 2) return
  form.options.splice(index, 1)
  form.answers = form.answers.filter(answer => answer !== index).map(answer => (answer > index ? answer - 1 : answer))
}

function setSingleAnswer(value: string | number | boolean | undefined) {
  form.answers = value === undefined || value === null || value === '' ? [] : [Number(value)]
}

function validate(): string {
  if (!form.stem.trim()) return '请填写题干'
  if (form.stem.trim().length > 1000) return '题干不能超过 1000 个字符'
  if (form.score < 1 || form.score > 100) return '分值应在 1–100 之间'
  return form.type === 'practical' ? validatePractical() : validateOptions()
}

function validatePractical() {
  if (form.practicalMode === 'automatic' && (!form.practicalNodeId || !form.practicalRubric.length))
    return '请选择模拟节点并添加至少一个评分项'
  return ''
}

function validateOptions() {
  if (form.options.length < 2) return '客观题至少需要两个选项'
  if (form.options.some(option => !option.trim())) return '选项内容不能为空'
  if (form.options.length > 10) return '客观题最多十个选项'
  if (form.answers.length === 0) return '请设置正确答案'
  if (form.type !== 'multiple' && form.answers.length !== 1) return '单选题和判断题只能有一个正确答案'
  if (form.answers.some(answer => answer < 0 || answer >= form.options.length)) return '正确答案选项无效'
  return ''
}

async function saveQuestion() {
  if (loadingPractical.value) return
  formError.value = validate()
  if (formError.value) return
  saving.value = true
  try {
    const payload: ExamQuestionInput = {
      bankId: form.bankId,
      type: form.type,
      stem: form.stem.trim(),
      options: form.type === 'practical' ? [] : form.options.map(option => option.trim()),
      answers: form.type === 'practical' ? [] : form.answers.slice().sort((left, right) => left - right),
      score: form.score,
      analysis: form.analysis.trim(),
      practicalResources: form.type === 'practical' ? form.practicalResources.trim() : undefined,
      ...(form.type === 'practical' && form.practicalMode === 'automatic'
        ? { practicalNodeId: form.practicalNodeId || undefined, practicalRubric: form.practicalRubric }
        : {})
    }
    if (editing.value) await put(`/exam-questions/${editing.value.id}?${scope.value}`, payload)
    else await post('/exam-questions', payload)
    ElMessage.success(
      form.bankId !== props.bank.id ? '题目已移入所选题库' : editing.value ? '题目已更新' : '题目已创建'
    )
    dialogVisible.value = false
    await load()
  } catch (failure) {
    formError.value = messageOf(failure)
  } finally {
    saving.value = false
  }
}

async function removeQuestion(question: ExamQuestionSummary) {
  try {
    await ElMessageBox.confirm(`确定删除题目“${question.stem.slice(0, 40)}”吗？`, '删除题目', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消'
    })
    await del(`/exam-questions/${question.id}?${scope.value}`)
    ElMessage.success('题目已删除')
    if (items.value.length === 1 && page.value > 1) page.value--
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

async function removeSelected() {
  if (!selected.value.length) return
  try {
    await ElMessageBox.confirm(`确定删除选中的 ${selected.value.length} 道题目吗？`, '批量删除题目', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消'
    })
    deleting.value = true
    await post(`/exam-questions/batch-delete?${scope.value}`, { ids: selected.value.map(item => item.id) })
    ElMessage.success('已删除所选题目')
    selected.value = []
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  } finally {
    deleting.value = false
  }
}

async function openPreview(question: ExamQuestionSummary) {
  previewVisible.value = true
  previewLoading.value = true
  preview.value = null
  try {
    preview.value = await api<ExamQuestionDetail>(`/exam-questions/${question.id}?${scope.value}`)
  } catch (failure) {
    ElMessage.error(messageOf(failure))
    previewVisible.value = false
  } finally {
    previewLoading.value = false
  }
}

function resetImport() {
  importBusy.value = false
  importError.value = ''
  importFile.value = null
  importFileName.value = ''
  importPreview.value = null
  previewPage.value = 1
  removedInvalidRows.value = false
  if (fileInput.value) fileInput.value.value = ''
}

function openImport() {
  resetImport()
  importVisible.value = true
}

function chooseImportFile(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]
  if (!file) return
  importPreview.value = null
  previewPage.value = 1
  removedInvalidRows.value = false
  if (file.size > limits.maxUploadBytes) {
    importError.value = `文件大小不能超过 ${maxUploadMegabytes()} MB`
    importFile.value = null
    importFileName.value = ''
    return
  }
  importFile.value = file
  importFileName.value = file.name
  importError.value = ''
}

function importPayload(file: File, removeInvalid: boolean): FormData {
  const data = new FormData()
  data.append('file', file)
  data.append('bankId', props.bank.id)
  if (removeInvalid) data.append('removeInvalidRows', 'true')
  return data
}

async function checkImport(removeInvalid = removedInvalidRows.value) {
  const file = importFile.value
  if (!file || importBusy.value) return
  importBusy.value = true
  importError.value = ''
  try {
    const data = importPayload(file, removeInvalid)
    data.append('preview', 'true')
    importPreview.value = await api<ImportPreview>(
      `/exam-questions/import?page=${previewPage.value}&pageSize=${previewPageSize}`,
      {
        method: 'POST',
        body: data
      }
    )
    removedInvalidRows.value = removeInvalid
  } catch (failure) {
    importError.value = messageOf(failure)
  } finally {
    importBusy.value = false
  }
}

function removeInvalidPreview() {
  previewPage.value = 1
  void checkImport(true)
}

async function runImport() {
  if (!importFile.value || !importPreview.value?.validCount || importBusy.value) return
  if (importPreview.value.invalidCount && !removedInvalidRows.value) return
  importBusy.value = true
  importError.value = ''
  try {
    const data = importPayload(importFile.value, removedInvalidRows.value)
    data.append('expectedBankType', importPreview.value.bankType)
    const result = await api<{ imported: number; skipped: number }>('/exam-questions/import', {
      method: 'POST',
      body: data
    })
    ElMessage.success(`已导入 ${result.imported} 道题目${result.skipped ? `，跳过 ${result.skipped} 行问题数据` : ''}`)
    importVisible.value = false
    page.value = 1
    await load()
  } catch (failure) {
    if (failure instanceof ApiError && failure.status === 409) {
      importPreview.value = null
      previewPage.value = 1
      removedInvalidRows.value = false
    }
    importError.value = messageOf(failure)
  } finally {
    importBusy.value = false
  }
}

function answerText(question: { options: string[] }, index: number): string {
  return `${optionLetters[index] || index + 1}. ${question.options[index] ?? ''}`
}

onMounted(load)
</script>

<template>
  <section class="page-heading">
    <div>
      <el-button link :icon="ArrowLeft" @click="emit('back')">返回题库列表</el-button>
      <h1>{{ bank.name }}</h1>
      <p>{{ examQuestionBankLabels[bank.type] }}</p>
    </div>
    <div class="heading-actions">
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      <el-button :icon="UploadFilled" @click="openImport">导入题目</el-button>
      <el-button type="primary" :icon="Plus" @click="openCreate">新建题目</el-button>
    </div>
  </section>

  <section class="surface">
    <div class="table-toolbar">
      <div class="table-controls">
        <el-input
          v-model="search"
          class="search-box"
          clearable
          placeholder="搜索题干或解析"
          :prefix-icon="Search"
          @keyup.enter="applyFilters"
          @clear="applyFilters"
        />
        <el-select v-model="typeFilter" class="table-filter" placeholder="全部题型" clearable @change="applyFilters">
          <el-option
            v-for="option in allowedQuestionTypes"
            :key="option.value"
            :label="option.label"
            :value="option.value"
          />
        </el-select>
        <el-button type="primary" :icon="Search" @click="applyFilters">查询</el-button>
        <el-button @click="resetFilters">重置</el-button>
        <el-button
          v-if="selected.length"
          type="danger"
          plain
          :icon="Delete"
          :loading="deleting"
          @click="removeSelected"
        >
          删除所选（{{ selected.length }}）
        </el-button>
      </div>
      <span class="record-count">共 {{ total }} 道题目</span>
    </div>

    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="table-error" />
    <el-table
      v-loading="loading"
      :data="items"
      row-key="id"
      empty-text="题库暂无题目"
      @selection-change="(rows: unknown) => (selected = rows as ExamQuestionSummary[])"
    >
      <el-table-column type="selection" width="46" />
      <el-table-column label="题型" width="96">
        <template #default="{ row }">
          <el-tag size="small" effect="plain">{{ examQuestionTypeLabel(row.type) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="题干" min-width="320">
        <template #default="{ row }">
          <strong class="question-stem">{{ row.stem }}</strong>
          <p v-if="row.analysis" class="question-analysis-preview">解析：{{ row.analysis }}</p>
        </template>
      </el-table-column>
      <el-table-column label="选项与答案" min-width="205">
        <template #default="{ row }">
          <span v-if="row.type === 'practical'" class="muted-text">{{
            row.automaticPractical ? '自动评分' : '人工评分'
          }}</span>
          <div v-else>
            <div v-for="(option, index) in row.options" :key="index" class="listed-option">
              {{ optionLetters[Number(index)] }}. {{ option }}
              <el-tag v-if="row.answers.includes(index)" size="small" type="success" effect="plain">正确</el-tag>
            </div>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="分值" width="80" align="center">
        <template #default="{ row }">{{ row.score }} 分</template>
      </el-table-column>
      <el-table-column label="引用" width="96" align="center">
        <template #default="{ row }">{{ row.paperCount }} 份试卷</template>
      </el-table-column>
      <el-table-column label="更新时间" width="168">
        <template #default="{ row }">{{ dateTime(row.updatedAt) }}</template>
      </el-table-column>
      <el-table-column label="操作" width="196" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" :icon="View" @click="openPreview(row as ExamQuestionSummary)">预览</el-button>
          <el-button link :icon="EditPen" @click="openEdit(row as ExamQuestionSummary)">编辑</el-button>
          <el-button link type="danger" :icon="Delete" @click="removeQuestion(row as ExamQuestionSummary)"
            >删除</el-button
          >
        </template>
      </el-table-column>
    </el-table>
    <div class="table-footer">
      <el-pagination
        v-model:current-page="page"
        v-model:page-size="pageSize"
        :total="total"
        :page-sizes="[10, 20, 50, 100]"
        layout="total, sizes, prev, pager, next"
        @current-change="load"
        @size-change="applyFilters"
      />
    </div>
  </section>

  <el-dialog
    v-model="dialogVisible"
    :title="editing ? '编辑题目' : '新建题目'"
    width="min(760px, 96vw)"
    :before-close="handleQuestionBeforeClose"
    destroy-on-close
    @opened="focusQuestionForm"
  >
    <el-alert v-if="formError" :title="formError" type="error" show-icon :closable="false" class="section-notice" />
    <el-form scroll-to-error ref="questionForm" v-loading="loadingPractical" label-position="top" class="entity-form">
      <div class="question-form-grid">
        <el-form-item label="题型" required>
          <el-select v-model="form.type" @change="changeType">
            <el-option
              v-for="option in allowedQuestionTypes"
              :key="option.value"
              :label="option.label"
              :value="option.value"
            />
          </el-select>
        </el-form-item>
        <el-form-item label="分值" required>
          <el-input-number v-model="form.score" :min="1" :max="100" :step="1" />
        </el-form-item>
      </div>
      <el-form-item v-if="editing" label="所属题库">
        <QuestionBankSelect v-model="form.bankId" :initial="bank" :question-type="form.type" placeholder="选择题库" />
      </el-form-item>
      <el-form-item label="题干" required>
        <el-input
          v-model="form.stem"
          type="textarea"
          :rows="3"
          maxlength="1000"
          show-word-limit
          placeholder="输入题目内容，实操题可直接描述任务要求"
        />
      </el-form-item>
      <template v-if="!isObjective">
        <el-form-item label="答题资源">
          <el-input
            v-model="form.practicalResources"
            type="textarea"
            :rows="4"
            maxlength="6000"
            show-word-limit
            placeholder="填写实操所需的参数、配置示例或参考材料"
          />
        </el-form-item>
        <el-form-item label="评分方式">
          <el-select v-model="form.practicalMode">
            <el-option label="模拟实操自动评分" value="automatic" />
            <el-option label="文字作答人工评分" value="manual" />
          </el-select>
        </el-form-item>
        <template v-if="form.practicalMode === 'automatic'">
          <el-form-item label="模拟环境节点" required>
            <el-select v-model="form.practicalNodeId" filterable placeholder="选择节点">
              <el-option v-for="node in nodes" :key="node.id" :label="node.name" :value="node.id" />
            </el-select>
          </el-form-item>
          <CaseRubricEditor v-model="form.practicalRubric" :node-id="form.practicalNodeId" />
          <p class="field-help">系统按命中的评分项比例折算题目得分。</p>
        </template>
      </template>
      <el-form-item v-if="isObjective" label="选项与答案" required>
        <div class="option-editor">
          <div v-for="(_, optionIndex) in form.options" :key="optionIndex" class="option-row">
            <span class="option-letter">{{ optionLetters[optionIndex] || optionIndex + 1 }}</span>
            <el-input
              v-model="form.options[optionIndex]"
              :maxlength="200"
              :placeholder="`选项 ${optionLetters[optionIndex] || optionIndex + 1}`"
            />
            <el-button
              link
              type="danger"
              :icon="Delete"
              :disabled="form.options.length <= 2"
              @click="removeOption(optionIndex)"
            />
          </div>
          <el-button link type="primary" :icon="Plus" :disabled="form.options.length >= 10" @click="addOption">
            添加选项
          </el-button>
        </div>
        <div class="answer-editor">
          <span class="option-editor-label">正确答案</span>
          <el-radio-group v-if="form.type !== 'multiple'" :model-value="form.answers[0]" @change="setSingleAnswer">
            <el-radio v-for="(_, optionIndex) in form.options" :key="optionIndex" :value="optionIndex">
              {{ answerText(form, optionIndex) }}
            </el-radio>
          </el-radio-group>
          <el-checkbox-group v-else v-model="form.answers">
            <el-checkbox v-for="(_, optionIndex) in form.options" :key="optionIndex" :value="optionIndex">
              {{ answerText(form, optionIndex) }}
            </el-checkbox>
          </el-checkbox-group>
        </div>
      </el-form-item>
      <el-alert
        v-else
        title="实操题不设置选项与标准答案，交卷后由教师在成绩管理中人工评分。"
        type="info"
        show-icon
        :closable="false"
        class="section-notice"
      />
      <el-form-item label="题目解析（可选）">
        <el-input
          v-model="form.analysis"
          type="textarea"
          :rows="3"
          maxlength="2000"
          show-word-limit
          placeholder="学生交卷后可在考试复盘中查看"
        />
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button @click="cancelQuestion">取消</el-button>
      <el-button type="primary" :loading="saving" @click="saveQuestion">
        {{ editing ? '保存修改' : '创建题目' }}
      </el-button>
    </template>
  </el-dialog>

  <el-drawer v-model="previewVisible" title="题目预览" size="min(620px, 96vw)">
    <div v-loading="previewLoading" class="question-preview">
      <template v-if="preview">
        <div class="preview-tags">
          <el-tag effect="plain">{{ examQuestionTypeLabel(preview.type) }}</el-tag>
          <el-tag type="warning" effect="plain">{{ preview.score }} 分</el-tag>
        </div>
        <h3>{{ preview.stem }}</h3>
        <div v-if="preview.options.length" class="preview-options">
          <div
            v-for="(option, index) in preview.options"
            :key="index"
            class="preview-option"
            :class="{ correct: preview.answers.includes(index) }"
          >
            <span>{{ optionLetters[index] || index + 1 }}</span>
            <p>{{ option }}</p>
            <el-icon v-if="preview.answers.includes(index)"><Document /></el-icon>
          </div>
        </div>
        <p v-else class="muted-text">实操题：由教师依据作答质量评分。</p>
        <div class="preview-block">
          <span>题目解析</span>
          <p>{{ preview.analysis || '暂无解析' }}</p>
        </div>
        <div class="preview-block">
          <span>引用试卷（{{ preview.papers.length }}）</span>
          <p v-if="!preview.papers.length" class="muted-text">尚未加入任何试卷</p>
          <div v-else class="preview-papers">
            <el-tag v-for="paper in preview.papers" :key="paper.id" effect="plain">{{ paper.name }}</el-tag>
          </div>
        </div>
      </template>
    </div>
  </el-drawer>

  <el-dialog
    v-model="importVisible"
    :title="`导入题目 · ${bank.name}`"
    width="min(900px, 96vw)"
    :close-on-click-modal="!importBusy"
    :close-on-press-escape="!importBusy"
    :show-close="!importBusy"
    @closed="resetImport"
    @opened="focusChooseFile"
  >
    <div class="import-hint">
      <p>支持 .xlsx 与 UTF-8 编码的 .csv，表头为：题型、题干、选项、正确答案、分值、解析。</p>
      <p>选项用竖线分隔（如“甲|乙|丙”），多选答案写 A,B 或直接写多个选项序号；判断题答案可写“正确 / 错误”。</p>
      <a class="template-link" href="/api/exam-questions/template" download>下载 CSV 导入模板</a>
    </div>
    <input ref="fileInput" class="hidden-file" type="file" accept=".xlsx,.csv" @change="chooseImportFile" />
    <div class="import-picker">
      <el-button ref="chooseFileButton" :icon="UploadFilled" :disabled="importBusy" @click="fileInput?.click()"
        >选择文件</el-button
      >
      <span>{{ importFileName || '尚未选择文件' }}</span>
    </div>
    <el-alert v-if="importError" :title="importError" type="error" show-icon :closable="false" class="section-notice" />
    <div v-if="importPreview" class="import-errors">
      <p>
        可导入 {{ importPreview.validCount }} 道
        <template v-if="importPreview.invalidCount">
          · {{ removedInvalidRows ? '已移除' : '待移除' }} {{ importPreview.invalidCount }} 行问题数据
        </template>
      </p>
      <el-table v-loading="importBusy" :data="importPreview.items" row-key="rowNumber" size="small" max-height="340">
        <el-table-column prop="rowNumber" label="行号" width="70" />
        <el-table-column label="题型" width="92">
          <template #default="{ row }">{{ examQuestionTypeLabel(row.type) }}</template>
        </el-table-column>
        <el-table-column prop="stem" label="题干" min-width="220" show-overflow-tooltip />
        <el-table-column prop="score" label="分值" width="64" />
        <el-table-column label="校验结果" min-width="210">
          <template #default="{ row }">
            <span v-if="!row.errors.length">通过</span>
            <ul v-else class="import-row-issues">
              <li v-for="(issue, index) in row.errors" :key="index">{{ issue.column }} · {{ issue.message }}</li>
            </ul>
          </template>
        </el-table-column>
      </el-table>
      <el-pagination
        v-if="importPreview.total > previewPageSize"
        v-model:current-page="previewPage"
        :page-size="previewPageSize"
        :total="importPreview.total"
        :disabled="importBusy"
        layout="total, prev, pager, next"
        @current-change="checkImport()"
      />
    </div>
    <template #footer>
      <el-button :disabled="importBusy" @click="importVisible = false">取消</el-button>
      <el-tooltip
        v-if="!importPreview"
        :disabled="!previewImportReason()"
        :content="previewImportReason()"
        placement="top"
      >
        <span class="disabled-action-wrap">
          <el-button type="primary" :disabled="!importFile" :loading="importBusy" @click="checkImport()">
            预览校验
          </el-button>
        </span>
      </el-tooltip>
      <el-button
        v-else-if="importPreview.invalidCount && !removedInvalidRows"
        type="warning"
        :loading="importBusy"
        @click="removeInvalidPreview"
      >
        一键移除问题数据
      </el-button>
      <el-tooltip v-else :disabled="!confirmImportReason()" :content="confirmImportReason()" placement="top">
        <span class="disabled-action-wrap">
          <el-button type="primary" :disabled="!importPreview.validCount" :loading="importBusy" @click="runImport">
            确认导入 {{ importPreview.validCount }} 道
          </el-button>
        </span>
      </el-tooltip>
    </template>
  </el-dialog>
</template>

<style scoped>
.listed-option {
  overflow-wrap: anywhere;
  margin-block: 2px;
}
.import-row-issues {
  margin: 0;
  padding-left: 16px;
}
.question-stem {
  display: block;
  color: #223f5c;
  font-size: 13px;
  line-height: 1.5;
  overflow-wrap: anywhere;
}

.question-analysis-preview {
  margin: 4px 0 0;
  color: #56697c;
  font-size: 12px;
  display: -webkit-box;
  overflow: hidden;
  -webkit-line-clamp: 1;
  -webkit-box-orient: vertical;
}

.muted-text {
  color: #56697c;
}

.question-form-grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 150px;
  gap: 0 14px;
}

.option-editor {
  display: grid;
  gap: 8px;
  width: 100%;
}

.option-row {
  display: grid;
  grid-template-columns: 24px minmax(0, 1fr) 28px;
  align-items: center;
  gap: 8px;
}

.option-letter {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border-radius: 50%;
  background: #e3f3ee;
  color: #145a50;
  font-size: 12px;
}

.option-editor-label {
  display: block;
  margin-bottom: 8px;
  color: #52687e;
  font-size: 13px;
}

.answer-editor {
  width: 100%;
  margin-top: 14px;
}

.answer-editor :deep(.el-radio),
.answer-editor :deep(.el-checkbox) {
  display: flex;
  margin-right: 0;
  margin-bottom: 4px;
}

.question-preview h3 {
  margin: 14px 0 18px;
  color: #1f3a55;
  font-size: 17px;
  line-height: 1.6;
}

.preview-tags {
  display: flex;
  gap: 8px;
}

.preview-options {
  display: grid;
  gap: 8px;
}

.preview-option {
  display: grid;
  grid-template-columns: 26px minmax(0, 1fr) 20px;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
  border: 1px solid #dce7eb;
  border-radius: 8px;
}

.preview-option.correct {
  border-color: #9fd0c7;
  background: #f1f9f6;
}

.preview-option > span {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border-radius: 50%;
  background: #e8f3ef;
  color: #145a50;
  font-size: 12px;
}

.preview-option p {
  margin: 0;
  color: #334f68;
  font-size: 13px;
  overflow-wrap: anywhere;
}

.preview-option .el-icon {
  color: #4b9b8e;
}

.preview-block {
  margin-top: 20px;
}

.preview-block > span {
  color: #56697c;
  font-size: 12px;
}

.preview-block > p {
  margin: 6px 0 0;
  color: #334f68;
  font-size: 13px;
  line-height: 1.7;
  white-space: pre-wrap;
}

.preview-papers {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 8px;
}

.import-hint p {
  margin: 0 0 6px;
  color: #56697c;
  font-size: 13px;
  line-height: 1.6;
}

.template-link {
  color: #3f7f7b;
  font-size: 13px;
}

.hidden-file {
  display: none;
}

.import-picker {
  display: flex;
  align-items: center;
  gap: 12px;
  margin: 14px 0;
  color: #56697c;
  font-size: 13px;
}

.import-errors {
  margin-top: 8px;
}

@media (max-width: 640px) {
  .question-form-grid {
    grid-template-columns: 1fr;
  }
}
</style>

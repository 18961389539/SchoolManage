<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { Delete, EditPen, Plus, Refresh, Search, View } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox, type TableInstance } from 'element-plus'
import { api, dateTime, del, messageOf, post, put } from '../api'
import { examQuestionTypeLabel, examQuestionTypeOptions, optionLetters, selectedText, typeCountText } from '../exam'
import type { ExamPaperDetail, ExamPaperQuestion, ExamPaperSummary, ExamQuestionSummary, Paged } from '../types'
import QuestionBankSelect from './QuestionBankSelect.vue'
import { focusFirstField } from '../focusField'

const items = ref<ExamPaperSummary[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)
const loading = ref(false)
const error = ref('')
const search = ref('')

const dialogVisible = ref(false)
const paperNameInput = ref<{ focus?: () => void; $el?: HTMLElement }>()
const pickerSearchInput = ref<{ $el?: HTMLElement }>()
function focusPaperName() {
  const root = paperNameInput.value?.$el
  if (root) focusFirstField(root)
  else paperNameInput.value?.focus?.()
}
function focusPickerSearch() {
  focusFirstField(pickerSearchInput.value?.$el)
}
const paperLockedReason = '该试卷已被考试引用，不能再调整题目或分值'
function addPaperQuestionReason(): string | undefined {
  if (paper.value && !paper.value.questionEditable) return paperLockedReason
}
function removePaperQuestionReason(): string | undefined {
  if (paper.value && !paper.value.questionEditable) return paperLockedReason
}
function addPickerReason(): string | undefined {
  if (!pickerSelection.value.length) return '请先勾选要加入试卷的题目'
}
const editing = ref<ExamPaperSummary | null>(null)
const saving = ref(false)
const formError = ref('')
const form = reactive({ name: '', description: '', remark: '' })

const editorVisible = ref(false)
const editorLoading = ref(false)
const paper = ref<ExamPaperDetail | null>(null)
const scoreBusy = ref('')
const removingId = ref('')
const paperFilter = ref('')
const paperSelection = ref<ExamPaperQuestion[]>([])
const paperTable = ref<TableInstance>()
const papersSelected = ref<ExamPaperSummary[]>([])
const paperRows = computed(() =>
  (paper.value?.questions || []).filter(item => !paperFilter.value || item.type === paperFilter.value)
)
const paperNumbers = computed(() => new Map((paper.value?.questions || []).map((item, index) => [item.id, index + 1])))

const pickerVisible = ref(false)
const pickerLoading = ref(false)
const pickerError = ref('')
const pickerItems = ref<ExamQuestionSummary[]>([])
const pickerTotal = ref(0)
const pickerPage = ref(1)
const pickerPageSize = ref(10)
const pickerSearch = ref('')
const pickerType = ref('')
const pickerBank = ref('')
const pickerSelection = ref<ExamQuestionSummary[]>([])
const adding = ref(false)
let pickerRequest = 0

const existingQuestionIds = computed(() => new Set((paper.value?.questions || []).map(item => item.questionId)))

async function load() {
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSize.value) })
    if (search.value.trim()) params.set('search', search.value.trim())
    const data = await api<Paged<ExamPaperSummary>>(`/exam-papers?${params.toString()}`)
    items.value = data.items
    total.value = data.total
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

function applyFilters() {
  page.value = 1
  void load()
}

function resetFilters() {
  search.value = ''
  applyFilters()
}

function openCreate() {
  editing.value = null
  form.name = ''
  form.description = ''
  form.remark = ''
  formError.value = ''
  dialogVisible.value = true
}

function openEdit(item: ExamPaperSummary) {
  editing.value = item
  form.name = item.name
  form.description = item.description
  form.remark = item.remark
  formError.value = ''
  dialogVisible.value = true
}

async function savePaper() {
  if (!form.name.trim()) {
    formError.value = '请填写试卷名称'
    return
  }
  saving.value = true
  formError.value = ''
  try {
    const payload = {
      name: form.name.trim(),
      description: form.description.trim(),
      remark: form.remark.trim()
    }
    if (editing.value) await put(`/exam-papers/${editing.value.id}`, payload)
    else await post('/exam-papers', payload)
    ElMessage.success(editing.value ? '试卷已更新' : '试卷已创建')
    dialogVisible.value = false
    await load()
  } catch (failure) {
    formError.value = messageOf(failure)
  } finally {
    saving.value = false
  }
}

async function removePaper(item: ExamPaperSummary) {
  try {
    await ElMessageBox.confirm(`确定删除试卷“${item.name}”吗？试卷中的题目不会被删除。`, '删除试卷', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消'
    })
    await del(`/exam-papers/${item.id}`)
    ElMessage.success('试卷已删除')
    if (items.value.length === 1 && page.value > 1) page.value--
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

async function openEditor(item: ExamPaperSummary) {
  paperFilter.value = ''
  paperSelection.value = []
  editorVisible.value = true
  await loadPaper(item.id)
}

async function loadPaper(id: string) {
  editorLoading.value = true
  try {
    paper.value = await api<ExamPaperDetail>(`/exam-papers/${id}`)
  } catch (failure) {
    ElMessage.error(messageOf(failure))
    editorVisible.value = false
  } finally {
    editorLoading.value = false
  }
}

async function updateScore(question: ExamPaperQuestion) {
  if (!paper.value || !paper.value.questionEditable) return
  scoreBusy.value = question.id
  try {
    await put(`/exam-papers/${paper.value.id}/questions/${question.id}`, { score: question.score })
    ElMessage.success('分值已调整')
    await loadPaper(paper.value.id)
    await load()
  } catch (failure) {
    ElMessage.error(messageOf(failure))
    await loadPaper(paper.value.id)
  } finally {
    scoreBusy.value = ''
  }
}

async function removeQuestion(question: ExamPaperQuestion) {
  await removePaperQuestions([question])
}

async function removePaperQuestions(questions: ExamPaperQuestion[]) {
  if (!paper.value?.questionEditable || !questions.length) return
  const paperId = paper.value.id
  try {
    await ElMessageBox.confirm(`确定从试卷中移除所选 ${questions.length} 道题目吗？`, '移除题目', {
      type: 'warning',
      confirmButtonText: '移除',
      cancelButtonText: '取消'
    })
    if (paper.value?.id !== paperId) return
    removingId.value = questions.length === 1 ? questions[0].id : 'selected'
    await post(`/exam-papers/${paperId}/questions/remove`, { paperQuestionIds: questions.map(item => item.id) })
    ElMessage.success('题目已移除')
    paperTable.value?.clearSelection()
    await loadPaper(paperId)
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  } finally {
    removingId.value = ''
  }
}

async function removeSelectedPapers() {
  const ids = papersSelected.value.map(item => item.id)
  if (!ids.length) return
  try {
    await ElMessageBox.confirm(`确定删除所选 ${ids.length} 份试卷吗？`, '批量删除试卷', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消'
    })
    await post('/exam-papers/batch-delete', { ids })
    ElMessage.success('试卷已删除')
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

function openPicker() {
  pickerVisible.value = true
  pickerSearch.value = ''
  pickerType.value = ''
  pickerBank.value = ''
  pickerPage.value = 1
  pickerSelection.value = []
  pickerError.value = ''
  void loadPicker()
}

async function loadPicker() {
  const request = ++pickerRequest
  pickerLoading.value = true
  pickerError.value = ''
  try {
    const params = new URLSearchParams({ page: String(pickerPage.value), pageSize: String(pickerPageSize.value) })
    if (pickerSearch.value.trim()) params.set('search', pickerSearch.value.trim())
    if (pickerType.value) params.set('type', pickerType.value)
    if (pickerBank.value) params.set('bankId', pickerBank.value)
    const data = await api<Paged<ExamQuestionSummary>>(`/exam-questions?${params.toString()}`)
    if (request !== pickerRequest) return
    pickerItems.value = data.items
    pickerTotal.value = data.total
  } catch (failure) {
    if (request === pickerRequest) pickerError.value = messageOf(failure)
  } finally {
    if (request === pickerRequest) pickerLoading.value = false
  }
}

function applyPickerFilters() {
  pickerPage.value = 1
  void loadPicker()
}

async function addSelected() {
  if (!paper.value || !pickerSelection.value.length) return
  adding.value = true
  try {
    const result = await post<{ added: number; skipped: number }>(`/exam-papers/${paper.value.id}/questions`, {
      items: pickerSelection.value.map(item => ({ questionId: item.id }))
    })
    ElMessage.success(`已添加 ${result.added} 道题目${result.skipped ? `，跳过 ${result.skipped} 道重复题` : ''}`)
    pickerVisible.value = false
    await loadPaper(paper.value.id)
    await load()
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    adding.value = false
  }
}

function answerOf(question: ExamPaperQuestion): string {
  return question.type === 'practical' ? '人工评分' : selectedText(question)
}

function optionText(question: { options: string[] }, index: number): string {
  return `${optionLetters[index] || index + 1}. ${question.options[index] ?? ''}`
}

onMounted(load)
</script>

<template>
  <section class="page-heading">
    <div>
      <h1>试卷管理</h1>
      <p>从题库挑选题目组成试卷并调整分值；重命名不影响已引用试卷的考试。</p>
    </div>
    <div class="heading-actions">
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      <el-button type="primary" :icon="Plus" @click="openCreate">新建试卷</el-button>
    </div>
  </section>

  <section class="surface">
    <div class="table-toolbar">
      <div class="table-controls">
        <el-input
          v-model="search"
          class="search-box"
          clearable
          placeholder="搜索试卷名称或简介"
          :prefix-icon="Search"
          @keyup.enter="applyFilters"
          @clear="applyFilters"
        />
        <el-button type="primary" :icon="Search" @click="applyFilters">查询</el-button>
        <el-button @click="resetFilters">重置</el-button>
      </div>
      <el-button type="danger" plain :disabled="!papersSelected.length" @click="removeSelectedPapers"
        >删除所选试卷</el-button
      >
      <span class="record-count">共 {{ total }} 份试卷</span>
    </div>

    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="table-error" />
    <el-table
      v-loading="loading"
      :data="items"
      row-key="id"
      empty-text="暂无试卷"
      @selection-change="rows => (papersSelected = rows)"
    >
      <el-table-column type="selection" width="45" />
      <el-table-column prop="name" label="试卷" min-width="220">
        <template #default="{ row }">
          <div class="table-name">
            <strong>{{ row.name }}</strong>
          </div>
          <p class="paper-description">{{ row.description || '暂无简介' }}</p>
        </template>
      </el-table-column>
      <el-table-column label="题目构成" min-width="220">
        <template #default="{ row }">
          <span class="paper-counts">{{ typeCountText(row.typeCounts) }}</span>
        </template>
      </el-table-column>
      <el-table-column label="题量 / 总分" width="130">
        <template #default="{ row }">{{ row.questionCount }} 题 · {{ row.totalScore }} 分</template>
      </el-table-column>
      <el-table-column label="使用考试" width="100" align="center">
        <template #default="{ row }">{{ row.examCount }} 场</template>
      </el-table-column>
      <el-table-column label="更新时间" width="168">
        <template #default="{ row }">{{ dateTime(row.updatedAt) }}</template>
      </el-table-column>
      <el-table-column label="操作" width="200" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" :icon="View" @click="openEditor(row as ExamPaperSummary)">组卷</el-button>
          <el-button link :icon="EditPen" @click="openEdit(row as ExamPaperSummary)">编辑</el-button>
          <el-button link type="danger" :icon="Delete" @click="removePaper(row as ExamPaperSummary)">删除</el-button>
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
    :title="editing ? '编辑试卷' : '新建试卷'"
    width="min(620px, 94vw)"
    @opened="focusPaperName"
  >
    <el-alert v-if="formError" :title="formError" type="error" show-icon :closable="false" class="section-notice" />
    <el-form scroll-to-error label-position="top" class="entity-form">
      <el-form-item label="试卷名称" required>
        <el-input
          ref="paperNameInput"
          v-model="form.name"
          maxlength="100"
          show-word-limit
          placeholder="例如：网络安全期中试卷"
        />
      </el-form-item>
      <el-form-item label="试卷简介">
        <el-input
          v-model="form.description"
          type="textarea"
          :rows="3"
          maxlength="2000"
          show-word-limit
          placeholder="考核范围、适用班级或命题说明"
        />
      </el-form-item>
      <el-form-item label="备注">
        <el-input v-model="form.remark" type="textarea" :rows="2" maxlength="2000" show-word-limit />
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button @click="dialogVisible = false">取消</el-button>
      <el-button type="primary" :loading="saving" @click="savePaper">
        {{ editing ? '保存修改' : '创建试卷' }}
      </el-button>
    </template>
  </el-dialog>

  <el-drawer
    v-model="editorVisible"
    :title="paper ? `组卷 · ${paper.name}` : '试卷配置'"
    size="min(1040px, 96vw)"
    @closed="paper = null"
  >
    <div v-loading="editorLoading" class="paper-editor">
      <template v-if="paper">
        <div class="paper-summary">
          <div>
            <span class="summary-label">总分</span>
            <strong>{{ paper.totalScore }}</strong>
          </div>
          <div>
            <span class="summary-label">题量</span>
            <strong>{{ paper.questionCount }}</strong>
          </div>
          <div class="paper-type-counts">{{ typeCountText(paper.typeCounts) }}</div>
          <el-tooltip :disabled="!addPaperQuestionReason()" :content="addPaperQuestionReason()" placement="bottom">
            <span class="disabled-action-wrap">
              <el-button type="primary" :icon="Plus" :disabled="!paper.questionEditable" @click="openPicker">
                添加题目
              </el-button>
            </span>
          </el-tooltip>
        </div>
        <el-alert
          v-if="!paper.questionEditable"
          title="该试卷已被考试引用，不能再调整题目或分值，以保证历史考试内容一致。"
          type="warning"
          show-icon
          :closable="false"
          class="section-notice"
        />
        <div class="table-toolbar">
          <el-select
            v-model="paperFilter"
            placeholder="全部题型"
            aria-label="筛选试卷题型"
            clearable
            class="table-filter"
          >
            <el-option
              v-for="type in examQuestionTypeOptions"
              :key="type.value"
              :label="type.label"
              :value="type.value"
            />
          </el-select>
          <el-button
            v-if="paper.questionEditable"
            type="danger"
            plain
            :disabled="!paperSelection.length || !!removingId"
            @click="removePaperQuestions(paperSelection)"
            >移除所选题目</el-button
          >
        </div>
        <el-table
          ref="paperTable"
          :data="paperRows"
          row-key="id"
          empty-text="没有符合条件的题目"
          @selection-change="rows => (paperSelection = rows)"
        >
          <el-table-column v-if="paper.questionEditable" type="selection" width="45" />
          <el-table-column label="#" width="54">
            <template #default="{ row }">{{ paperNumbers.get(row.id) }}</template>
          </el-table-column>
          <el-table-column label="题型" width="90">
            <template #default="{ row }">
              <el-tag size="small" effect="plain">{{ examQuestionTypeLabel(row.type) }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column label="题干" min-width="320">
            <template #default="{ row }">
              <strong class="paper-question-stem">{{ row.stem }}</strong>
              <p v-if="row.options.length" class="paper-option-line">
                {{
                  row.options
                    .map((option: string, index: number) => optionText(row as ExamPaperQuestion, index))
                    .join('　')
                }}
              </p>
              <p class="paper-answer-line">答案：{{ answerOf(row as ExamPaperQuestion) }}</p>
            </template>
          </el-table-column>
          <el-table-column label="分值" width="140">
            <template #default="{ row }">
              <el-input-number
                v-model="row.score"
                :min="1"
                :max="100"
                size="small"
                :disabled="!paper.questionEditable || scoreBusy === row.id"
                @change="updateScore(row as ExamPaperQuestion)"
              />
            </template>
          </el-table-column>
          <el-table-column label="操作" width="90">
            <template #default="{ row }">
              <el-tooltip
                :disabled="!removePaperQuestionReason()"
                :content="removePaperQuestionReason()"
                placement="left"
              >
                <span class="disabled-action-wrap">
                  <el-button
                    link
                    type="danger"
                    :icon="Delete"
                    :disabled="!paper.questionEditable"
                    :loading="removingId === row.id"
                    @click="removeQuestion(row as ExamPaperQuestion)"
                  >
                    移除
                  </el-button>
                </span>
              </el-tooltip>
            </template>
          </el-table-column>
        </el-table>
        <div v-if="paper.exams.length" class="paper-exams">
          <span>引用考试：</span>
          <el-tag v-for="exam in paper.exams" :key="exam.id" effect="plain">{{ exam.name }}</el-tag>
        </div>
      </template>
    </div>
  </el-drawer>

  <el-dialog
    v-model="pickerVisible"
    title="从题库添加题目"
    width="min(980px, 96vw)"
    append-to-body
    destroy-on-close
    @opened="focusPickerSearch"
  >
    <div class="table-toolbar picker-toolbar">
      <div class="table-controls">
        <el-input
          ref="pickerSearchInput"
          v-model="pickerSearch"
          class="search-box"
          clearable
          placeholder="搜索题干"
          :prefix-icon="Search"
          @keyup.enter="applyPickerFilters"
          @clear="applyPickerFilters"
        />
        <QuestionBankSelect
          v-model="pickerBank"
          class="table-filter"
          placeholder="全部题库"
          clearable
          @change="applyPickerFilters"
        />
        <el-select
          v-model="pickerType"
          class="table-filter"
          placeholder="全部题型"
          clearable
          @change="applyPickerFilters"
        >
          <el-option
            v-for="option in examQuestionTypeOptions"
            :key="option.value"
            :label="option.label"
            :value="option.value"
          />
        </el-select>
        <el-button type="primary" :icon="Search" @click="applyPickerFilters">查询</el-button>
      </div>
      <span class="record-count">已选 {{ pickerSelection.length }} 道</span>
    </div>
    <el-alert v-if="pickerError" :title="pickerError" type="error" show-icon :closable="false" class="section-notice" />
    <el-table
      v-loading="pickerLoading"
      :data="pickerItems"
      row-key="id"
      max-height="380"
      empty-text="没有可添加的题目"
      @selection-change="(rows: unknown) => (pickerSelection = rows as ExamQuestionSummary[])"
    >
      <el-table-column
        type="selection"
        width="46"
        reserve-selection
        :selectable="(row: ExamQuestionSummary) => !existingQuestionIds.has(row.id)"
      />
      <el-table-column label="题型" width="90">
        <template #default="{ row }">
          <el-tag size="small" effect="plain">{{ examQuestionTypeLabel(row.type) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="bankName" label="所属题库" min-width="130" show-overflow-tooltip />
      <el-table-column label="题干" min-width="320">
        <template #default="{ row }">
          <strong class="paper-question-stem">{{ row.stem }}</strong>
          <p v-if="existingQuestionIds.has(row.id)" class="picker-existing">已在试卷中</p>
        </template>
      </el-table-column>
      <el-table-column label="分值" width="80" align="center">
        <template #default="{ row }">{{ row.score }} 分</template>
      </el-table-column>
    </el-table>
    <div class="table-footer">
      <el-pagination
        v-model:current-page="pickerPage"
        v-model:page-size="pickerPageSize"
        :total="pickerTotal"
        :page-sizes="[10, 20, 50]"
        layout="total, sizes, prev, pager, next"
        @current-change="loadPicker"
        @size-change="applyPickerFilters"
      />
    </div>
    <template #footer>
      <el-button @click="pickerVisible = false">取消</el-button>
      <el-tooltip :disabled="!addPickerReason()" :content="addPickerReason()" placement="top">
        <span class="disabled-action-wrap">
          <el-button type="primary" :loading="adding" :disabled="!pickerSelection.length" @click="addSelected">
            加入试卷（{{ pickerSelection.length }}）
          </el-button>
        </span>
      </el-tooltip>
    </template>
  </el-dialog>
</template>

<style scoped>
.paper-description {
  margin: 4px 0 0;
  color: #5d6f80;
  font-size: 12px;
  line-height: 1.45;
  display: -webkit-box;
  overflow: hidden;
  -webkit-line-clamp: 1;
  -webkit-box-orient: vertical;
}

.paper-counts {
  color: #51687f;
  font-size: 13px;
  line-height: 1.5;
}

.paper-summary {
  display: flex;
  align-items: center;
  gap: 28px;
  padding: 16px 18px;
  margin-bottom: 16px;
  background: #f3f8f6;
  border: 1px solid #e4eeea;
  border-radius: 10px;
}

.paper-summary > div:not(.paper-type-counts) {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.summary-label {
  color: #56697c;
  font-size: 12px;
}

.paper-summary strong {
  color: #223f5c;
  font-size: 24px;
}

.paper-type-counts {
  flex: 1;
  color: #5b7187;
  font-size: 13px;
}

.paper-question-stem {
  display: block;
  color: #223f5c;
  font-size: 13px;
  line-height: 1.5;
}

.paper-option-line {
  margin: 4px 0 0;
  color: #56697c;
  font-size: 12px;
  line-height: 1.5;
}

.paper-answer-line {
  margin: 4px 0 0;
  color: #12735f;
  font-size: 12px;
}

.paper-exams {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-top: 18px;
  color: #56697c;
  font-size: 12px;
}

.picker-toolbar {
  padding-top: 0;
}

.picker-existing {
  margin: 4px 0 0;
  color: #94610f;
  font-size: 12px;
}
</style>

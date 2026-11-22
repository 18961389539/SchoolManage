<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { CircleClose, EditPen, MoreFilled, Plus, Refresh, Search, Tickets, VideoPlay } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, del, messageOf, post, put } from '../api'
import { rangeText } from '../time'
import { examStatusLabel, examStatusOptions, examStatusType } from '../exam'
import { durationText } from '../teaching'
import { shanghaiInputTime, shanghaiInputToIso } from '../time'
import type { ExamPaperSummary, Paged, TeachingClassesResponse, TrainingExamSummary } from '../types'
import ExamAnalysisDrawer from './ExamAnalysisDrawer.vue'
import ExamMonitorDrawer from './ExamMonitorDrawer.vue'
import ExamRosterDrawer from './ExamRosterDrawer.vue'
import ExamScoresDrawer from './ExamScoresDrawer.vue'
import ExamStatisticsDrawer from './ExamStatisticsDrawer.vue'
import TableEmpty from './TableEmpty.vue'
import { focusFirstField } from '../focusField'

const props = defineProps<{ admin: boolean }>()

const items = ref<TrainingExamSummary[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)
const loading = ref(false)
const error = ref('')
const search = ref('')
const statusFilter = ref('')
const tableViewport = window.matchMedia('(max-width: 1280px)')
const narrowTable = ref(tableViewport.matches)
function syncTableViewport(event: MediaQueryListEvent) {
  narrowTable.value = event.matches
}
const papers = ref<ExamPaperSummary[]>([])
const teaching = ref<TeachingClassesResponse>({ professions: [], classes: [] })

const dialogVisible = ref(false)
const nameInput = ref<{ focus?: () => void; $el?: HTMLElement }>()
function focusNameInput() {
  const root = nameInput.value?.$el
  if (root) focusFirstField(root)
  else nameInput.value?.focus?.()
}
const editing = ref<TrainingExamSummary | null>(null)
const saving = ref(false)
const formError = ref('')
const form = reactive({
  name: '',
  paperId: '',
  description: '',
  startAt: '',
  endAt: '',
  durationMinutes: 60,
  classIds: [] as string[],
  professionIds: [] as string[]
})

const activeExam = ref<TrainingExamSummary | null>(null)
const rosterVisible = ref(false)
const scoresVisible = ref(false)
const monitorVisible = ref(false)
const statsVisible = ref(false)
const analysisVisible = ref(false)

function canEdit(item: TrainingExamSummary): boolean {
  return item.storedStatus === 'scheduled' && item.attemptCount === 0
}

function examEditReason(item: TrainingExamSummary): string | undefined {
  if (canEdit(item)) return undefined
  if (item.attemptCount > 0) return '已有作答记录的考试不能编辑'
  return '开考后考试信息已锁定，不能编辑'
}
function examDeleteReason(item: TrainingExamSummary): string | undefined {
  if (item.attemptCount > 0) return '已有作答记录的考试不能删除'
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSize.value) })
    if (search.value.trim()) params.set('search', search.value.trim())
    if (statusFilter.value) params.set('status', statusFilter.value)
    const data = await api<Paged<TrainingExamSummary>>(`/exams?${params.toString()}`)
    items.value = data.items
    total.value = data.total
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

async function loadOptions() {
  try {
    const [paperData, teachingData] = await Promise.all([
      api<Paged<ExamPaperSummary>>('/exam-papers?page=1&pageSize=1000'),
      api<TeachingClassesResponse>('/teaching/classes')
    ])
    papers.value = paperData.items
    teaching.value = teachingData
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  }
}

function applyFilters() {
  page.value = 1
  void load()
}

function resetFilters() {
  search.value = ''
  statusFilter.value = ''
  applyFilters()
}

const filtering = computed(() => !!search.value.trim() || !!statusFilter.value)

function defaultWindow() {
  const now = new Date()
  return {
    startAt: shanghaiInputTime(new Date(now.getTime() + 10 * 60 * 1000).toISOString()).slice(0, 16),
    endAt: shanghaiInputTime(new Date(now.getTime() + 130 * 60 * 1000).toISOString()).slice(0, 16)
  }
}

function openCreate() {
  if (!papers.value.length) {
    ElMessage.warning('请先在试卷管理中创建含题目的试卷')
    return
  }
  const window = defaultWindow()
  editing.value = null
  form.name = ''
  form.paperId = papers.value[0]?.id || ''
  form.description = ''
  form.startAt = window.startAt
  form.endAt = window.endAt
  form.durationMinutes = 60
  form.classIds = []
  form.professionIds = []
  formError.value = ''
  dialogVisible.value = true
}

function openEdit(item: TrainingExamSummary) {
  editing.value = item
  if (!papers.value.some(paper => paper.id === item.paperId)) {
    papers.value = [
      {
        id: item.paperId,
        name: item.paperName,
        description: '',
        remark: '',
        createdAt: item.createdAt,
        updatedAt: item.createdAt,
        questionCount: item.questionCount,
        totalScore: item.totalScore,
        typeCounts: {},
        examCount: 0
      },
      ...papers.value
    ]
  }
  form.name = item.name
  form.paperId = item.paperId
  form.description = item.description
  form.startAt = shanghaiInputTime(item.startAt).slice(0, 16)
  form.endAt = shanghaiInputTime(item.endAt).slice(0, 16)
  form.durationMinutes = item.durationMinutes
  form.classIds = []
  form.professionIds = []
  formError.value = ''
  dialogVisible.value = true
}

async function saveExam() {
  if (!form.name.trim()) {
    formError.value = '请填写考试名称'
    return
  }
  if (!form.paperId) {
    formError.value = '请选择试卷'
    return
  }
  const startAt = shanghaiInputToIso(form.startAt)
  const endAt = shanghaiInputToIso(form.endAt)
  if (!startAt || !endAt) {
    formError.value = '请选择有效的开始和结束时间'
    return
  }
  if (new Date(endAt) <= new Date(startAt)) {
    formError.value = '结束时间必须晚于开始时间'
    return
  }
  if (form.durationMinutes < 1 || form.durationMinutes > 600) {
    formError.value = '考试时长应在 1–600 分钟之间'
    return
  }
  saving.value = true
  formError.value = ''
  try {
    const payload = {
      name: form.name.trim(),
      paperId: form.paperId,
      description: form.description.trim(),
      startAt,
      endAt,
      durationMinutes: form.durationMinutes,
      classIds: editing.value ? [] : form.classIds,
      professionIds: editing.value ? [] : form.professionIds
    }
    if (editing.value) await put(`/exams/${editing.value.id}`, payload)
    else await post('/exams', payload)
    ElMessage.success(editing.value ? '考试已更新' : '考试已创建')
    dialogVisible.value = false
    await load()
  } catch (failure) {
    formError.value = messageOf(failure)
  } finally {
    saving.value = false
  }
}

async function toggleExam(item: TrainingExamSummary) {
  try {
    if (item.storedStatus === 'open') {
      await ElMessageBox.confirm('结考后，进行中的答卷将自动提交。已出分的考生可以查看答题回顾。', '结束考试', {
        type: 'warning',
        confirmButtonText: '结考',
        cancelButtonText: '取消'
      })
      await post(`/exams/${item.id}/close`)
      ElMessage.success('考试已结束')
    } else if (item.status === 'scheduled') {
      await ElMessageBox.confirm('确认后锁定考试信息，考生可在设定的开始时间后进入。', '开始考试', {
        type: 'warning',
        confirmButtonText: '开考',
        cancelButtonText: '取消'
      })
      await post(`/exams/${item.id}/open`)
      ElMessage.success(Date.parse(item.startAt) > Date.now() ? '考试将在设定时间开放' : '考试已开始')
    }
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

async function removeExam(item: TrainingExamSummary) {
  try {
    await ElMessageBox.confirm(`确定删除考试“${item.name}”吗？已有作答记录的考试不能删除。`, '删除考试', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消'
    })
    await del(`/exams/${item.id}`)
    ElMessage.success('考试已删除')
    if (items.value.length === 1 && page.value > 1) page.value--
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

function openRoster(item: TrainingExamSummary) {
  activeExam.value = item
  rosterVisible.value = true
}

function openScores(item: TrainingExamSummary) {
  activeExam.value = item
  scoresVisible.value = true
}

function handleCommand(command: string, item: TrainingExamSummary) {
  activeExam.value = item
  if (command === 'roster') rosterVisible.value = true
  else if (command === 'scores') scoresVisible.value = true
  else if (command === 'monitor') monitorVisible.value = true
  else if (command === 'statistics') statsVisible.value = true
  else if (command === 'analysis') analysisVisible.value = true
  else if (command === 'edit') {
    if (!canEdit(item)) return
    openEdit(item)
  } else if (command === 'delete') {
    if (item.attemptCount > 0) return
    void removeExam(item)
  }
}

async function onChanged() {
  await load()
}

onMounted(() => {
  tableViewport.addEventListener('change', syncTableViewport)
  void load()
  void loadOptions()
})

onBeforeUnmount(() => tableViewport.removeEventListener('change', syncTableViewport))
</script>

<template>
  <section class="page-heading management-header">
    <div>
      <h1>考试管理</h1>
      <p>
        {{ props.admin ? '管理全校考试安排、名单、成绩与统计分析。' : '管理你创建的考试，分配学生并完成成绩批阅。' }}
      </p>
    </div>
    <div class="heading-actions">
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      <el-tooltip :disabled="papers.length > 0" content="请先在试卷管理中创建含题目的试卷" placement="bottom">
        <span class="disabled-action-wrap">
          <el-button type="primary" :icon="Plus" :disabled="!papers.length" @click="openCreate">新建考试</el-button>
        </span>
      </el-tooltip>
    </div>
  </section>

  <el-alert
    v-if="!papers.length && !loading"
    title="尚无可用试卷，请先在试卷管理中创建含题目的试卷后再新建考试。"
    type="info"
    show-icon
    :closable="false"
    class="section-notice"
  />

  <section class="surface management-table">
    <div class="table-toolbar">
      <div class="table-controls">
        <el-input
          v-model="search"
          class="search-box"
          clearable
          placeholder="搜索考试名称或说明"
          :prefix-icon="Search"
          @keyup.enter="applyFilters"
          @clear="applyFilters"
        />
        <el-select v-model="statusFilter" class="table-filter" placeholder="全部状态" clearable @change="applyFilters">
          <el-option
            v-for="option in examStatusOptions"
            :key="option.value"
            :label="option.label"
            :value="option.value"
          />
        </el-select>
        <el-button type="primary" :icon="Search" @click="applyFilters">查询</el-button>
        <el-button @click="resetFilters">重置</el-button>
      </div>
      <span class="record-count">{{ loading && !total ? '正在加载考试…' : '共 ' + total + ' 场考试' }}</span>
    </div>

    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="table-error" />
    <p v-if="narrowTable && items.length" class="table-scroll-hint">横向滚动可查看更多信息和操作</p>
    <el-table
      empty-text=""
      v-loading="loading"
      :data="items"
      row-key="id"
      :scrollbar-tabindex="narrowTable && items.length ? 0 : undefined"
      :scrollbar-always-on="narrowTable"
    >
      <template #empty>
        <TableEmpty
          v-if="!loading"
          :title="error ? '考试加载失败' : filtering ? '没有符合条件的考试' : '暂无考试'"
          :hint="
            error
              ? '请检查服务状态后刷新重试'
              : filtering
                ? '试试调整关键词或状态筛选，也可清除后查看全部考试。'
                : papers.length
                  ? '点击右上角「新建考试」，从试卷创建并分配学生。'
                  : '请先在试卷管理中创建含题目的试卷，再来新建考试。'
          "
        >
          <template v-if="!error && filtering" #action>
            <el-button type="primary" plain @click="resetFilters">清除筛选</el-button>
          </template>
        </TableEmpty>
      </template>
      <el-table-column label="考试" min-width="230">
        <template #default="{ row }">
          <div class="table-name">
            <strong>{{ row.name }}</strong>
            <el-tag :type="examStatusType(row.status)" size="small">{{ examStatusLabel(row.status) }}</el-tag>
          </div>
          <p class="exam-description">{{ row.description || '暂无说明' }}</p>
        </template>
      </el-table-column>
      <el-table-column prop="paperName" label="试卷" min-width="170">
        <template #default="{ row }">
          <span>{{ row.paperName }}</span>
          <p class="exam-sub">{{ row.questionCount }} 题 · {{ row.totalScore }} 分</p>
        </template>
      </el-table-column>
      <el-table-column label="考试时间" min-width="175">
        <template #default="{ row }">
          <span class="exam-time">{{ rangeText(row.startAt, row.endAt) }}</span>
          <p class="exam-sub">限时 {{ durationText(row.durationMinutes) }}</p>
        </template>
      </el-table-column>
      <el-table-column label="参考情况" min-width="190">
        <template #default="{ row }">
          <span class="exam-counts">
            应考 {{ row.studentCount }} · 开考 {{ row.attemptCount }}<br />
            提交 {{ row.submittedCount }} · 已阅 {{ row.gradedCount }}
          </span>
        </template>
      </el-table-column>
      <el-table-column label="操作" :width="narrowTable ? 210 : 290" :fixed="narrowTable ? false : 'right'">
        <template #default="{ row }">
          <div class="management-actions">
            <el-button
              v-if="row.status !== 'closed'"
              :type="row.storedStatus === 'scheduled' ? 'primary' : 'warning'"
              plain
              size="small"
              :icon="row.storedStatus === 'scheduled' ? VideoPlay : CircleClose"
              @click="toggleExam(row as TrainingExamSummary)"
            >
              {{ row.storedStatus === 'scheduled' ? '开考' : '结考' }}
            </el-button>
            <el-button link type="primary" :icon="Tickets" @click="openRoster(row as TrainingExamSummary)"
              >名单</el-button
            >
            <el-button link :icon="EditPen" @click="openScores(row as TrainingExamSummary)">成绩</el-button>
            <el-dropdown
              trigger="click"
              @command="(command: string) => handleCommand(command, row as TrainingExamSummary)"
            >
              <el-button link :icon="MoreFilled">更多</el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item command="monitor">考试监控</el-dropdown-item>
                  <el-dropdown-item command="statistics">统计分析</el-dropdown-item>
                  <el-dropdown-item command="analysis">试题分析</el-dropdown-item>
                  <el-dropdown-item
                    command="edit"
                    :disabled="!canEdit(row as TrainingExamSummary)"
                    :title="examEditReason(row as TrainingExamSummary)"
                    :class="{ 'has-disabled-reason': !canEdit(row as TrainingExamSummary) }"
                    divided
                  >
                    <el-tooltip
                      :disabled="canEdit(row as TrainingExamSummary)"
                      :content="examEditReason(row as TrainingExamSummary)"
                      placement="left"
                    >
                      <span>编辑考试</span>
                    </el-tooltip>
                  </el-dropdown-item>
                  <el-dropdown-item
                    command="delete"
                    :disabled="(row as TrainingExamSummary).attemptCount > 0"
                    :title="examDeleteReason(row as TrainingExamSummary)"
                    :class="{ 'has-disabled-reason': (row as TrainingExamSummary).attemptCount > 0 }"
                  >
                    <el-tooltip
                      :disabled="!(row as TrainingExamSummary).attemptCount"
                      :content="examDeleteReason(row as TrainingExamSummary)"
                      placement="left"
                    >
                      <span>删除考试</span>
                    </el-tooltip>
                  </el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </div>
        </template>
      </el-table-column>
    </el-table>
    <div class="table-footer">
      <el-pagination
        v-model:current-page="page"
        v-model:page-size="pageSize"
        :total="total"
        :page-sizes="[10, 20, 50, 100]"
        :layout="loading && !total ? 'prev, pager, next' : 'total, sizes, prev, pager, next'"
        @current-change="load"
        @size-change="applyFilters"
      />
    </div>
  </section>

  <el-dialog
    v-model="dialogVisible"
    :title="editing ? '编辑考试' : '新建考试'"
    width="min(720px, 96vw)"
    @opened="focusNameInput"
  >
    <el-alert v-if="formError" :title="formError" type="error" show-icon :closable="false" class="section-notice" />
    <el-form scroll-to-error label-position="top" class="entity-form">
      <div class="exam-form-grid">
        <el-form-item label="考试名称" required>
          <el-input
            ref="nameInput"
            v-model="form.name"
            maxlength="100"
            show-word-limit
            placeholder="例如：网络安全期中考试"
          />
        </el-form-item>
        <el-form-item label="使用试卷" required>
          <el-select v-model="form.paperId" filterable placeholder="选择试卷">
            <el-option
              v-for="paper in papers"
              :key="paper.id"
              :label="`${paper.name}（${paper.questionCount} 题 / ${paper.totalScore} 分）`"
              :value="paper.id"
            />
          </el-select>
        </el-form-item>
      </div>
      <div class="exam-form-grid three">
        <el-form-item label="开始时间" required>
          <el-date-picker
            v-model="form.startAt"
            type="datetime"
            value-format="YYYY-MM-DDTHH:mm"
            placeholder="选择开始时间"
            style="width: 100%"
          />
        </el-form-item>
        <el-form-item label="结束时间" required>
          <el-date-picker
            v-model="form.endAt"
            type="datetime"
            value-format="YYYY-MM-DDTHH:mm"
            placeholder="选择结束时间"
            style="width: 100%"
          />
        </el-form-item>
        <el-form-item label="考试时长（分钟）" required>
          <el-input-number v-model="form.durationMinutes" :min="1" :max="600" :step="10" style="width: 100%" />
        </el-form-item>
      </div>
      <el-form-item label="考试说明">
        <el-input
          v-model="form.description"
          type="textarea"
          :rows="3"
          maxlength="2000"
          show-word-limit
          placeholder="考试范围、纪律要求或注意事项"
        />
      </el-form-item>
      <template v-if="!editing">
        <div class="exam-form-grid">
          <el-form-item label="按班级分配学生">
            <el-select v-model="form.classIds" multiple filterable collapse-tags placeholder="选择班级">
              <el-option
                v-for="item in teaching.classes"
                :key="item.id"
                :label="`${item.name} · ${item.studentCount} 人`"
                :value="item.id"
              />
            </el-select>
          </el-form-item>
          <el-form-item label="按专业分配学生">
            <el-select v-model="form.professionIds" multiple filterable collapse-tags placeholder="选择专业">
              <el-option v-for="item in teaching.professions" :key="item.id" :label="item.name" :value="item.id" />
            </el-select>
          </el-form-item>
        </div>
        <p class="field-help">创建后可在“名单”中继续添加或移除学生；开考后名单锁定。</p>
      </template>
      <el-alert
        v-else
        title="编辑考试不会调整学生名单；如需增减考生，请使用名单管理。开考后考试信息锁定。"
        type="info"
        show-icon
        :closable="false"
        class="section-notice"
      />
    </el-form>
    <template #footer>
      <el-button @click="dialogVisible = false">取消</el-button>
      <el-button type="primary" :loading="saving" @click="saveExam">
        {{ editing ? '保存修改' : '创建考试' }}
      </el-button>
    </template>
  </el-dialog>

  <ExamRosterDrawer
    v-model="rosterVisible"
    :exam-id="activeExam?.id || ''"
    :exam-name="activeExam?.name || ''"
    :editable="!!activeExam && activeExam.status === 'scheduled' && activeExam.attemptCount === 0"
    :deployable="activeExam?.status !== 'closed'"
    @changed="onChanged"
  />
  <ExamScoresDrawer
    v-model="scoresVisible"
    :exam-id="activeExam?.id || ''"
    :exam-name="activeExam?.name || ''"
    @changed="onChanged"
  />
  <ExamMonitorDrawer v-model="monitorVisible" :exam-id="activeExam?.id || ''" :exam-name="activeExam?.name || ''" />
  <ExamStatisticsDrawer v-model="statsVisible" :exam-id="activeExam?.id || ''" :exam-name="activeExam?.name || ''" />
  <ExamAnalysisDrawer v-model="analysisVisible" :exam-id="activeExam?.id || ''" :exam-name="activeExam?.name || ''" />
</template>

<style scoped>
.exam-description {
  margin: 4px 0 0;
  color: #5d6f80;
  font-size: 12px;
  line-height: 1.45;
  display: -webkit-box;
  overflow: hidden;
  -webkit-line-clamp: 1;
  -webkit-box-orient: vertical;
}

.exam-sub {
  margin: 3px 0 0;
  color: #56697c;
  font-size: 12px;
  line-height: 1.4;
}

.exam-time {
  display: block;
  color: #52687e;
  font-size: 12px;
  line-height: 1.5;
}

.exam-counts {
  color: #51687f;
  font-size: 12px;
  line-height: 1.6;
}

.exam-form-grid {
  display: grid;
  grid-template-columns: minmax(0, 1.3fr) minmax(0, 1fr);
  gap: 0 14px;
}

.exam-form-grid.three {
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) 160px;
}

@media (max-width: 720px) {
  .exam-form-grid,
  .exam-form-grid.three {
    grid-template-columns: 1fr;
  }
}
</style>

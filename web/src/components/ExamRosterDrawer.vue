<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage, ElMessageBox, type TableInstance } from 'element-plus'
import { Delete, Plus, Refresh, Search } from '@element-plus/icons-vue'
import { api, dateTime, messageOf, post } from '../api'
import { attemptStatusLabel, attemptStatusType, deploymentLabel, deploymentType } from '../exam'
import type { ExamRosterStudent, ExamStudentOption } from '../types'
import ExamDeploymentDialog from './ExamDeploymentDialog.vue'
import TableEmpty from './TableEmpty.vue'
import { focusFirstField } from '../focusField'

const props = defineProps<{
  modelValue: boolean
  examId: string
  examName: string
  editable: boolean
  deployable: boolean
}>()

const addStudentSelect = ref<{ $el?: HTMLElement }>()
function focusAddStudent() {
  focusFirstField(addStudentSelect.value?.$el)
}
function deployAllReason(): string | undefined {
  if (!props.deployable) return '考试已结束，不能再部署环境'
  if (!items.value.length) return '请先添加考生后再部署环境'
  if (rowDeploying.value) return '正在部署个别考生环境，请稍后再试'
}
function deploySelectedReason(): string | undefined {
  if (!props.deployable) return '考试已结束，不能再部署环境'
  if (!selection.value.length) return '请先勾选要部署的学生'
  if (deploying.value) return '正在部署环境，请稍后再试'
}
function removeStudentsReason(): string | undefined {
  if (!selection.value.length) return '请先勾选要移除的学生'
}
function addStudentsReason(): string | undefined {
  if (!addSelection.value.length) return '请先搜索并选择要加入的学生'
}
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; changed: [] }>()

const visible = computed({
  get: () => props.modelValue,
  set: value => emit('update:modelValue', value)
})

const items = ref<ExamRosterStudent[]>([])
const loading = ref(false)
const error = ref('')
const search = ref('')
const deploying = ref(false)
const removingId = ref('')
const rowDeploying = ref('')
const projectCount = ref(0)
const selection = ref<ExamRosterStudent[]>([])
const rosterTable = ref<TableInstance>()
const deploymentStudent = ref<ExamRosterStudent | null>(null)
const deploymentVisible = ref(false)
const autoRefresh = ref(true)

const addVisible = ref(false)
const adding = ref(false)
const options = ref<ExamStudentOption[]>([])
const optionsLoading = ref(false)
const addSelection = ref<string[]>([])

const filtered = computed(() => {
  const keyword = search.value.trim().toLowerCase()
  if (!keyword) return items.value
  return items.value.filter(item =>
    [item.name, item.userName, item.number, item.className].some(value => value.toLowerCase().includes(keyword))
  )
})
const deployedCount = computed(() => items.value.filter(item => item.deploymentStatus === 'deployed').length)
const searching = computed(() => search.value.trim().length > 0)

function rosterEmptyTitle() {
  if (error.value) return '名单加载失败'
  if (searching.value) return '没有符合条件的学生'
  return '名单中还没有学生'
}

function rosterEmptyHint() {
  if (error.value) return '请稍后刷新重试。'
  if (searching.value) return '试试调整关键词，或清除搜索查看全部名单。'
  return props.editable ? '点击“添加学生”，将应考学生加入本场考试。' : '应考学生加入后会显示在这里。'
}

async function load(background = false) {
  const id = props.examId
  if (!id || (background && (loading.value || deploying.value || rowDeploying.value))) return
  if (!background) loading.value = true
  error.value = ''
  try {
    const data = await api<{ items: ExamRosterStudent[]; practicalProjectCount: number }>(`/exams/${id}/students`)
    if (id !== props.examId) return
    items.value = data.items
    projectCount.value = data.practicalProjectCount
  } catch (failure) {
    if (id === props.examId) error.value = messageOf(failure)
  } finally {
    if (id === props.examId && !background) loading.value = false
  }
}

async function loadOptions(keyword = '') {
  if (!props.examId) return
  optionsLoading.value = true
  try {
    const params = new URLSearchParams({ examId: props.examId })
    if (keyword.trim()) params.set('search', keyword.trim())
    const data = await api<{ items: ExamStudentOption[] }>(`/exam-student-options?${params.toString()}`)
    options.value = data.items
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    optionsLoading.value = false
  }
}

function openAdd() {
  addSelection.value = []
  options.value = []
  addVisible.value = true
  void loadOptions()
}

async function addStudents() {
  if (!addSelection.value.length) return
  adding.value = true
  try {
    const result = await post<{ added: number }>(`/exams/${props.examId}/students`, {
      studentUserIds: addSelection.value
    })
    ElMessage.success(`已加入 ${result.added} 名学生`)
    addVisible.value = false
    await load()
    emit('changed')
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    adding.value = false
  }
}

async function removeStudents(students: ExamRosterStudent[]) {
  if (!students.length || removingId.value) return
  try {
    const target = students.length === 1 ? `“${students[0].name}”` : `所选的 ${students.length} 名学生`
    await ElMessageBox.confirm(`确定将${target}从本场考试名单中移除吗？`, '移除学生', {
      type: 'warning',
      confirmButtonText: '移除',
      cancelButtonText: '取消'
    })
    removingId.value = students.length === 1 ? students[0].studentUserId : 'selected'
    await post(`/exams/${props.examId}/students/remove`, {
      studentUserIds: students.map(student => student.studentUserId)
    })
    ElMessage.success('已移除学生')
    rosterTable.value?.clearSelection()
    await load()
    emit('changed')
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  } finally {
    removingId.value = ''
  }
}

async function deploy(studentUserIds?: string[]) {
  if (!items.value.length || deploying.value) return
  deploying.value = true
  try {
    const result = await post<{ deployed: number; existing: number; failed: number; failures: string[] }>(
      `/exams/${props.examId}/deployment`,
      { studentUserIds }
    )
    const summary = `新就绪 ${result.deployed} 人，跳过 ${result.existing} 人`
    if (result.failed) ElMessage.warning(`${summary}，失败 ${result.failed} 人。${result.failures[0] || ''}`)
    else ElMessage.success(summary)
    await load()
    emit('changed')
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    deploying.value = false
  }
}

async function deployOne(student: ExamRosterStudent) {
  rowDeploying.value = student.studentUserId
  try {
    await deploy([student.studentUserId])
  } finally {
    rowDeploying.value = ''
  }
}

function openDeployment(student: ExamRosterStudent) {
  deploymentStudent.value = student
  deploymentVisible.value = true
}

watch(
  () => [props.modelValue, props.examId] as const,
  ([open]) => {
    items.value = []
    selection.value = []
    projectCount.value = 0
    deploymentVisible.value = false
    if (open) void load()
  }
)
watch(
  () => props.modelValue && autoRefresh.value,
  (enabled, _, onCleanup) => {
    if (!enabled) return
    const timer = window.setInterval(() => void load(true), 10000)
    onCleanup(() => window.clearInterval(timer))
  }
)
</script>

<template>
  <el-drawer v-model="visible" :title="`考试名单 · ${examName}`" size="min(980px, 96vw)">
    <div class="roster-toolbar">
      <el-input
        v-model="search"
        class="search-box"
        clearable
        placeholder="搜索姓名、账号、学号或班级"
        :prefix-icon="Search"
      />
      <el-button :icon="Refresh" :loading="loading" @click="load()">刷新</el-button>
      <el-switch v-model="autoRefresh" active-text="自动刷新" />
      <el-button v-if="editable" :icon="Plus" @click="openAdd">添加学生</el-button>
      <template v-if="editable">
        <el-tooltip :disabled="!removeStudentsReason()" :content="removeStudentsReason()" placement="bottom">
          <span class="disabled-action-wrap">
            <el-button
              :disabled="!selection.length || !!removingId"
              type="danger"
              plain
              @click="removeStudents(selection)"
              >移除所选</el-button
            >
          </span>
        </el-tooltip>
      </template>
      <template v-if="projectCount">
        <el-tooltip :disabled="!deployAllReason()" :content="deployAllReason()" placement="bottom">
          <span class="disabled-action-wrap">
            <el-button
              type="primary"
              :loading="deploying && !rowDeploying"
              :disabled="!deployable || !items.length || !!rowDeploying"
              @click="deploy()"
            >
              一键部署环境
            </el-button>
          </span>
        </el-tooltip>
        <el-tooltip :disabled="!deploySelectedReason()" :content="deploySelectedReason()" placement="bottom">
          <span class="disabled-action-wrap">
            <el-button
              :disabled="!deployable || !selection.length || deploying"
              @click="deploy(selection.map(item => item.studentUserId))"
            >
              部署所选
            </el-button>
          </span>
        </el-tooltip>
      </template>
      <span class="record-count">共 {{ items.length }} 人 · 已部署 {{ deployedCount }} 人</span>
    </div>

    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
    <p v-if="filtered.length" class="table-scroll-hint">横向滚动可查看更多列</p>
    <el-table
      ref="rosterTable"
      v-loading="loading"
      :data="filtered"
      row-key="studentUserId"
      empty-text=""
      @selection-change="rows => (selection = rows)"
    >
      <template #empty>
        <TableEmpty v-if="!loading" :title="rosterEmptyTitle()" :hint="rosterEmptyHint()">
          <template v-if="searching && !error" #action>
            <el-button @click="search = ''">清除搜索</el-button>
          </template>
        </TableEmpty>
      </template>
      <el-table-column v-if="editable || (projectCount && deployable)" type="selection" reserve-selection width="46" />
      <el-table-column prop="name" label="学生" min-width="140">
        <template #default="{ row }">
          <strong>{{ row.name }}</strong>
          <p class="roster-sub">{{ row.userName }} · {{ row.number }}</p>
        </template>
      </el-table-column>
      <el-table-column prop="className" label="班级" min-width="120">
        <template #default="{ row }">{{ row.className || '未分班' }}</template>
      </el-table-column>
      <el-table-column label="性别与联系方式" min-width="155">
        <template #default="{ row }">
          <div>{{ row.gender || '—' }} · {{ row.phone || '—' }}</div>
          <div>{{ row.email || '—' }}</div>
        </template>
      </el-table-column>
      <el-table-column v-if="projectCount" label="环境" min-width="135">
        <template #default="{ row }">
          <el-tag :type="deploymentType(row.deploymentStatus)" size="small" effect="plain">
            {{ deploymentLabel(row.deploymentStatus) }}
          </el-tag>
          <p v-if="row.deploymentMessage" class="roster-sub">{{ row.deploymentMessage }}</p>
        </template>
      </el-table-column>
      <el-table-column label="参加情况" width="120">
        <template #default="{ row }">
          <el-tag :type="attemptStatusType(row.attemptStatus)" size="small">
            {{ attemptStatusLabel(row.attemptStatus) }}
          </el-tag>
          <p v-if="row.autoSubmitted" class="roster-sub">超时自动交卷</p>
        </template>
      </el-table-column>
      <el-table-column label="成绩" width="90" align="center">
        <template #default="{ row }">
          <span v-if="row.totalScore !== null">{{ row.totalScore }} / {{ row.maxScore }}</span>
          <span v-else class="roster-muted">—</span>
        </template>
      </el-table-column>
      <el-table-column label="加入时间" width="168">
        <template #default="{ row }">{{ dateTime(row.addedAt) }}</template>
      </el-table-column>
      <el-table-column label="操作" width="176" fixed="right">
        <template #default="{ row }">
          <el-button v-if="projectCount" link type="primary" @click="openDeployment(row as ExamRosterStudent)"
            >实操项目</el-button
          >
          <el-button
            v-if="projectCount && deployable && row.deploymentStatus !== 'deployed'"
            link
            type="primary"
            :loading="rowDeploying === row.studentUserId"
            :disabled="deploying"
            @click="deployOne(row as ExamRosterStudent)"
          >
            部署
          </el-button>
          <el-button
            v-if="editable"
            link
            type="danger"
            :icon="Delete"
            :loading="removingId === row.studentUserId"
            @click="removeStudents([row as ExamRosterStudent])"
          >
            移除
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog
      v-model="deploymentVisible"
      :title="`${deploymentStudent?.name || ''} · 实操环境`"
      width="min(850px, 96vw)"
      append-to-body
      destroy-on-close
    >
      <ExamDeploymentDialog
        v-if="deploymentStudent"
        :key="deploymentStudent.studentUserId"
        :url="`/exams/${examId}/deployment/${deploymentStudent.studentUserId}`"
      />
    </el-dialog>

    <el-dialog
      v-model="addVisible"
      title="添加考试学生"
      width="min(600px, 94vw)"
      append-to-body
      @opened="focusAddStudent"
    >
      <div class="add-student-form">
        <el-select
          ref="addStudentSelect"
          v-model="addSelection"
          multiple
          filterable
          remote
          reserve-keyword
          :remote-method="loadOptions"
          :loading="optionsLoading"
          placeholder="输入姓名、学号或账号搜索"
        >
          <el-option
            v-for="option in options"
            :key="option.userId"
            :label="`${option.name}（${option.userName} · ${option.number}）`"
            :value="option.userId"
          >
            <span>{{ option.name }}</span>
            <span class="option-meta">{{ option.className || '未分班' }} · {{ option.number }}</span>
          </el-option>
        </el-select>
        <p class="field-help">仅显示尚未加入本场考试的学生，最多返回 50 条搜索结果。</p>
      </div>
      <template #footer>
        <el-button @click="addVisible = false">取消</el-button>
        <el-tooltip :disabled="!addStudentsReason()" :content="addStudentsReason()" placement="top">
          <span class="disabled-action-wrap">
            <el-button type="primary" :loading="adding" :disabled="!addSelection.length" @click="addStudents">
              加入 {{ addSelection.length }} 名学生
            </el-button>
          </span>
        </el-tooltip>
      </template>
    </el-dialog>
  </el-drawer>
</template>

<style scoped>
.roster-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
  margin-bottom: 16px;
}

.roster-toolbar .search-box {
  width: min(300px, 100%);
}

.roster-toolbar .record-count {
  margin-left: auto;
}

.roster-sub {
  margin: 3px 0 0;
  color: #56697c;
  font-size: 12px;
  line-height: 1.4;
}

.roster-muted {
  color: #5d6f80;
}

.add-student-form :deep(.el-select) {
  width: 100%;
}

.option-meta {
  float: right;
  color: #56697c;
  font-size: 12px;
}

@media (max-width: 640px) {
  .roster-toolbar .search-box,
  .roster-toolbar .record-count {
    margin-left: 0;
    width: 100%;
  }
}
</style>

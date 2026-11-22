<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { MoreFilled, Plus, Refresh, Search } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, del, messageOf, post, put } from '../api'
import { readListState, writeListState } from '../listState'
import { recoverPage } from '../paging'
import { rangeText } from '../time'
import {
  classroomResourceCountLines,
  classroomResourceCounts,
  classroomStatusOptions,
  classroomStatusType,
  courseTypeOptions,
  labelOf
} from '../teaching'
import { shanghaiInputTime, shanghaiInputToIso } from '../time'
import type {
  ClassroomStatus,
  ClassroomStudent,
  ClassroomSummary,
  CourseSummary,
  Paged,
  TeacherOption,
  TeachingClassesResponse
} from '../types'
import ClassroomStatisticsDrawer from './ClassroomStatisticsDrawer.vue'
import ClassroomGroupsDrawer from './ClassroomGroupsDrawer.vue'
import ClassroomCabinetDrawer from './ClassroomCabinetDrawer.vue'
import ClassroomCaseMonitorDrawer from './ClassroomCaseMonitorDrawer.vue'
import ClassroomStudentPicker from './ClassroomStudentPicker.vue'
import TableEmpty from './TableEmpty.vue'
import { focusFirstField } from '../focusField'

const props = defineProps<{ admin: boolean; initialStatisticsId?: string }>()
const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()

const items = ref<ClassroomSummary[]>([])
const total = ref(0)
const savedList = readListState('classrooms')
const page = ref(savedList.page)
const pageSize = ref(savedList.pageSize)
const loading = ref(false)
const error = ref('')
const search = ref(savedList.search)
const statusFilter = ref(savedList.filters.status || '')
const tableViewport = window.matchMedia('(max-width: 1280px)')
const narrowTable = ref(tableViewport.matches)
function syncTableViewport(event: MediaQueryListEvent) {
  narrowTable.value = event.matches
}

const courseOptions = ref<(ClassroomSummary['resources'] & { id: string; name: string })[]>([])
const selectedCourse = computed(() => courseOptions.value.find(course => course.id === form.courseId))
const teachers = ref<TeacherOption[]>([])
const teaching = ref<TeachingClassesResponse>({ professions: [], classes: [] })

const dialogVisible = ref(false)
const nameInput = ref<{ focus?: () => void; $el?: HTMLElement }>()
function focusNameInput() {
  const root = nameInput.value?.$el
  if (root) focusFirstField(root)
  else nameInput.value?.focus?.()
}
const editing = ref<ClassroomSummary | null>(null)
const saving = ref(false)
const formError = ref('')
const form = reactive({
  name: '',
  courseId: '',
  teacherUserId: '',
  openAt: '',
  closeAt: '',
  classIds: [] as string[],
  professionIds: [] as string[],
  studentUserIds: [] as string[]
})
const formBaseline = ref('')
const formDirty = computed(() => JSON.stringify(form) !== formBaseline.value)

const rosterVisible = ref(false)
const rosterLoading = ref(false)
const rosterError = ref('')
const roster = ref<ClassroomStudent[]>([])
const rosterSearch = ref('')
const filteredRoster = computed(() => {
  const keyword = rosterSearch.value.trim().toLowerCase()
  return keyword
    ? roster.value.filter(student =>
        [student.name, student.number, student.userName, student.className, student.professionName].some(value =>
          value.toLowerCase().includes(keyword)
        )
      )
    : roster.value
})
const selectedStudents = ref<ClassroomStudent[]>([])
const rosterClassroom = ref<ClassroomSummary | null>(null)
const addStudentVisible = ref(false)
const addStudentSaving = ref(false)
const addStudentError = ref('')
const addStudentForm = reactive({
  classIds: [] as string[],
  professionIds: [] as string[],
  studentUserIds: [] as string[]
})

const addStudentFormRef = ref<{ $el?: HTMLElement }>()
function focusAddStudent() {
  focusFirstField(addStudentFormRef.value?.$el)
}

const statisticsVisible = ref(false)
const statisticsClassroomId = ref('')

const collaborationClassroom = ref<ClassroomSummary | null>(null)
const groupsVisible = ref(false)
const cabinetsVisible = ref(false)
const monitorVisible = ref(false)

function openCollaboration(item: ClassroomSummary, command: string) {
  collaborationClassroom.value = item
  if (command === 'groups') groupsVisible.value = true
  else if (command === 'cabinets') cabinetsVisible.value = true
  else if (command === 'monitor') monitorVisible.value = true
}

function courseOptionName(type: string) {
  return labelOf(courseTypeOptions, type)
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSize.value) })
    if (search.value.trim()) params.set('search', search.value.trim())
    if (statusFilter.value) params.set('status', statusFilter.value)
    const data = await api<Paged<ClassroomSummary>>(`/classrooms?${params.toString()}`)
    items.value = data.items
    total.value = data.total
    const recovered = recoverPage(page.value, pageSize.value, total.value, items.value.length)
    if (recovered !== null && recovered !== page.value) {
      page.value = recovered
      return load()
    }
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

async function loadCourses() {
  try {
    const data = await api<Paged<CourseSummary>>('/courses?page=1&pageSize=1000&active=true')
    courseOptions.value = data.items
  } catch {
    courseOptions.value = []
  }
}

async function loadTeachers() {
  if (!props.admin) return
  try {
    teachers.value = await api<TeacherOption[]>('/teaching/teachers')
  } catch {
    teachers.value = []
  }
}

async function loadTeachingClasses() {
  try {
    teaching.value = await api<TeachingClassesResponse>('/teaching/classes')
  } catch {
    teaching.value = { professions: [], classes: [] }
  }
}

function saveListState() {
  writeListState('classrooms', {
    search: search.value,
    filters: { status: statusFilter.value },
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
  statusFilter.value = ''
  applyFilters()
}

watch([page, pageSize], saveListState)

const filtering = computed(() => !!search.value.trim() || !!statusFilter.value)

function defaultWindow() {
  const now = new Date()
  return {
    openAt: shanghaiInputTime(now.toISOString()).slice(0, 16),
    closeAt: shanghaiInputTime(new Date(now.getTime() + 7 * 24 * 60 * 60 * 1000).toISOString()).slice(0, 16)
  }
}

function resetForm() {
  const window = defaultWindow()
  form.name = ''
  form.courseId = courseOptions.value[0]?.id || ''
  form.teacherUserId = props.admin ? teachers.value[0]?.id || '' : ''
  form.openAt = window.openAt
  form.closeAt = window.closeAt
  form.classIds = []
  form.professionIds = []
  form.studentUserIds = []
  formError.value = ''
}

function openCreate() {
  editing.value = null
  resetForm()
  formBaseline.value = JSON.stringify(form)
  dialogVisible.value = true
}

function classroomCommand(command: string, item: ClassroomSummary) {
  if (command === 'edit') openEdit(item)
  else if (command === 'toggle') void toggleOpen(item)
  else if (command === 'delete') void removeClassroom(item)
  else openCollaboration(item, command)
}

function openEdit(item: ClassroomSummary) {
  editing.value = item
  if (!courseOptions.value.some(option => option.id === item.courseId))
    courseOptions.value = [{ id: item.courseId, name: item.courseName, ...item.resources }, ...courseOptions.value]
  form.name = item.name
  form.courseId = item.courseId
  form.teacherUserId = item.teacherUserId
  form.openAt = shanghaiInputTime(item.openAt).slice(0, 16)
  form.closeAt = shanghaiInputTime(item.closeAt).slice(0, 16)
  form.classIds = []
  form.professionIds = []
  form.studentUserIds = []
  formError.value = ''
  formBaseline.value = JSON.stringify(form)
  dialogVisible.value = true
}

async function confirmDiscardClassroom() {
  if (saving.value) return false
  if (!formDirty.value) return true
  try {
    await ElMessageBox.confirm('课堂信息还没有保存，关闭后不会保留。', '放弃未保存的修改？', {
      type: 'warning',
      confirmButtonText: '放弃修改',
      cancelButtonText: '继续编辑'
    })
    return true
  } catch {
    return false
  }
}

async function handleClassroomBeforeClose(done: () => void) {
  if (await confirmDiscardClassroom()) done()
}

async function cancelClassroom() {
  if (await confirmDiscardClassroom()) dialogVisible.value = false
}

async function saveClassroom() {
  if (!form.name.trim()) {
    formError.value = '请填写课堂名称'
    return
  }
  if (!form.courseId) {
    formError.value = '请选择已发布的课程'
    return
  }
  const openAt = shanghaiInputToIso(form.openAt)
  const closeAt = shanghaiInputToIso(form.closeAt)
  if (!openAt || !closeAt) {
    formError.value = '请选择有效的开课和结课时间'
    return
  }
  if (new Date(closeAt) <= new Date(openAt)) {
    formError.value = '结课时间必须晚于开课时间'
    return
  }
  saving.value = true
  formError.value = ''
  try {
    const payload = {
      name: form.name.trim(),
      courseId: form.courseId,
      teacherUserId: props.admin ? form.teacherUserId || null : null,
      openAt,
      closeAt,
      classIds: editing.value ? [] : form.classIds,
      professionIds: editing.value ? [] : form.professionIds,
      studentUserIds: editing.value ? [] : form.studentUserIds
    }
    if (editing.value) await put(`/classrooms/${editing.value.id}`, payload)
    else await post('/classrooms', payload)
    ElMessage.success(editing.value ? '课堂已更新' : '课堂已创建')
    dialogVisible.value = false
    await load()
  } catch (failure) {
    formError.value = messageOf(failure)
  } finally {
    saving.value = false
  }
}

async function toggleOpen(item: ClassroomSummary) {
  try {
    if (item.storedStatus === 'open' && item.status !== 'closed') {
      await ElMessageBox.confirm('结课后学生将不能继续提交学习记录，已完成的记录会保留。', '结束课堂', {
        type: 'warning',
        confirmButtonText: '结课',
        cancelButtonText: '取消'
      })
      await post(`/classrooms/${item.id}/close`)
      ElMessage.success('课堂已结课')
    } else {
      const room = await post<{ status: ClassroomStatus }>(`/classrooms/${item.id}/open`)
      ElMessage.success(room.status === 'open' ? '课堂已开放' : '课堂将在设定时间开放')
    }
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

async function removeClassroom(item: ClassroomSummary) {
  try {
    await ElMessageBox.confirm(`删除课堂“${item.name}”会同时删除学生名单和学习记录，是否继续？`, '删除课堂', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消'
    })
    await del(`/classrooms/${item.id}`)
    ElMessage.success('课堂已删除')
    if (items.value.length === 1 && page.value > 1) page.value--
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

function openContent(item: ClassroomSummary) {
  emit('navigate', 'classroom-content', { classroomId: item.id })
}

async function openRoster(item: ClassroomSummary) {
  rosterSearch.value = ''
  rosterClassroom.value = item
  rosterVisible.value = true
  selectedStudents.value = []
  await loadRoster()
}

async function loadRoster() {
  if (!rosterClassroom.value) return
  rosterLoading.value = true
  rosterError.value = ''
  try {
    const data = await api<{ items: ClassroomStudent[] }>(`/classrooms/${rosterClassroom.value.id}/students`)
    roster.value = data.items
  } catch (failure) {
    roster.value = []
    rosterError.value = messageOf(failure)
  } finally {
    rosterLoading.value = false
  }
}

function openAddStudents() {
  addStudentForm.classIds = []
  addStudentForm.professionIds = []
  addStudentForm.studentUserIds = []
  addStudentError.value = ''
  addStudentVisible.value = true
}

async function addStudents() {
  if (!rosterClassroom.value) return
  if (
    !addStudentForm.classIds.length &&
    !addStudentForm.professionIds.length &&
    !addStudentForm.studentUserIds.length
  ) {
    addStudentError.value = '请选择学生、专业或班级'
    return
  }
  addStudentSaving.value = true
  addStudentError.value = ''
  try {
    const result = await api<{ added: number }>(`/classrooms/${rosterClassroom.value.id}/students`, {
      method: 'POST',
      body: JSON.stringify(addStudentForm)
    })
    ElMessage.success(`已新增 ${result.added} 名学生`)
    addStudentVisible.value = false
    await Promise.all([loadRoster(), load()])
  } catch (failure) {
    addStudentError.value = messageOf(failure)
  } finally {
    addStudentSaving.value = false
  }
}

async function removeStudents() {
  if (!rosterClassroom.value || !selectedStudents.value.length) {
    ElMessage.warning('请先选择学生')
    return
  }
  try {
    await ElMessageBox.confirm(
      `将从课堂移除 ${selectedStudents.value.length} 名学生，并删除其在本课堂的学习记录，是否继续？`,
      '移除学生',
      { type: 'warning', confirmButtonText: '移除', cancelButtonText: '取消' }
    )
    await api(`/classrooms/${rosterClassroom.value.id}/students/remove`, {
      method: 'POST',
      body: JSON.stringify({ userIds: selectedStudents.value.map(item => item.userId) })
    })
    ElMessage.success('学生已移除')
    selectedStudents.value = []
    await Promise.all([loadRoster(), load()])
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

function openStatistics(item: ClassroomSummary) {
  emit('navigate', 'classrooms', { statistics: item.id })
}

function closeStatistics() {
  statisticsVisible.value = false
  emit('navigate', 'classrooms')
}

function classroomStatusText(status: string) {
  return labelOf(classroomStatusOptions, status)
}

watch(
  () => props.initialStatisticsId,
  id => {
    statisticsVisible.value = !!id
    if (id) statisticsClassroomId.value = id
  },
  { immediate: true }
)

onMounted(() => {
  tableViewport.addEventListener('change', syncTableViewport)
  void load()
  void loadCourses()
  void loadTeachers()
  void loadTeachingClasses()
})

onBeforeUnmount(() => tableViewport.removeEventListener('change', syncTableViewport))
</script>

<template>
  <section class="page-heading management-header">
    <div>
      <h1>{{ admin ? '课堂管理' : '我的课堂' }}</h1>
      <p>
        {{
          admin
            ? '按专业或班级组课，管理授课教师、学生名单与学习进度。'
            : '组织课程内容、维护学生名单并查看课堂学习情况。'
        }}
      </p>
    </div>
    <div class="heading-actions">
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      <el-tooltip :disabled="courseOptions.length > 0" content="请先在课程管理中发布并激活课程" placement="bottom">
        <span class="disabled-action-wrap">
          <el-button type="primary" :icon="Plus" :disabled="!courseOptions.length" @click="openCreate"
            >新建课堂</el-button
          >
        </span>
      </el-tooltip>
    </div>
  </section>

  <el-alert
    v-if="!courseOptions.length && !loading"
    title="尚无可组课的课程，请先在课程管理中发布并激活课程。"
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
          placeholder="搜索课堂、课程或教师"
          :prefix-icon="Search"
          @keyup.enter="applyFilters"
          @clear="applyFilters"
        />
        <el-select v-model="statusFilter" class="table-filter" placeholder="全部状态" clearable @change="applyFilters">
          <el-option
            v-for="option in classroomStatusOptions"
            :key="option.value"
            :label="option.label"
            :value="option.value"
          />
        </el-select>
        <el-button type="primary" :icon="Search" @click="applyFilters">查询</el-button>
        <el-button @click="resetFilters">重置</el-button>
      </div>
      <span class="record-count">{{ loading && !total ? '正在加载课堂…' : '共 ' + total + ' 个课堂' }}</span>
    </div>

    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="table-error" />
    <p v-if="narrowTable && items.length" class="table-scroll-hint">横向滚动可查看更多信息和操作</p>
    <el-table
      v-loading="loading"
      empty-text=""
      :data="items"
      row-key="id"
      :scrollbar-tabindex="narrowTable && items.length ? 0 : undefined"
      :scrollbar-always-on="narrowTable"
    >
      <template #empty>
        <TableEmpty
          v-if="!loading"
          :title="error ? '课堂加载失败' : filtering ? '没有符合条件的课堂' : '暂无课堂'"
          :hint="
            error
              ? '请检查服务状态后刷新重试'
              : filtering
                ? '试试调整关键词或状态筛选，也可清除后查看全部课堂。'
                : courseOptions.length
                  ? '点击右上角「新建课堂」，选择已激活课程与班级。'
                  : '请先在课程管理中发布并激活课程，再来新建课堂。'
          "
        >
          <template v-if="!error && filtering" #action>
            <el-button type="primary" plain @click="resetFilters">清除筛选</el-button>
          </template>
        </TableEmpty>
      </template>
      <el-table-column prop="name" label="课堂" min-width="230">
        <template #default="{ row }">
          <div class="table-name">
            <strong>{{ row.name }}</strong>
            <el-tag size="small" effect="plain">{{ courseOptionName(row.courseType) }}</el-tag>
          </div>
          <p class="classroom-subline">{{ row.courseName }}</p>
        </template>
      </el-table-column>
      <el-table-column prop="teacherName" label="授课教师" min-width="96" />
      <el-table-column label="时间" min-width="165">
        <template #default="{ row }">
          <span class="time-range">{{ rangeText(row.openAt, row.closeAt) }}</span>
        </template>
      </el-table-column>
      <el-table-column label="课程资源" min-width="185">
        <template #default="{ row }">
          <span class="classroom-subline"
            >{{ classroomResourceCountLines(row.resources)[0] }}<br />{{
              classroomResourceCountLines(row.resources)[1]
            }}</span
          >
        </template>
      </el-table-column>
      <el-table-column label="学生" width="75">
        <template #default="{ row }">{{ row.studentCount }} 人</template>
      </el-table-column>
      <el-table-column label="状态" width="90">
        <template #default="{ row }">
          <el-tag :type="classroomStatusType(row.status as ClassroomStatus)" size="small">
            {{ classroomStatusText(row.status) }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="操作" :width="narrowTable ? 175 : 215" :fixed="narrowTable ? false : 'right'">
        <template #default="{ row }">
          <div class="management-actions">
            <el-button link type="primary" @click="openContent(row as ClassroomSummary)">内容</el-button>
            <el-button link @click="openRoster(row as ClassroomSummary)">学生</el-button>
            <el-button link @click="openStatistics(row as ClassroomSummary)">统计</el-button>
            <el-dropdown trigger="click" @command="command => classroomCommand(command, row as ClassroomSummary)">
              <el-button link :icon="MoreFilled">更多</el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item command="groups">小组管理</el-dropdown-item>
                  <el-dropdown-item command="cabinets">分配机柜</el-dropdown-item>
                  <el-dropdown-item command="monitor">实操监控</el-dropdown-item>
                  <el-dropdown-item command="edit" divided>编辑课堂</el-dropdown-item>
                  <el-dropdown-item command="toggle">{{
                    row.storedStatus === 'open' && row.status !== 'closed' ? '结课' : '一键开课'
                  }}</el-dropdown-item>
                  <el-dropdown-item command="delete" divided>删除课堂</el-dropdown-item>
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
    :title="editing ? '编辑课堂' : '新建课堂'"
    width="min(680px, 94vw)"
    :before-close="handleClassroomBeforeClose"
    @opened="focusNameInput"
  >
    <el-alert v-if="formError" :title="formError" type="error" show-icon :closable="false" class="section-notice" />
    <el-form scroll-to-error label-position="top" class="entity-form">
      <div class="classroom-form-grid">
        <el-form-item label="课堂名称" required>
          <el-input
            ref="nameInput"
            v-model="form.name"
            maxlength="100"
            show-word-limit
            placeholder="例如：网络 2401 班实训"
          />
        </el-form-item>
        <el-form-item label="课程" required>
          <el-select v-model="form.courseId" filterable placeholder="选择已激活课程">
            <el-option v-for="option in courseOptions" :key="option.id" :label="option.name" :value="option.id" />
          </el-select>
          <small v-if="selectedCourse">{{ classroomResourceCounts(selectedCourse) }}</small>
        </el-form-item>
      </div>
      <el-form-item v-if="admin" label="授课教师">
        <el-select v-model="form.teacherUserId" filterable placeholder="选择教师">
          <el-option
            v-for="teacher in teachers"
            :key="teacher.id"
            :label="`${teacher.displayName}（${teacher.userName}）`"
            :value="teacher.id"
          />
        </el-select>
        <p class="field-help">不选择时由当前管理员账号担任授课教师。</p>
      </el-form-item>
      <div class="classroom-form-grid">
        <el-form-item label="开课时间" required>
          <el-date-picker
            v-model="form.openAt"
            type="datetime"
            value-format="YYYY-MM-DDTHH:mm"
            placeholder="选择开课时间"
            style="width: 100%"
          />
        </el-form-item>
        <el-form-item label="结课时间" required>
          <el-date-picker
            v-model="form.closeAt"
            type="datetime"
            value-format="YYYY-MM-DDTHH:mm"
            placeholder="选择结课时间"
            style="width: 100%"
          />
        </el-form-item>
      </div>
      <template v-if="!editing">
        <ClassroomStudentPicker v-if="dialogVisible" v-model="form.studentUserIds" :teaching="teaching" />
        <el-form-item label="按班级选课（可选）">
          <el-select v-model="form.classIds" multiple filterable collapse-tags placeholder="选择班级">
            <el-option
              v-for="item in teaching.classes"
              :key="item.id"
              :label="`${item.name} · ${item.studentCount} 人`"
              :value="item.id"
            />
          </el-select>
        </el-form-item>
        <el-form-item label="按专业选课（可选）">
          <el-select v-model="form.professionIds" multiple filterable collapse-tags placeholder="选择专业">
            <el-option v-for="item in teaching.professions" :key="item.id" :label="item.name" :value="item.id" />
          </el-select>
        </el-form-item>
        <p class="field-help">按专业或班级创建时会自动选入对应学生；也可创建后在“学生”中调整。</p>
      </template>
      <el-alert
        v-else
        title="学生名单可在列表“学生”中调整；已有学习记录时不能更换课程。"
        type="info"
        show-icon
        :closable="false"
      />
    </el-form>
    <template #footer>
      <el-button @click="cancelClassroom">取消</el-button>
      <el-button type="primary" :loading="saving" @click="saveClassroom">{{
        editing ? '保存修改' : '创建课堂'
      }}</el-button>
    </template>
  </el-dialog>

  <el-drawer
    v-model="rosterVisible"
    :title="rosterClassroom ? `${rosterClassroom.name} · 学生名单` : '学生名单'"
    :size="'min(760px, 96vw)'"
  >
    <div class="drawer-toolbar">
      <el-input
        v-model="rosterSearch"
        class="search-box"
        :prefix-icon="Search"
        clearable
        placeholder="搜索姓名、学号、班级或专业"
      />
      <el-button type="primary" :icon="Plus" @click="openAddStudents">添加学生</el-button>
      <el-tooltip :disabled="selectedStudents.length > 0" content="请先勾选要移除的学生" placement="bottom">
        <span class="disabled-action-wrap">
          <el-button type="danger" plain :disabled="!selectedStudents.length" @click="removeStudents"
            >移除所选</el-button
          >
        </span>
      </el-tooltip>
      <el-button :icon="Refresh" :loading="rosterLoading" @click="loadRoster">刷新</el-button>
    </div>
    <el-alert v-if="rosterError" :title="rosterError" type="error" show-icon :closable="false" class="section-notice" />
    <el-table
      v-loading="rosterLoading"
      :data="filteredRoster"
      row-key="userId"
      empty-text=""
      @selection-change="(rows: unknown) => (selectedStudents = rows as ClassroomStudent[])"
    >
      <template #empty>
        <TableEmpty
          v-if="!rosterLoading"
          :title="rosterSearch.trim() ? '没有符合条件的学生' : '暂无学生'"
          :hint="rosterSearch.trim() ? '试试调整搜索关键词。' : '点击「添加学生」，按专业、班级或名单加入课堂。'"
        />
      </template>
      <el-table-column type="selection" width="48" />
      <el-table-column prop="name" label="姓名" min-width="100" />
      <el-table-column prop="number" label="学号" min-width="120" />
      <el-table-column prop="className" label="班级" min-width="140" />
      <el-table-column prop="professionName" label="专业" min-width="130" />
      <el-table-column prop="userName" label="账号" min-width="130" />
    </el-table>
  </el-drawer>

  <el-dialog v-model="addStudentVisible" title="添加课堂学生" width="min(540px, 94vw)" @opened="focusAddStudent">
    <el-alert
      v-if="addStudentError"
      :title="addStudentError"
      type="error"
      show-icon
      :closable="false"
      class="section-notice"
    />
    <el-form scroll-to-error ref="addStudentFormRef" label-position="top" class="entity-form">
      <ClassroomStudentPicker v-if="addStudentVisible" v-model="addStudentForm.studentUserIds" :teaching="teaching" />
      <el-form-item label="专业">
        <el-select v-model="addStudentForm.professionIds" multiple filterable placeholder="选择专业">
          <el-option v-for="item in teaching.professions" :key="item.id" :label="item.name" :value="item.id" />
        </el-select>
      </el-form-item>
      <el-form-item label="班级">
        <el-select v-model="addStudentForm.classIds" multiple filterable placeholder="选择班级">
          <el-option
            v-for="item in teaching.classes"
            :key="item.id"
            :label="`${item.name} · ${item.professionName} · ${item.studentCount} 人`"
            :value="item.id"
          />
        </el-select>
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button @click="addStudentVisible = false">取消</el-button>
      <el-button type="primary" :loading="addStudentSaving" @click="addStudents">添加学生</el-button>
    </template>
  </el-dialog>

  <ClassroomStatisticsDrawer
    :visible="statisticsVisible"
    :classroom-id="statisticsClassroomId"
    @update:visible="value => (value ? (statisticsVisible = value) : closeStatistics())"
  />

  <ClassroomGroupsDrawer
    v-model:visible="groupsVisible"
    :classroom-id="collaborationClassroom?.id || ''"
    :classroom-name="collaborationClassroom?.name || ''"
  />
  <ClassroomCabinetDrawer
    v-model:visible="cabinetsVisible"
    :classroom-id="collaborationClassroom?.id || ''"
    :classroom-name="collaborationClassroom?.name || ''"
  />
  <ClassroomCaseMonitorDrawer
    v-model:visible="monitorVisible"
    :classroom-id="collaborationClassroom?.id || ''"
    :classroom-name="collaborationClassroom?.name || ''"
  />
</template>

<style scoped>
.classroom-subline {
  margin: 3px 0 0;
  color: #5d6f80;
  font-size: 12px;
}

.time-range {
  color: #51687f;
  font-size: 12px;
  line-height: 1.55;
}

.classroom-form-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 0 14px;
}

@media (max-width: 700px) {
  .classroom-form-grid {
    grid-template-columns: 1fr;
  }
}
</style>

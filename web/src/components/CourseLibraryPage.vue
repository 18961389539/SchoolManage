<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { Plus, Refresh, Search, View } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, dateTime, del, messageOf, post, put } from '../api'
import { readListState, useListPersist } from '../listState'
import { recoverPage } from '../paging'
import { useRequestGeneration } from '../requestGeneration'
import { confirmReload, isStale } from '../stale'
import { courseStatusOptions, courseTypeOptions, durationText, labelOf } from '../teaching'
import type { CoreCourseSeries, CourseDetail, CourseSummary, Paged } from '../types'
import CourseTreeEditor from './CourseTreeEditor.vue'
import TableEmpty from './TableEmpty.vue'
import { focusFirstField } from '../focusField'
import { useDiscardGuard } from '../discardGuard'

const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>]; 'activation-changed': [] }>()

const listKey = 'courses'
const savedList = readListState(listKey)
const items = ref<CourseSummary[]>([])
const total = ref(0)
const page = ref(savedList.page)
const pageSize = ref(savedList.pageSize)
const loading = ref(false)
const error = ref('')
const search = ref(savedList.search)
const statusFilter = ref(savedList.filters.status || '')
const typeFilter = ref(savedList.filters.type || '')
const listRequests = useRequestGeneration()

const dialogVisible = ref(false)
const nameInput = ref<{ focus?: () => void; $el?: HTMLElement }>()
function focusNameInput() {
  const root = nameInput.value?.$el
  if (root) focusFirstField(root)
  else nameInput.value?.focus?.()
}
const editing = ref<CourseSummary | null>(null)
const saving = ref(false)
const formError = ref('')
const form = reactive({ name: '', type: 'general', description: '', remark: '', seriesId: '' })
const guard = useDiscardGuard({
  visible: dialogVisible,
  snapshot: () => form,
  message: '课程信息还没有保存，关闭后不会保留。',
  busy: () => saving.value
})
const series = ref<CoreCourseSeries[]>([])
const seriesVisible = ref(false)
const seriesForm = reactive({ id: '', name: '', description: '' })
const seriesSaving = ref(false)

const seriesNameInput = ref<{ focus?: () => void; $el?: HTMLElement }>()
function focusSeriesName() {
  const root = seriesNameInput.value?.$el
  if (root) focusFirstField(root)
  else seriesNameInput.value?.focus?.()
}

async function loadSeries() {
  try {
    series.value = await api<CoreCourseSeries[]>('/course-series?manage=true')
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  }
}

function openSeriesManager() {
  seriesVisible.value = true
  void loadSeries()
}

function editSeries(item?: CoreCourseSeries) {
  Object.assign(seriesForm, { id: item?.id || '', name: item?.name || '', description: item?.description || '' })
}

async function saveSeries() {
  seriesSaving.value = true
  try {
    const payload = { name: seriesForm.name, description: seriesForm.description }
    if (seriesForm.id) await put(`/course-series/${seriesForm.id}`, payload)
    else await post('/course-series', payload)
    editSeries()
    await loadSeries()
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    seriesSaving.value = false
  }
}

async function removeSeries(item: CoreCourseSeries) {
  try {
    await ElMessageBox.confirm(`删除“${item.name}”系列后，其中的课程仍会保留。`, '删除课程系列', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消'
    })
    await del(`/course-series/${item.id}`)
    if (seriesForm.id === item.id) editSeries()
    await Promise.all([loadSeries(), load()])
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

const treeVisible = ref(false)
const treeCourseId = ref('')

async function load() {
  const token = listRequests.begin()
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSize.value) })
    if (search.value.trim()) params.set('search', search.value.trim())
    if (statusFilter.value) params.set('status', statusFilter.value)
    if (typeFilter.value) params.set('type', typeFilter.value)
    const data = await api<Paged<CourseSummary>>(`/courses?${params.toString()}`)
    if (!listRequests.isCurrent(token)) return
    items.value = data.items
    total.value = data.total
    const recovered = recoverPage(page.value, pageSize.value, total.value, items.value.length)
    if (recovered !== null && recovered !== page.value) {
      page.value = recovered
      return load()
    }
  } catch (failure) {
    if (listRequests.isCurrent(token)) error.value = messageOf(failure)
  } finally {
    if (listRequests.isCurrent(token)) loading.value = false
  }
}

useListPersist(listKey, () => ({
  search: search.value,
  filters: { status: statusFilter.value, type: typeFilter.value },
  page: page.value,
  pageSize: pageSize.value
}))

function applyFilters() {
  page.value = 1
  void load()
}

function resetFilters() {
  search.value = ''
  statusFilter.value = ''
  typeFilter.value = ''
  applyFilters()
}

const filtering = computed(() => !!search.value.trim() || !!statusFilter.value || !!typeFilter.value)

function openCreate() {
  editing.value = null
  form.name = ''
  form.type = 'general'
  form.description = ''
  form.remark = ''
  form.seriesId = ''
  formError.value = ''
  guard.capture()
  dialogVisible.value = true
}

function openEdit(course: CourseSummary) {
  editing.value = course
  form.name = course.name
  form.type = course.type
  form.description = course.description
  form.remark = course.remark
  form.seriesId = course.seriesId || ''
  formError.value = ''
  guard.capture()
  dialogVisible.value = true
}

async function saveCourse() {
  if (!form.name.trim()) {
    formError.value = '请填写课程名称'
    return
  }
  saving.value = true
  formError.value = ''
  try {
    const payload = {
      name: form.name.trim(),
      type: form.type,
      description: form.description.trim(),
      remark: form.remark.trim(),
      seriesId: form.type === 'core' ? form.seriesId || null : null
    }
    if (editing.value) await put(`/courses/${editing.value.id}`, { ...payload, version: editing.value.version })
    else await post('/courses', payload)
    ElMessage.success(editing.value ? '课程已更新' : '课程已创建')
    dialogVisible.value = false
    await load()
  } catch (failure) {
    formError.value = messageOf(failure)
    if (editing.value && isStale(failure)) {
      const id = editing.value.id
      saving.value = false
      if (!(await confirmReload('课程'))) return
      await reloadCourse(id)
    }
  } finally {
    saving.value = false
  }
}

/** Pulls the server copy back into the dialog after a 409, so a retry writes on a fresh base. */
async function reloadCourse(id: string) {
  try {
    openEdit(await api<CourseDetail>(`/courses/${id}`))
  } catch {
    dialogVisible.value = false
    ElMessage.warning('这门课程已被删除')
  }
}

async function togglePublish(course: CourseSummary) {
  try {
    if (course.status === 'published') {
      await ElMessageBox.confirm('下线后学生将无法在课程目录中看到该课程，已结课的课堂仍保留历史内容。', '下线课程', {
        type: 'warning',
        confirmButtonText: '下线',
        cancelButtonText: '取消'
      })
      await post(`/courses/${course.id}/unpublish`)
      ElMessage.success('课程已下线')
    } else {
      await post(`/courses/${course.id}/publish?activate=false`)
      ElMessage.success('课程已发布')
    }
    await load()
    emit('activation-changed')
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

async function toggleActivation(course: CourseSummary) {
  try {
    await put(`/courses/${course.id}/activation`, { active: !course.isActive })
    ElMessage.success(course.isActive ? '课程已停用' : '课程已激活')
    await load()
    emit('activation-changed')
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  }
}

async function removeCourse(course: CourseSummary) {
  try {
    await ElMessageBox.confirm(
      `确定删除课程“${course.name}”吗？课程目录会一并删除；已被课堂使用时不能删除。`,
      '删除课程',
      {
        type: 'warning',
        confirmButtonText: '删除',
        cancelButtonText: '取消'
      }
    )
    await del(`/courses/${course.id}`)
    ElMessage.success('课程已删除')
    if (items.value.length === 1 && page.value > 1) page.value--
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

function openTree(course: CourseSummary) {
  treeCourseId.value = course.id
  treeVisible.value = true
}

function previewCourse(course: CourseSummary) {
  emit('navigate', 'course-preview', { courseId: course.id, backKey: 'courses' })
}

async function onTreeChanged() {
  await load()
}

onMounted(() => {
  void load()
  void loadSeries()
})
</script>

<template>
  <section class="page-heading">
    <div>
      <h1>课程管理</h1>
      <p>课程发布后进入课程目录，激活后可用于开设课堂。</p>
    </div>
    <div class="heading-actions">
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      <el-button @click="openSeriesManager">管理核心课系列</el-button>
      <el-button type="primary" :icon="Plus" @click="openCreate">新建课程</el-button>
    </div>
  </section>

  <section class="surface">
    <div class="table-toolbar">
      <div class="table-controls">
        <el-input
          v-model="search"
          class="search-box"
          clearable
          placeholder="搜索课程名称或简介"
          :prefix-icon="Search"
          @keyup.enter="applyFilters"
          @clear="applyFilters"
        />
        <el-select v-model="typeFilter" class="table-filter" placeholder="全部类型" clearable @change="applyFilters">
          <el-option
            v-for="option in courseTypeOptions"
            :key="option.value"
            :label="option.label"
            :value="option.value"
          />
        </el-select>
        <el-select v-model="statusFilter" class="table-filter" placeholder="全部状态" clearable @change="applyFilters">
          <el-option
            v-for="option in courseStatusOptions"
            :key="option.value"
            :label="option.label"
            :value="option.value"
          />
        </el-select>
        <el-button type="primary" :icon="Search" @click="applyFilters">查询</el-button>
        <el-button @click="resetFilters">重置</el-button>
      </div>
      <span class="record-count">{{ loading && !total ? '正在加载课程…' : '共 ' + total + ' 门课程' }}</span>
    </div>

    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="table-error" />
    <el-table v-loading="loading" :data="items" row-key="id" empty-text="">
      <template #empty>
        <TableEmpty
          v-if="!loading"
          :title="error ? '课程加载失败' : filtering ? '没有符合条件的课程' : '暂无课程'"
          :hint="
            error
              ? '请检查服务状态后刷新重试'
              : filtering
                ? '试试调整关键词、类型或状态筛选，也可清除后查看全部课程。'
                : '点击右上角「新建课程」，配置章节资源后发布。'
          "
        >
          <template v-if="!error && filtering" #action>
            <el-button type="primary" plain @click="resetFilters">清除筛选</el-button>
          </template>
        </TableEmpty>
      </template>
      <el-table-column prop="name" label="课程" min-width="210">
        <template #default="{ row }">
          <div class="table-name">
            <strong>{{ row.name }}</strong>
            <el-tag size="small" effect="plain">{{ labelOf(courseTypeOptions, row.type) }}</el-tag>
          </div>
          <p class="course-description">{{ row.description || '暂无简介' }}</p>
        </template>
      </el-table-column>
      <el-table-column label="目录与资源" min-width="230">
        <template #default="{ row }">
          <span class="structure-counts">
            {{ row.chapterCount }} 大章 · {{ row.sectionCount }} 小节 · {{ row.resourceCount }} 资源
          </span>
          <p class="resource-breakdown">
            理论 {{ row.theoryCount }} · 案例 {{ row.caseCount }} · 练习 {{ row.theoryPracticeCount }} · 实操
            {{ row.casePracticeCount }}
          </p>
          <p class="resource-breakdown">总课时 {{ durationText(row.totalMinutes) }}</p>
        </template>
      </el-table-column>
      <el-table-column label="状态" width="100">
        <template #default="{ row }">
          <el-tag :type="row.status === 'published' ? 'success' : 'info'" size="small">
            {{ labelOf(courseStatusOptions, row.status) }}
          </el-tag>
          <div v-if="row.status === 'published'">
            <small>{{ row.isActive ? '已激活' : '未激活' }}</small>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="创建时间" width="170">
        <template #default="{ row }">{{ dateTime(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column label="操作" width="330" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" :icon="View" @click="openTree(row as CourseSummary)">管理目录</el-button>
          <el-button link type="primary" @click="previewCourse(row as CourseSummary)">预览</el-button>
          <el-button link @click="openEdit(row as CourseSummary)">编辑</el-button>
          <el-button link type="warning" @click="togglePublish(row as CourseSummary)">
            {{ row.status === 'published' ? '下线' : '发布' }}
          </el-button>
          <el-button
            v-if="row.status === 'published'"
            link
            type="primary"
            @click="toggleActivation(row as CourseSummary)"
          >
            {{ row.isActive ? '停用' : '激活' }}
          </el-button>
          <el-button link type="danger" @click="removeCourse(row as CourseSummary)">删除</el-button>
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
    :title="editing ? '编辑课程' : '新建课程'"
    width="min(620px, 94vw)"
    :before-close="guard.beforeClose"
    @opened="focusNameInput"
  >
    <el-alert v-if="formError" :title="formError" type="error" show-icon :closable="false" class="section-notice" />
    <el-form scroll-to-error label-position="top" class="entity-form">
      <div class="course-form-grid">
        <el-form-item label="课程名称" required>
          <el-input
            ref="nameInput"
            v-model="form.name"
            maxlength="100"
            show-word-limit
            placeholder="例如：计算机网络基础"
          />
        </el-form-item>
        <el-form-item label="课程类型" required>
          <el-select v-model="form.type">
            <el-option
              v-for="option in courseTypeOptions"
              :key="option.value"
              :label="option.label"
              :value="option.value"
            />
          </el-select>
        </el-form-item>
      </div>
      <el-form-item v-if="form.type === 'core'" label="所属核心课系列">
        <el-select v-model="form.seriesId" clearable filterable placeholder="选择系列">
          <el-option v-for="item in series" :key="item.id" :label="item.name" :value="item.id" />
        </el-select>
      </el-form-item>
      <el-form-item label="课程简介">
        <el-input
          v-model="form.description"
          type="textarea"
          :rows="4"
          maxlength="2000"
          show-word-limit
          placeholder="课程目标、适用专业或学习建议"
        />
      </el-form-item>
      <el-form-item label="备注">
        <el-input v-model="form.remark" type="textarea" :rows="2" maxlength="2000" show-word-limit />
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button @click="guard.cancel">取消</el-button>
      <el-button type="primary" :loading="saving" @click="saveCourse">{{
        editing ? '保存修改' : '创建课程'
      }}</el-button>
    </template>
  </el-dialog>

  <el-dialog v-model="seriesVisible" title="专业核心课系列" width="min(720px, 94vw)" @opened="focusSeriesName">
    <el-table :data="series" row-key="id" empty-text="暂无系列，可在下方输入名称后新增。">
      <el-table-column prop="name" label="系列名称" min-width="140" />
      <el-table-column prop="description" label="介绍" min-width="180" show-overflow-tooltip />
      <el-table-column prop="courseCount" label="课程数" width="75" />
      <el-table-column label="操作" width="115">
        <template #default="{ row }">
          <el-button link type="primary" @click="editSeries(row as CoreCourseSeries)">编辑</el-button>
          <el-button link type="danger" @click="removeSeries(row as CoreCourseSeries)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>
    <el-form scroll-to-error label-position="top" class="entity-form section-gap" @submit.prevent="saveSeries">
      <el-form-item :label="seriesForm.id ? '编辑系列名称' : '新增系列名称'">
        <el-input ref="seriesNameInput" v-model="seriesForm.name" maxlength="100" />
      </el-form-item>
      <el-form-item label="系列介绍"
        ><el-input v-model="seriesForm.description" type="textarea" maxlength="2000"
      /></el-form-item>
      <el-button v-if="seriesForm.id" @click="editSeries()">取消编辑</el-button>
      <el-button type="primary" :disabled="!seriesForm.name.trim()" :loading="seriesSaving" native-type="submit">
        {{ seriesForm.id ? '保存修改' : '新增系列' }}
      </el-button>
    </el-form>
  </el-dialog>

  <CourseTreeEditor v-model="treeVisible" :course-id="treeCourseId" @changed="onTreeChanged" />
</template>

<style scoped>
.course-description {
  margin: 4px 0 0;
  color: #5d6f80;
  font-size: 12px;
  line-height: 1.45;
  display: -webkit-box;
  overflow: hidden;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}

.structure-counts {
  color: #51687f;
  font-size: 13px;
}

.resource-breakdown {
  margin: 3px 0 0;
  color: #64788a;
  font-size: 11px;
  line-height: 1.4;
}

.course-form-grid {
  display: grid;
  grid-template-columns: minmax(0, 1.5fr) minmax(150px, 0.8fr);
  gap: 0 14px;
}
</style>

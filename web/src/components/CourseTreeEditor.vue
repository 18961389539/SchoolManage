<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import { Delete, EditPen, Files, Plus, UploadFilled } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, del, messageOf, post, put } from '../api'
import { courseTypeOptions, durationText, labelOf, resourceKindOptions } from '../teaching'
import type { CourseDetail, CourseResourceSummary } from '../types'
import ImportResourcesDialog from './ImportResourcesDialog.vue'
import ResourceEditorDialog from './ResourceEditorDialog.vue'
import ResourceFilesDialog from './ResourceFilesDialog.vue'
import { focusFirstField } from '../focusField'

const visible = defineModel<boolean>({ required: true })
const props = defineProps<{ courseId: string }>()
const emit = defineEmits<{ changed: [] }>()

const chapterNameInput = ref<{ focus?: () => void; $el?: HTMLElement }>()
const sectionNameInput = ref<{ focus?: () => void; $el?: HTMLElement }>()
function focusNamedInput(target?: { focus?: () => void; $el?: HTMLElement } | null) {
  const root = target?.$el
  if (root) focusFirstField(root)
  else target?.focus?.()
}
function focusChapterName() {
  focusNamedInput(chapterNameInput.value)
}
function focusSectionName() {
  focusNamedInput(sectionNameInput.value)
}

const course = ref<CourseDetail | null>(null)
const loading = ref(false)
const error = ref('')

const chapterDialog = ref(false)
const chapterSaving = ref(false)
const chapterForm = reactive({ id: '', name: '' })
const sectionDialog = ref(false)
const sectionSaving = ref(false)
const sectionForm = reactive({ id: '', chapterId: '', name: '', durationMinutes: 30 })
const resourceDialog = ref(false)
const editingSectionId = ref('')
const editingResourceId = ref<string | undefined>()
const filesDialog = ref(false)
const filesResourceId = ref('')
const filesResourceName = ref('')
const importDialog = ref(false)
const importSectionId = ref('')
const importSectionName = ref('')

async function load() {
  if (!props.courseId) return
  loading.value = true
  error.value = ''
  try {
    course.value = await api<CourseDetail>(`/courses/${props.courseId}`)
  } catch (failure) {
    course.value = null
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

watch(
  () => [visible.value, props.courseId],
  () => {
    if (visible.value) void load()
  },
  { immediate: true }
)

async function reload() {
  await load()
  emit('changed')
}

function openChapterCreate() {
  chapterForm.id = ''
  chapterForm.name = ''
  chapterDialog.value = true
}

function openChapterEdit(id: string, name: string) {
  chapterForm.id = id
  chapterForm.name = name
  chapterDialog.value = true
}

async function saveChapter() {
  if (!chapterForm.name.trim()) {
    ElMessage.warning('请填写大章名称')
    return
  }
  chapterSaving.value = true
  try {
    if (chapterForm.id) await put(`/chapters/${chapterForm.id}`, { name: chapterForm.name.trim() })
    else await post(`/courses/${props.courseId}/chapters`, { name: chapterForm.name.trim() })
    ElMessage.success(chapterForm.id ? '大章已更新' : '大章已创建')
    chapterDialog.value = false
    await reload()
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    chapterSaving.value = false
  }
}

function openSectionCreate(chapterId: string) {
  sectionForm.id = ''
  sectionForm.chapterId = chapterId
  sectionForm.name = ''
  sectionForm.durationMinutes = 30
  sectionDialog.value = true
}

function openSectionEdit(id: string, chapterId: string, name: string, durationMinutes: number) {
  sectionForm.id = id
  sectionForm.chapterId = chapterId
  sectionForm.name = name
  sectionForm.durationMinutes = durationMinutes
  sectionDialog.value = true
}

async function saveSection() {
  if (!sectionForm.name.trim()) {
    ElMessage.warning('请填写小节名称')
    return
  }
  sectionSaving.value = true
  try {
    const payload = { name: sectionForm.name.trim(), durationMinutes: sectionForm.durationMinutes }
    if (sectionForm.id) await put(`/sections/${sectionForm.id}`, payload)
    else await post(`/chapters/${sectionForm.chapterId}/sections`, payload)
    ElMessage.success(sectionForm.id ? '小节已更新' : '小节已创建')
    sectionDialog.value = false
    await reload()
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    sectionSaving.value = false
  }
}

async function confirmDelete(message: string, title: string) {
  try {
    await ElMessageBox.confirm(message, title, { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' })
    return true
  } catch {
    return false
  }
}

async function deleteChapter(id: string, name: string) {
  if (!(await confirmDelete(`删除“${name}”会同时删除其下的小节、资源和学习记录，是否继续？`, '删除大章'))) return
  try {
    await del(`/chapters/${id}`)
    ElMessage.success('大章已删除')
    await reload()
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  }
}

async function deleteSection(id: string, name: string) {
  if (!(await confirmDelete(`删除“${name}”会同时删除其下资源和学习记录，是否继续？`, '删除小节'))) return
  try {
    await del(`/sections/${id}`)
    ElMessage.success('小节已删除')
    await reload()
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  }
}

function openResourceCreate(sectionId: string) {
  editingSectionId.value = sectionId
  editingResourceId.value = undefined
  resourceDialog.value = true
}

function openResourceEdit(resource: CourseResourceSummary) {
  const section = course.value?.chapters
    .flatMap(chapter => chapter.sections)
    .find(item => item.resources.some(resourceItem => resourceItem.id === resource.id))
  editingSectionId.value = section?.id || ''
  editingResourceId.value = resource.id
  resourceDialog.value = true
}

async function onResourceSaved() {
  await reload()
}

function openFiles(resource: CourseResourceSummary) {
  filesResourceId.value = resource.id
  filesResourceName.value = resource.name
  filesDialog.value = true
}

function openImport(sectionId: string, sectionName: string) {
  importSectionId.value = sectionId
  importSectionName.value = sectionName
  importDialog.value = true
}

async function deleteResource(id: string, name: string) {
  if (!(await confirmDelete(`删除资源“${name}”会同时删除题目与关联学习记录，是否继续？`, '删除资源'))) return
  try {
    await del(`/resources/${id}`)
    ElMessage.success('资源已删除')
    await reload()
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  }
}
</script>

<template>
  <el-drawer v-model="visible" title="课程目录" :size="'min(880px, 96vw)'" destroy-on-close>
    <div v-if="loading" class="tree-loading">
      <el-skeleton :rows="6" animated />
    </div>
    <el-result v-else-if="error" icon="error" title="课程目录加载失败" :sub-title="error">
      <template #extra><el-button type="primary" @click="load">重试</el-button></template>
    </el-result>
    <template v-else-if="course">
      <div class="tree-heading">
        <div>
          <span class="eyebrow">COURSE STRUCTURE</span>
          <h2>{{ course.name }}</h2>
          <p>
            {{ labelOf(courseTypeOptions, course.type) }} · {{ course.resourceCount }} 个资源 ·
            {{ course.sectionCount }} 个小节
          </p>
        </div>
        <el-button type="primary" :icon="Plus" @click="openChapterCreate">新增大章</el-button>
      </div>
      <el-empty v-if="!course.chapters.length" description="课程还没有大章">
        <p class="tree-hint">按“大章 → 小节 → 资源”的顺序构建课程目录。</p>
      </el-empty>
      <div v-else class="course-tree">
        <section v-for="(chapter, chapterIndex) in course.chapters" :key="chapter.id" class="chapter-block">
          <header class="chapter-head">
            <div class="chapter-title">
              <span class="tree-index">第 {{ chapterIndex + 1 }} 章</span>
              <strong>{{ chapter.name }}</strong>
              <small>{{ chapter.sections.length }} 个小节</small>
            </div>
            <div class="tree-actions">
              <el-button link type="primary" :icon="Plus" @click="openSectionCreate(chapter.id)">新增小节</el-button>
              <el-button link :icon="EditPen" @click="openChapterEdit(chapter.id, chapter.name)">编辑</el-button>
              <el-button link type="danger" :icon="Delete" @click="deleteChapter(chapter.id, chapter.name)"
                >删除</el-button
              >
            </div>
          </header>
          <p v-if="!chapter.sections.length" class="tree-empty">尚未添加小节</p>
          <div v-for="section in chapter.sections" :key="section.id" class="section-block">
            <header class="section-head">
              <div>
                <strong>{{ section.name }}</strong>
                <span>{{ durationText(section.durationMinutes) }}</span>
              </div>
              <div class="tree-actions">
                <el-button link type="primary" :icon="Plus" @click="openResourceCreate(section.id)">新增资源</el-button>
                <el-button link :icon="UploadFilled" @click="openImport(section.id, section.name)">导入资源</el-button>
                <el-button
                  link
                  :icon="EditPen"
                  @click="openSectionEdit(section.id, chapter.id, section.name, section.durationMinutes)"
                >
                  编辑
                </el-button>
                <el-button link type="danger" :icon="Delete" @click="deleteSection(section.id, section.name)"
                  >删除</el-button
                >
              </div>
            </header>
            <p v-if="!section.resources.length" class="tree-empty">尚未添加资源</p>
            <ul v-else class="resource-list">
              <li v-for="resource in section.resources" :key="resource.id">
                <el-tag size="small" effect="plain">{{ labelOf(resourceKindOptions, resource.kind) }}</el-tag>
                <span class="resource-name">{{ resource.name }}</span>
                <span class="resource-meta">
                  {{
                    resource.kind === 'theoryPractice'
                      ? `${resource.questionCount} 题`
                      : durationText(resource.durationMinutes)
                  }}
                </span>
                <div class="resource-actions">
                  <el-button link type="primary" :icon="EditPen" @click="openResourceEdit(resource)">编辑</el-button>
                  <el-button link :icon="Files" @click="openFiles(resource)">
                    附件{{ resource.fileCount ? ` ${resource.fileCount}` : '' }}
                  </el-button>
                  <el-button link type="danger" :icon="Delete" @click="deleteResource(resource.id, resource.name)"
                    >删除</el-button
                  >
                </div>
              </li>
            </ul>
          </div>
        </section>
      </div>
    </template>

    <el-dialog
      v-model="chapterDialog"
      :title="chapterForm.id ? '编辑大章' : '新增大章'"
      width="min(460px, 94vw)"
      @opened="focusChapterName"
    >
      <el-form label-position="top" class="entity-form">
        <el-form-item label="大章名称" required>
          <el-input
            ref="chapterNameInput"
            v-model="chapterForm.name"
            maxlength="100"
            show-word-limit
            @keyup.enter="saveChapter"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="chapterDialog = false">取消</el-button>
        <el-button type="primary" :loading="chapterSaving" @click="saveChapter">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog
      v-model="sectionDialog"
      :title="sectionForm.id ? '编辑小节' : '新增小节'"
      width="min(480px, 94vw)"
      @opened="focusSectionName"
    >
      <el-form label-position="top" class="entity-form">
        <el-form-item label="小节名称" required>
          <el-input ref="sectionNameInput" v-model="sectionForm.name" maxlength="100" show-word-limit />
        </el-form-item>
        <el-form-item label="预计时长（分钟）">
          <el-input-number v-model="sectionForm.durationMinutes" :min="1" :max="600" controls-position="right" />
          <p class="field-help">{{ durationText(sectionForm.durationMinutes) }}</p>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="sectionDialog = false">取消</el-button>
        <el-button type="primary" :loading="sectionSaving" @click="saveSection">保存</el-button>
      </template>
    </el-dialog>

    <ResourceEditorDialog
      v-model="resourceDialog"
      :section-id="editingSectionId"
      :resource-id="editingResourceId"
      @saved="onResourceSaved"
    />
    <ResourceFilesDialog
      v-model="filesDialog"
      :resource-id="filesResourceId"
      :resource-name="filesResourceName"
      @changed="reload"
    />
    <ImportResourcesDialog
      v-model="importDialog"
      :section-id="importSectionId"
      :section-name="importSectionName"
      @changed="reload"
    />
  </el-drawer>
</template>

<style scoped>
.tree-loading {
  padding: 18px 0;
}

.tree-heading {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 16px;
  padding-bottom: 18px;
  border-bottom: 1px solid #dce7eb;
  margin-bottom: 18px;
}

.tree-heading h2 {
  margin: 4px 0 0;
  color: #1c334e;
  font-size: 21px;
}

.tree-heading p {
  margin: 5px 0 0;
  color: #61758b;
  font-size: 13px;
}

.tree-hint {
  color: #61758b;
  font-size: 13px;
}

.course-tree {
  display: grid;
  gap: 14px;
}

.chapter-block {
  padding: 16px;
  background: #f6faf8;
  border: 1px solid #dce7eb;
  border-radius: 10px;
}

.chapter-head,
.section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.chapter-title {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
}

.chapter-title strong {
  color: #19344f;
  font-size: 16px;
}

.chapter-title small {
  color: #56697c;
  font-size: 12px;
}

.tree-index {
  padding: 2px 7px;
  color: #145a50;
  font-size: 12px;
  background: #e3f3ee;
  border-radius: 4px;
}

.tree-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 2px;
}

.tree-empty {
  margin: 12px 0 0;
  color: #56697c;
  font-size: 12px;
}

.section-block {
  padding: 12px 0 0 14px;
  margin-top: 12px;
  border-top: 1px solid #dce7eb;
}

.section-head strong {
  color: #294761;
  font-size: 14px;
}

.section-head span {
  margin-left: 9px;
  color: #56697c;
  font-size: 12px;
}

.resource-list {
  list-style: none;
  margin: 9px 0 0;
  padding: 0;
  display: grid;
  gap: 6px;
}

.resource-list li {
  display: flex;
  align-items: center;
  gap: 9px;
  padding: 8px 10px;
  background: #fff;
  border: 1px solid #e4eeea;
  border-radius: 7px;
}

.resource-name {
  flex: 1;
  min-width: 0;
  overflow-wrap: anywhere;
  color: #253e56;
  font-size: 13px;
}

.resource-meta {
  color: #5d6f80;
  font-size: 12px;
  white-space: nowrap;
}

.resource-actions {
  display: flex;
  flex: 0 0 auto;
  gap: 2px;
}

@media (max-width: 700px) {
  .tree-heading,
  .chapter-head,
  .section-head {
    align-items: flex-start;
    flex-direction: column;
  }

  .resource-list li {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
  }

  .resource-meta {
    grid-column: 2;
  }

  .resource-actions {
    grid-column: 2;
  }
}
</style>

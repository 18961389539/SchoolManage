<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { focusFirstField } from '../focusField'
import { Refresh, Search } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, messageOf, post } from '../api'
import { durationText, labelOf, resourceKindOptions } from '../teaching'
import type { CourseImportableResource, CourseImportableResponse } from '../types'

const visible = defineModel<boolean>({ required: true })
const props = defineProps<{ sectionId: string; sectionName: string }>()
const emit = defineEmits<{ changed: [] }>()

const items = ref<CourseImportableResource[]>([])
const targetName = ref('')
const loading = ref(false)
const importing = ref(false)
const error = ref('')
const search = ref('')
const searchInput = ref<{ focus?: () => void; $el?: HTMLElement }>()
const selected = ref<CourseImportableResource[]>([])

async function confirmDiscard() {
  if (importing.value) {
    ElMessage.warning('正在导入资源，请等待完成')
    return false
  }
  if (!selected.value.length) return true
  try {
    await ElMessageBox.confirm('已勾选的资源还没有导入，关闭后需要重新选择。', '关闭导入资源？', {
      type: 'warning',
      confirmButtonText: '关闭',
      cancelButtonText: '继续选择'
    })
    return true
  } catch {
    return false
  }
}

async function handleBeforeClose(done: () => void) {
  if (await confirmDiscard()) done()
}

async function cancel() {
  if (await confirmDiscard()) visible.value = false
}

const selectedIds = computed(() => selected.value.map(item => item.id))
const summary = computed(() => {
  if (!selected.value.length) return '尚未选择资源'
  const questions = selected.value.reduce((total, item) => total + item.questionCount, 0)
  const files = selected.value.reduce((total, item) => total + item.fileCount, 0)
  return `已选 ${selected.value.length} 个资源 · ${questions} 道题 · ${files} 个附件`
})

async function load() {
  if (!props.sectionId) return
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams()
    if (search.value.trim()) params.set('search', search.value.trim())
    const data = await api<CourseImportableResponse>(
      `/sections/${props.sectionId}/importable-resources?${params.toString()}`
    )
    items.value = data.items
    targetName.value = data.target.name
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

watch(
  () => [visible.value, props.sectionId],
  () => {
    if (visible.value) {
      search.value = ''
      selected.value = []
      void load()
    }
  },
  { immediate: true }
)

function onSelectionChange(rows: CourseImportableResource[]) {
  selected.value = rows
}

function onDialogOpened() {
  const root = searchInput.value?.$el
  if (root) focusFirstField(root)
  else searchInput.value?.focus?.()
}

function applySearch() {
  selected.value = []
  void load()
}

async function confirmImport() {
  if (!selectedIds.value.length) {
    error.value = '请选择要导入的资源'
    return
  }
  importing.value = true
  error.value = ''
  try {
    const result = await post<{ resources: number; questions: number; files: number }>(
      `/sections/${props.sectionId}/resources/import`,
      { resourceIds: selectedIds.value }
    )
    ElMessage.success(`已导入 ${result.resources} 个资源、${result.questions} 道题、${result.files} 个附件`)
    emit('changed')
    visible.value = false
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    importing.value = false
  }
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="`导入资源到「${props.sectionName || targetName || '小节'}」`"
    width="min(880px, 96vw)"
    :before-close="handleBeforeClose"
    destroy-on-close
    @opened="onDialogOpened"
  >
    <el-alert type="info" :closable="false" show-icon class="import-hint">
      从其他小节的课程资源复制一份到当前小节，题目和附件会一并复制，不影响原资源。
    </el-alert>
    <div class="import-toolbar">
      <el-input
        ref="searchInput"
        v-model="search"
        class="search-box"
        clearable
        placeholder="搜索资源名称或课程"
        :prefix-icon="Search"
        @keyup.enter="applySearch"
        @clear="applySearch"
      />
      <el-button :icon="Search" @click="applySearch">查询</el-button>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      <span class="record-count">{{ summary }}</span>
    </div>
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
    <el-table
      v-loading="loading"
      :data="items"
      row-key="id"
      max-height="460"
      :empty-text="search.trim() ? '没有符合条件的资源，试试清除搜索。' : '暂无可导入的资源'"
      @selection-change="onSelectionChange"
    >
      <el-table-column type="selection" width="46" />
      <el-table-column prop="name" label="资源" min-width="180" show-overflow-tooltip />
      <el-table-column label="类型" width="105">
        <template #default="{ row }">
          <el-tag size="small" effect="plain">{{ labelOf(resourceKindOptions, row.kind) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="来源" min-width="210">
        <template #default="{ row }">
          <div class="source-path">{{ row.courseName }}</div>
          <div class="source-sub">{{ row.chapterName }} / {{ row.sectionName }}</div>
        </template>
      </el-table-column>
      <el-table-column label="时长" width="105">
        <template #default="{ row }">{{ durationText(row.durationMinutes) }}</template>
      </el-table-column>
      <el-table-column label="题目" width="75">
        <template #default="{ row }">{{ row.questionCount }}</template>
      </el-table-column>
      <el-table-column label="附件" width="75">
        <template #default="{ row }">{{ row.fileCount }}</template>
      </el-table-column>
    </el-table>
    <template #footer>
      <el-button @click="cancel">取消</el-button>
      <el-button type="primary" :loading="importing" :disabled="!selectedIds.length" @click="confirmImport">
        导入所选资源
      </el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.import-hint {
  margin-bottom: 14px;
}

.import-toolbar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 9px;
  margin-bottom: 12px;
}

.import-toolbar .record-count {
  margin-left: auto;
  color: #61758b;
  font-size: 12px;
}

.source-path {
  color: #2c4a66;
  font-size: 13px;
  overflow-wrap: anywhere;
}

.source-sub {
  margin-top: 2px;
  color: #56697c;
  font-size: 11px;
}
</style>

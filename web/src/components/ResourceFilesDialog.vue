<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { Delete, Download, Files, Plus, Refresh } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, bytes, dateTime, del, downloadFile, messageOf, post, put } from '../api'
import { appState, maxUploadMegabytes } from '../store'
import { focusFirstField } from '../focusField'
import type { CourseResourceDetail, CourseResourceFile } from '../types'
import TableEmpty from './TableEmpty.vue'

const visible = defineModel<boolean>({ required: true })
const props = defineProps<{ resourceId: string; resourceName: string }>()
const emit = defineEmits<{ changed: [] }>()

const files = ref<CourseResourceFile[]>([])
const loading = ref(false)
const uploading = ref(false)
const error = ref('')
const uploadInput = ref<HTMLInputElement>()
const dialogBody = ref<HTMLElement | null>(null)
const refreshButton = ref<{ $el?: HTMLElement } | null>(null)

async function onOpened() {
  await nextTick()
  if (focusFirstField(dialogBody.value)) return
  const rootEl = refreshButton.value?.$el ?? (refreshButton.value as unknown as HTMLElement | undefined)
  const button = rootEl?.querySelector?.('button') ?? rootEl
  button?.focus()
}

const title = computed(() => `${props.resourceName || '资源'} · 附件`)
const maxMegabytes = maxUploadMegabytes()

async function load() {
  if (!props.resourceId) return
  loading.value = true
  error.value = ''
  try {
    const detail = await api<CourseResourceDetail>(`/resources/${props.resourceId}`)
    files.value = detail.files
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

watch(
  () => [visible.value, props.resourceId],
  () => {
    if (visible.value) void load()
  },
  { immediate: true }
)

function openUpload() {
  uploadInput.value?.click()
}

async function uploadFile(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
  if (file.size > appState.maxUploadBytes) {
    error.value = `附件不能超过 ${maxMegabytes}MB`
    return
  }
  await sendUpload(file)
}

async function sendUpload(file: File) {
  uploading.value = true
  error.value = ''
  try {
    const form = new FormData()
    form.append('file', file)
    await api<CourseResourceFile>(`/resources/${props.resourceId}/files`, { method: 'POST', body: form })
    ElMessage.success('附件已上传')
    await load()
    emit('changed')
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    uploading.value = false
  }
}

async function rename(file: CourseResourceFile) {
  try {
    const result = await ElMessageBox.prompt('附件名称', '重命名附件', {
      inputValue: file.name,
      confirmButtonText: '保存',
      cancelButtonText: '取消',
      inputValidator: value => (value || '').trim().length > 0 && (value || '').trim().length <= 100
    })
    await put(`/course-files/${file.id}`, { name: result.value.trim() })
    ElMessage.success('附件已重命名')
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

async function togglePin(file: CourseResourceFile) {
  try {
    await post(`/course-files/${file.id}/pin`, { pinned: !file.isPinned })
    ElMessage.success(file.isPinned ? '已取消置顶' : '附件已置顶')
    await load()
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  }
}

async function remove(file: CourseResourceFile) {
  try {
    await ElMessageBox.confirm(`确定删除附件“${file.name}”吗？文件将从服务器移除。`, '删除附件', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消'
    })
    await del(`/course-files/${file.id}`)
    ElMessage.success('附件已删除')
    await load()
    emit('changed')
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

async function download(file: CourseResourceFile) {
  try {
    await downloadFile(`/course-files/${file.id}/download`, file.name)
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  }
}
</script>

<template>
  <el-dialog v-model="visible" :title="title" width="min(760px, 96vw)" destroy-on-close @opened="onOpened">
    <div ref="dialogBody" class="file-dialog-body">
      <div class="file-toolbar">
        <span class="file-hint">单个附件不超过 {{ maxMegabytes }}MB，单个资源最多 50 个附件。</span>
        <div class="file-actions">
          <el-button ref="refreshButton" :icon="Refresh" :loading="loading" :disabled="uploading" @click="load"
            >刷新</el-button
          >
          <el-button type="primary" :icon="Plus" :loading="uploading" @click="openUpload">上传附件</el-button>
          <input ref="uploadInput" class="upload-input" type="file" @change="uploadFile" />
        </div>
      </div>
      <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
      <el-table v-loading="loading" :data="files" row-key="id" empty-text="">
        <template #empty>
          <TableEmpty v-if="!loading" title="暂无附件" hint="点击“上传附件”，可为该资源补充讲义、参考资料或其他文件。">
            <template #action>
              <el-button type="primary" plain :loading="uploading" @click="openUpload">上传附件</el-button>
            </template>
          </TableEmpty>
        </template>
        <el-table-column label="附件名称" min-width="230">
          <template #default="{ row }">
            <div class="file-name">
              <el-icon><Files /></el-icon>
              <span>{{ row.name }}</span>
              <el-tag v-if="row.isPinned" size="small" type="warning" effect="plain">置顶</el-tag>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="大小" width="100">
          <template #default="{ row }">{{ bytes(row.size) }}</template>
        </el-table-column>
        <el-table-column label="上传时间" width="170">
          <template #default="{ row }">{{ dateTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="280" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" :icon="Download" @click="download(row as CourseResourceFile)"
              >下载</el-button
            >
            <el-button link :icon="Plus" @click="togglePin(row as CourseResourceFile)">
              {{ row.isPinned ? '取消置顶' : '置顶' }}
            </el-button>
            <el-button link @click="rename(row as CourseResourceFile)">重命名</el-button>
            <el-button link type="danger" :icon="Delete" @click="remove(row as CourseResourceFile)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>
  </el-dialog>
</template>

<style scoped>
.file-toolbar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 10px;
  margin-bottom: 14px;
}

.file-hint {
  color: #61758b;
  font-size: 12px;
}

.file-actions {
  display: flex;
  gap: 8px;
}

.upload-input {
  display: none;
}

.file-name {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 7px;
  min-width: 0;
}

.file-name span {
  overflow-wrap: anywhere;
}
</style>

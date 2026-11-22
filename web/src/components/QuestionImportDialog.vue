<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Download, UploadFilled } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { api, messageOf } from '../api'
import { downloadFile } from '../exam'
import type { CourseQuestion, CourseQuestionParseError, CourseQuestionParseResponse } from '../types'

const visible = defineModel<boolean>({ required: true })
const emit = defineEmits<{ imported: [questions: Omit<CourseQuestion, 'id' | 'sortOrder'>[]] }>()

const file = ref<File | null>(null)
const removeInvalidRows = ref(false)
const parsing = ref(false)
const error = ref('')
const errors = ref<CourseQuestionParseError[]>([])
const summary = ref('')
const fileInput = ref<HTMLInputElement>()

const fileName = computed(() => file.value?.name || '未选择文件')

watch(visible, value => {
  if (value) reset()
})

function reset() {
  file.value = null
  removeInvalidRows.value = false
  error.value = ''
  errors.value = []
  summary.value = ''
}

function chooseFile() {
  fileInput.value?.click()
}

function onFileChange(event: Event) {
  const input = event.target as HTMLInputElement
  file.value = input.files?.[0] || null
  input.value = ''
  errors.value = []
  error.value = ''
}

async function downloadTemplate() {
  try {
    await downloadFile('/courses/questions/template', '课程练习导入模板.csv')
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  }
}

async function parse() {
  if (!file.value) {
    error.value = '请选择 Excel 或 CSV 文件'
    return
  }
  parsing.value = true
  error.value = ''
  errors.value = []
  summary.value = ''
  try {
    const form = new FormData()
    form.append('file', file.value)
    form.append('removeInvalidRows', String(removeInvalidRows.value))
    const result = await api<CourseQuestionParseResponse>('/courses/questions/parse', {
      method: 'POST',
      body: form
    })
    emit('imported', result.questions)
    ElMessage.success(
      result.invalidCount > 0
        ? `已解析 ${result.validCount} 道有效题目，忽略 ${result.invalidCount} 处问题`
        : `已解析 ${result.validCount} 道题目`
    )
    visible.value = false
  } catch (failure) {
    const details = (failure as { details?: { errors?: CourseQuestionParseError[]; validCount?: number } }).details
    if (details?.errors?.length) {
      errors.value = details.errors
      summary.value = `发现 ${details.errors.length} 处问题，可勾选“忽略问题行”后重新解析。`
    } else {
      error.value = messageOf(failure)
    }
  } finally {
    parsing.value = false
  }
}
</script>

<template>
  <el-dialog v-model="visible" title="批量导入练习题目" width="min(760px, 96vw)" destroy-on-close>
    <el-alert type="info" :closable="false" show-icon class="import-hint">
      <p>支持 Excel（.xlsx）或 CSV。表头为：题型、题干、选项、正确答案、分值、解析；选项和答案用竖线或逗号分隔。</p>
      <p>课程练习只支持单选、多选和判断题；导入的题目会追加到当前列表，保存资源后生效。</p>
    </el-alert>
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
    <div class="import-picker">
      <el-button :icon="UploadFilled" @click="chooseFile">选择文件</el-button>
      <span class="import-file">{{ fileName }}</span>
      <input ref="fileInput" class="upload-input" type="file" accept=".xlsx,.csv" @change="onFileChange" />
    </div>
    <div class="import-options">
      <el-checkbox v-model="removeInvalidRows">忽略存在问题的行，仅导入有效题目</el-checkbox>
      <el-button link type="primary" :icon="Download" @click="downloadTemplate">下载 CSV 模板</el-button>
    </div>
    <template v-if="errors.length">
      <p class="import-summary">{{ summary }}</p>
      <el-table :data="errors" max-height="260" size="small" border>
        <el-table-column prop="rowNumber" label="行号" width="70" />
        <el-table-column prop="column" label="列" width="100" />
        <el-table-column prop="message" label="问题" min-width="220" show-overflow-tooltip />
        <el-table-column prop="stem" label="题干" min-width="180" show-overflow-tooltip />
      </el-table>
    </template>
    <template #footer>
      <el-button @click="visible = false">取消</el-button>
      <el-button type="primary" :loading="parsing" :disabled="!file" @click="parse">解析并导入</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.import-hint p {
  margin: 0 0 4px;
}

.import-hint p:last-child {
  margin-bottom: 0;
}

.import-picker {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 10px;
  margin: 16px 0 10px;
}

.import-file {
  color: #4e657b;
  font-size: 13px;
  overflow-wrap: anywhere;
}

.import-options {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 8px;
  margin-bottom: 12px;
}

.import-summary {
  margin: 0 0 8px;
  color: #8a6d3b;
  font-size: 12px;
}

.upload-input {
  display: none;
}
</style>

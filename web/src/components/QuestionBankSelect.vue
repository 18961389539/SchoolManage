<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api, messageOf } from '../api'
import { bankAccepts, examQuestionBankLabels } from '../questionBanks'
import type { ExamQuestionBank, ExamQuestionType, Paged } from '../types'

const chosenId = defineModel<string>({ required: true })
const props = defineProps<{
  placeholder: string
  initial?: ExamQuestionBank
  clearable?: boolean
  questionType?: ExamQuestionType
}>()
const emit = defineEmits<{ change: [] }>()
const chosen = ref<ExamQuestionBank | undefined>(props.initial)
const results = ref<ExamQuestionBank[]>([])
const busy = ref(false)
const error = ref('')
let requestVersion = 0

const options = computed(() => {
  const questionType = props.questionType
  const visible = questionType ? results.value.filter(bank => bankAccepts(bank.type, questionType)) : results.value
  if (chosen.value && !visible.some(bank => bank.id === chosen.value?.id)) return [chosen.value, ...visible]
  return visible
})

async function load(search = '') {
  const version = ++requestVersion
  busy.value = true
  error.value = ''
  try {
    const params = new URLSearchParams({ search, pageSize: '50' })
    const data = await api<Paged<ExamQuestionBank>>(`/exam-question-banks?${params}`)
    if (version === requestVersion) results.value = data.items
  } catch (failure) {
    if (version === requestVersion) error.value = messageOf(failure)
  } finally {
    if (version === requestVersion) busy.value = false
  }
}

function changed(value: string) {
  chosen.value = options.value.find(item => item.id === value)
  emit('change')
}

onMounted(() => void load())
</script>

<template>
  <el-select
    v-model="chosenId"
    :placeholder="placeholder"
    filterable
    remote
    :remote-method="load"
    :clearable="clearable"
    value-on-clear=""
    :loading="busy"
    :no-data-text="error || '未找到题库'"
    @change="changed"
    @visible-change="
      open => {
        if (open) load()
      }
    "
  >
    <el-option v-for="bank in options" :key="bank.id" :label="bank.name" :value="bank.id">
      <span>{{ bank.name }}</span>
      <small class="bank-kind">{{ examQuestionBankLabels[bank.type] }}</small>
    </el-option>
  </el-select>
</template>

<style scoped>
.bank-kind {
  margin-left: 12px;
  color: var(--el-text-color-secondary);
}
</style>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { api, messageOf } from '../api'
import type { Paged, TeachingClassesResponse } from '../types'

interface StudentOption {
  userId: string
  name: string
  number: string
  className: string
}
const selected = defineModel<string[]>({ required: true })
const props = defineProps<{ teaching: TeachingClassesResponse }>()
const profession = ref('')
const classroom = ref('')
const rows = ref<StudentOption[]>([])
const cache = new Map<string, StudentOption>()
const error = ref('')
const loading = ref(false)
let requestId = 0
const classes = computed(() =>
  props.teaching.classes.filter(item => !profession.value || item.professionId === profession.value)
)
const options = computed(() => [
  ...new Map([
    ...selected.value.flatMap(id => (cache.has(id) ? [[id, cache.get(id)!] as const] : [])),
    ...rows.value.map(row => [row.userId, row] as const)
  ]).values()
])

async function search(keyword = '') {
  const request = ++requestId
  loading.value = true
  error.value = ''
  const params = new URLSearchParams({
    pageSize: '100',
    search: keyword.trim(),
    classId: classroom.value,
    professionId: profession.value
  })
  try {
    const page = await api<Paged<StudentOption>>(`/teaching/students?${params}`)
    if (request !== requestId) return
    rows.value = page.items
    for (const row of rows.value) cache.set(row.userId, row)
  } catch (failure) {
    if (request === requestId) error.value = messageOf(failure)
  } finally {
    if (request === requestId) loading.value = false
  }
}

function changeProfession() {
  classroom.value = ''
  void search()
}
</script>

<template>
  <el-form-item label="选择具体学生">
    <div class="picker">
      <div class="filters">
        <el-select v-model="profession" clearable filterable placeholder="筛选专业" @change="changeProfession">
          <el-option v-for="item in teaching.professions" :key="item.id" :label="item.name" :value="item.id" />
        </el-select>
        <el-select v-model="classroom" clearable filterable placeholder="筛选班级" @change="search()">
          <el-option v-for="item in classes" :key="item.id" :label="item.name" :value="item.id" />
        </el-select>
      </div>
      <el-select
        v-model="selected"
        multiple
        filterable
        remote
        collapse-tags
        :loading="loading"
        :remote-method="search"
        placeholder="按姓名、学号或账号搜索学生"
        @visible-change="
          open => {
            if (open) search()
          }
        "
      >
        <el-option
          v-for="item in options"
          :key="item.userId"
          :label="`${item.name} · ${item.number} · ${item.className || '未分班'}`"
          :value="item.userId"
        />
      </el-select>
      <el-alert v-if="error" type="error" :title="error" :closable="false" />
    </div>
  </el-form-item>
</template>

<style scoped>
.picker {
  display: grid;
  gap: 8px;
  width: 100%;
}
.filters {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
}
</style>

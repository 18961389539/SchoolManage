<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ArrowLeft, Reading, Refresh } from '@element-plus/icons-vue'
import { api, messageOf } from '../api'
import type { StudentStatsDetail } from '../analytics'
import StudentStatsDetailView from './StudentStatsDetail.vue'

const props = defineProps<{ classroomId: string; backKey?: string }>()
const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()

const loading = ref(false)
const error = ref('')
const detail = ref<StudentStatsDetail | null>(null)

async function load() {
  if (!props.classroomId) {
    error.value = '未指定课堂'
    return
  }
  loading.value = true
  error.value = ''
  try {
    detail.value = await api<StudentStatsDetail>(`/classrooms/${props.classroomId}/statistics/mine`)
  } catch (failure) {
    error.value = messageOf(failure)
    detail.value = null
  } finally {
    loading.value = false
  }
}

function back() {
  const key = props.backKey || 'my-classrooms'
  if (key === 'classroom-content') emit('navigate', key, { classroomId: props.classroomId })
  else emit('navigate', key)
}

function enterClassroom() {
  if (detail.value)
    emit('navigate', 'classroom-content', { classroomId: detail.value.classroom.id, backKey: 'classroom-statistics' })
}

onMounted(load)
</script>

<template>
  <section class="page-heading">
    <div>
      <h1>{{ detail ? detail.classroom.name : '我的课堂统计' }}</h1>
      <p>
        {{
          detail ? `${detail.classroom.courseName} · 学习进度与练习记录` : '查看本课堂的学习进度、成绩单与练习历史。'
        }}
      </p>
    </div>
    <div class="heading-actions">
      <el-button :icon="ArrowLeft" @click="back">返回</el-button>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      <el-button v-if="detail" type="primary" :icon="Reading" @click="enterClassroom">进入课堂</el-button>
    </div>
  </section>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />

  <div v-loading="loading" class="stats-page-body">
    <StudentStatsDetailView v-if="detail" :detail="detail" />
    <el-empty v-else-if="!loading && !error" description="暂无可查看的课堂统计" />
  </div>
</template>

<style scoped>
.heading-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.stats-page-body {
  min-height: 220px;
}
</style>

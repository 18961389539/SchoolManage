<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { DataAnalysis, Refresh, School, Tickets } from '@element-plus/icons-vue'
import { api, messageOf } from '../api'
import type { TeacherHomeResponse } from '../analytics'
import HomeClassroomsPanel from './HomeClassroomsPanel.vue'
import HomeLeaderboard from './HomeLeaderboard.vue'
import '../role-home.css'

const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()
const loading = ref(false)
const error = ref('')
const data = ref<TeacherHomeResponse | null>(null)

const passRate = computed(() => {
  if (!data.value?.overview.managedStudents) return 0
  return Math.round((data.value.overview.passedStudents / data.value.overview.managedStudents) * 100)
})

async function load() {
  loading.value = true
  error.value = ''
  try {
    data.value = await api<TeacherHomeResponse>('/home/teacher')
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

function enterClassroom(id: string) {
  emit('navigate', 'classroom-content', { classroomId: id, backKey: 'teacher-home' })
}

function openStatistics(id: string) {
  emit('navigate', 'classrooms', { statistics: id })
}

onMounted(load)
</script>

<template>
  <section class="page-heading role-home-heading">
    <div>
      <h1>教学首页</h1>
      <p v-if="data">{{ data.teacher.displayName }}</p>
    </div>
    <div class="heading-actions">
      <el-button type="primary" :icon="School" @click="emit('navigate', 'classrooms')">我的课堂</el-button>
      <el-button :icon="Tickets" @click="emit('navigate', 'exams')">考试管理</el-button>
      <el-button :icon="DataAnalysis" @click="emit('navigate', 'skill-graph')">技能图谱</el-button>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新数据</el-button>
    </div>
  </section>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
  <el-skeleton v-if="loading && !data" :rows="5" animated class="surface role-home-loading" />

  <div v-if="data" v-loading="loading" class="role-home-grid">
    <HomeClassroomsPanel
      :classrooms="data.classrooms"
      teacher
      @all="emit('navigate', 'classrooms')"
      @enter="enterClassroom"
      @statistics="openStatistics"
    />

    <div class="role-home-stack">
      <section class="surface role-overview" aria-labelledby="teaching-overview-title">
        <header class="card-heading"><h2 id="teaching-overview-title">课堂概况</h2></header>
        <div class="role-overview-goal">
          <div>
            <span>学生达标率</span>
            <strong v-if="data.overview.managedStudents">{{ passRate }}%</strong>
            <strong v-else aria-hidden="true">—</strong>
          </div>
          <template v-if="data.overview.managedStudents">
            <el-progress
              :percentage="passRate"
              :show-text="false"
              :stroke-width="6"
              color="#79dbc4"
              aria-label="学生达标率"
            />
            <p>已达标 {{ data.overview.passedStudents }} / {{ data.overview.managedStudents }} 名学生</p>
          </template>
          <p v-else>暂无课堂学生</p>
        </div>
        <dl class="role-overview-metrics">
          <div>
            <dt>今日活跃学生</dt>
            <dd>{{ data.today.activeStudents }} <small>人</small></dd>
          </div>
          <div>
            <dt>今日达标学生</dt>
            <dd>{{ data.today.passedStudents }} <small>人</small></dd>
          </div>
          <div>
            <dt>资源学习率</dt>
            <dd>{{ data.overview.managedStudents ? `${data.overview.learningRate}%` : '—' }}</dd>
          </div>
          <div>
            <dt>资源达标率</dt>
            <dd>{{ data.overview.managedStudents ? `${data.overview.resourcePassRate}%` : '—' }}</dd>
          </div>
        </dl>
        <p class="role-overview-caption">覆盖 {{ data.overview.managedClasses }} 个班级</p>
      </section>

      <HomeLeaderboard :rows="data.leaderboard" teacher />
    </div>
  </div>
</template>

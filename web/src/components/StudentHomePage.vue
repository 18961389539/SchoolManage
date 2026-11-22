<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { DataAnalysis, Refresh, Tickets } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import type { StudentHomeResponse } from '../analytics'
import HomeClassroomsPanel from './HomeClassroomsPanel.vue'
import HomeLeaderboard from './HomeLeaderboard.vue'
import '../role-home.css'

const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()
const loading = ref(false)
const error = ref('')
const data = ref<StudentHomeResponse | null>(null)

const kindRows = computed(() => {
  if (!data.value) return []
  const kinds = data.value.kinds
  return [
    { key: 'theory', label: '理论知识', stats: kinds.theory },
    { key: 'theoryPractice', label: '理论练习', stats: kinds.theoryPractice },
    { key: 'case', label: '案例教学', stats: kinds.case },
    { key: 'casePractice', label: '案例实操', stats: kinds.casePractice }
  ]
})

async function load() {
  loading.value = true
  error.value = ''
  try {
    data.value = await api<StudentHomeResponse>('/home/student')
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

function enterClassroom(id: string) {
  emit('navigate', 'classroom-content', { classroomId: id, backKey: 'student-home' })
}

function openStatistics(id: string) {
  emit('navigate', 'classroom-statistics', { classroomId: id, backKey: 'student-home' })
}

onMounted(load)
</script>

<template>
  <section class="page-heading role-home-heading">
    <div>
      <h1>学习首页</h1>
      <p v-if="data">
        {{ data.student.name }} · {{ data.student.className || '未分班'
        }}<template v-if="data.student.professionName"> · {{ data.student.professionName }}</template
        ><template v-if="data.student.number"> · 学号 {{ data.student.number }}</template>
      </p>
    </div>
    <div class="heading-actions">
      <el-button type="primary" :icon="Tickets" @click="emit('navigate', 'my-exams')">我的考试</el-button>
      <el-button :icon="DataAnalysis" @click="emit('navigate', 'skill-graph')">技能图谱</el-button>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新数据</el-button>
    </div>
  </section>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
  <el-skeleton v-if="loading && !data" :rows="5" animated class="surface role-home-loading" />

  <div v-if="data" v-loading="loading" class="role-home-grid">
    <HomeClassroomsPanel
      :classrooms="data.classrooms"
      @all="emit('navigate', 'my-classrooms')"
      @enter="enterClassroom"
      @statistics="openStatistics"
    />

    <div class="role-home-stack">
      <section class="surface role-overview" aria-labelledby="learning-overview-title">
        <header class="card-heading"><h2 id="learning-overview-title">学习概况</h2></header>
        <div class="role-overview-goal">
          <div>
            <span>资源达标率</span>
            <strong v-if="data.overview.totalResources">{{ data.overview.passRate }}%</strong>
            <strong v-else aria-hidden="true">—</strong>
          </div>
          <template v-if="data.overview.totalResources">
            <el-progress
              :percentage="data.overview.passRate"
              :show-text="false"
              :stroke-width="6"
              :color="data.overview.isPassing ? '#79dbc4' : '#f2c780'"
              aria-label="资源达标率"
            />
            <p>
              已达标 {{ data.overview.passed }} / {{ data.overview.totalResources }} · 达标线 60%
              <template v-if="data.overview.isPassing"> · 已达标</template>
            </p>
          </template>
          <p v-else>暂无课堂资源</p>
        </div>
        <dl class="role-overview-metrics">
          <div>
            <dt>今日学习资源</dt>
            <dd>{{ data.today.learned }} <small>项</small></dd>
          </div>
          <div>
            <dt>今日练习</dt>
            <dd>{{ data.today.practices }} <small>次</small></dd>
          </div>
          <div>
            <dt>已学习资源</dt>
            <dd>
              {{ data.overview.learned }} <small>/ {{ data.overview.totalResources }} 项</small>
              <small v-if="data.overview.totalResources" class="learning-rate"
                >学习率 {{ data.overview.learningRate }}%</small
              >
            </dd>
          </div>
          <div>
            <dt>累计练习</dt>
            <dd>{{ data.overview.practices }} <small>次</small></dd>
          </div>
        </dl>
        <p class="role-overview-caption">
          累计活跃 {{ data.overview.activeDays }} 天<template v-if="data.overview.lastActivity">
            · 最近学习 <time :datetime="data.overview.lastActivity">{{ dateTime(data.overview.lastActivity) }}</time>
          </template>
        </p>
      </section>
      <HomeLeaderboard :rows="data.classLeaderboard" />
    </div>

    <section class="surface learning-kinds" aria-labelledby="learning-kinds-title">
      <header class="card-heading"><h2 id="learning-kinds-title">各类资源进度</h2></header>
      <div class="kind-grid">
        <article v-for="row in kindRows" :key="row.key" class="kind-card">
          <div class="kind-card-title">
            <h3>{{ row.label }}</h3>
            <strong v-if="row.stats.total">{{ row.stats.passRate }}%</strong>
          </div>
          <template v-if="row.stats.total">
            <el-progress
              :percentage="row.stats.passRate"
              :stroke-width="5"
              :show-text="false"
              :aria-label="row.label + '达标率'"
            />
            <p>已学习 {{ row.stats.learned }} / {{ row.stats.total }} · 达标 {{ row.stats.passed }}</p>
          </template>
          <p v-else>暂无该类资源</p>
        </article>
      </div>
    </section>
  </div>
</template>

<style scoped>
.learning-rate {
  display: block;
}

.learning-kinds {
  grid-column: 1 / -1;
  min-width: 0;
}

.kind-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 9px;
  padding: 0 20px 20px;
}

.kind-card {
  padding: 12px;
  border: 1px solid var(--surface-border);
  border-radius: 9px;
  background: #f8fafb;
}

.kind-card-title {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 4px 8px;
  margin-bottom: 6px;
}

.kind-card-title h3 {
  margin: 0;
  color: #234055;
  font-size: 14px;
}

.kind-card-title strong {
  color: var(--workspace-accent);
  font-size: 15px;
  font-variant-numeric: tabular-nums;
}

.kind-card p {
  margin: 6px 0 0;
  font-size: 12px;
}

@media (max-width: 700px) {
  .kind-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    padding-inline: 12px;
  }
}
</style>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { DataAnalysis, Refresh, School, TrendCharts } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import type { StudentHomeResponse } from '../analytics'
import { classroomStatusOptions, classroomStatusType, labelOf } from '../teaching'

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
const passTone = computed(() => (data.value?.overview.isPassing ? 'success' : 'warning'))

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
  emit('navigate', 'classroom-content', { classroomId: id })
}

function openStatistics(id: string) {
  emit('navigate', 'classroom-statistics', { classroomId: id, backKey: 'student-home' })
}

onMounted(load)
</script>

<template>
  <section class="page-heading">
    <div>
      <h1>{{ data ? `你好，${data.student.name}` : '学习首页' }}</h1>
    </div>
    <div class="heading-actions">
      <el-button :icon="DataAnalysis" @click="emit('navigate', 'skill-graph')">技能图谱</el-button>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新数据</el-button>
    </div>
  </section>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />

  <div v-loading="loading">
    <template v-if="data">
      <section class="home-hero">
        <div class="home-hero-copy">
          <h2>学习概况</h2>
          <p>
            {{ data.student.className || '未分班' }} · {{ data.student.professionName || '专业待完善' }} · 学号
            {{ data.student.number }}
          </p>
          <div class="today-strip">
            <div>
              <strong>{{ data.today.learned }}</strong><span>今日学习资源</span>
            </div>
            <div>
              <strong>{{ data.today.practices }}</strong><span>今日练习次数</span>
            </div>
            <div>
              <strong>{{ data.overview.activeDays }}</strong><span>累计活跃天数</span>
            </div>
            <div>
              <strong>{{ data.overview.practices }}</strong><span>累计练习次数</span>
            </div>
          </div>
        </div>
        <div class="score-orb" :class="passTone">
          <div class="score-ring" :style="{ '--rate': `${data.overview.passRate}%` }">
            <div class="score-ring-inner">
              <strong>{{ data.overview.passRate }}<small>%</small></strong>
              <span>资源达标率</span>
            </div>
          </div>
          <p>{{ data.overview.isPassing ? '已达到课堂目标' : '尚未达到课堂达标线' }}</p>
        </div>
      </section>

      <div class="home-metric-grid">
        <article class="home-metric">
          <span>已学习资源</span>
          <strong
            >{{ data.overview.learned }}<small> / {{ data.overview.totalResources }}</small></strong
          >
          <div class="metric-track"><i :style="{ width: `${data.overview.learningRate}%` }" /></div>
          <p>学习率 {{ data.overview.learningRate }}%</p>
        </article>
        <article class="home-metric">
          <span>已达标资源</span>
          <strong
            >{{ data.overview.passed }}<small> / {{ data.overview.totalResources }}</small></strong
          >
          <div class="metric-track success"><i :style="{ width: `${data.overview.passRate}%` }" /></div>
          <p>达标线为资源总数 60%</p>
        </article>
        <article class="home-metric">
          <span>累计活跃天数</span>
          <strong>{{ data.overview.activeDays }}<small> 天</small></strong>
          <p>最近学习 {{ data.overview.lastActivity ? dateTime(data.overview.lastActivity) : '暂无记录' }}</p>
        </article>
        <button class="home-metric action" @click="emit('navigate', 'my-classrooms')">
          <span>我的课堂</span>
          <strong>{{ data.classrooms.length }}<small> 个</small></strong>
          <p>
            <el-icon><School /></el-icon>查看全部课堂与学习统计
          </p>
        </button>
      </div>

      <div class="home-columns">
        <section class="surface">
          <div class="card-heading">
            <div>
              <h2>今日课堂</h2>
              <p>优先进入开放中的课堂继续学习</p>
            </div>
            <el-button text type="primary" @click="emit('navigate', 'my-classrooms')">全部课堂</el-button>
          </div>
          <div v-if="data.classrooms.length" class="home-classroom-list">
            <article v-for="item in data.classrooms.slice(0, 5)" :key="item.id" class="mini-classroom">
              <div class="mini-classroom-head">
                <div>
                  <h3>{{ item.name }}</h3>
                  <p>{{ item.courseName }} · {{ item.teacherName }}</p>
                </div>
                <el-tag :type="classroomStatusType(item.status)" size="small">
                  {{ labelOf(classroomStatusOptions, item.status) }}
                </el-tag>
              </div>
              <div class="mini-classroom-progress">
                <span>已达标 {{ item.passed }} / {{ item.totalResources }}</span>
                <strong>{{ item.passRate }}%</strong>
              </div>
              <el-progress :percentage="item.passRate" :stroke-width="7" :show-text="false" />
              <div class="mini-classroom-actions">
                <span>{{ item.studentCount }} 名同学</span>
                <div>
                  <el-button link type="primary" @click="openStatistics(item.id)">统计</el-button>
                  <el-button type="primary" plain size="small" @click="enterClassroom(item.id)">进入课堂</el-button>
                </div>
              </div>
            </article>
          </div>
          <el-empty v-else description="暂未加入课堂" :image-size="72" />
        </section>

        <section class="surface">
          <div class="card-heading">
            <div>
              <h2>班级学习榜</h2>
              <p>按已学习资源数与达标数排序</p>
            </div>
          </div>
          <div v-if="data.classLeaderboard.length" class="leaderboard">
            <div
              v-for="(row, index) in data.classLeaderboard"
              :key="row.userId"
              class="leaderboard-row"
              :class="{ me: row.isMe }"
            >
              <span class="rank" :class="`rank-${index + 1}`">{{ index + 1 }}</span>
              <div class="leaderboard-person">
                <strong>{{ row.name }}<em v-if="row.isMe">我</em></strong>
                <span>{{ row.number }} · {{ row.activeDays }} 天活跃</span>
              </div>
              <div class="leaderboard-value">
                <strong>{{ row.learned }}</strong><span>学习资源</span>
              </div>
              <div class="leaderboard-value">
                <strong>{{ row.passRate }}%</strong><span>达标率</span>
              </div>
            </div>
          </div>
          <el-empty v-else description="暂无排行榜数据" :image-size="72" />
        </section>
      </div>

      <section class="surface section-gap">
        <div class="card-heading">
          <div>
            <h2>四类资源达标情况</h2>
            <p>理论、练习、案例与实操的学习覆盖和达标率</p>
          </div>
          <el-button text type="primary" :icon="TrendCharts" @click="emit('navigate', 'skill-graph')"
            >查看技能图谱</el-button
          >
        </div>
        <div class="kind-grid">
          <article v-for="row in kindRows" :key="row.key" class="kind-card">
            <div class="kind-card-head">
              <span>{{ row.label }}</span>
              <strong>{{ row.stats.passRate }}%</strong>
            </div>
            <el-progress :percentage="row.stats.passRate" :stroke-width="8" :show-text="false" />
            <p>已学习 {{ row.stats.learned }} / {{ row.stats.total }}，达标 {{ row.stats.passed }}</p>
          </article>
        </div>
      </section>
    </template>
  </div>
</template>

<style scoped>
.home-hero {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 230px;
  gap: 22px;
  align-items: center;
  padding: 28px 30px;
  margin-bottom: 16px;
  color: #f5fbff;
  background: var(--workspace-hero);
  border-radius: 12px;
}

.home-hero-copy h2 {
  margin: 0 0 8px;
  color: inherit;
  font-size: 22px;
  line-height: 1.35;
}

.home-hero-copy > p {
  margin: 0;
  color: var(--workspace-on-hero);
  font-size: 13px;
}

.today-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  margin-top: 22px;
}

.today-strip > div {
  flex: 1 1 112px;
  min-width: 0;
  padding: 10px 14px;
  background: rgba(255, 255, 255, 0.1);
  border: 1px solid rgba(255, 255, 255, 0.13);
  border-radius: 9px;
}

.today-strip strong {
  display: block;
  font-size: 25px;
}

.today-strip span {
  color: var(--workspace-on-hero);
  font-size: 11px;
}

.score-orb {
  display: grid;
  justify-items: center;
  gap: 9px;
}

.score-ring {
  display: grid;
  place-items: center;
  width: 166px;
  height: 166px;
  border-radius: 50%;
  background: conic-gradient(#61d5b4 var(--rate), rgba(255, 255, 255, 0.14) 0);
}

.score-orb.warning .score-ring {
  background: conic-gradient(#f4c467 var(--rate), rgba(255, 255, 255, 0.14) 0);
}

.score-ring-inner {
  display: grid;
  place-items: center;
  align-content: center;
  width: 132px;
  height: 132px;
  background: #17404c;
  border-radius: 50%;
}

.score-ring-inner strong {
  font-size: 32px;
  line-height: 1;
}

.score-ring-inner strong small {
  font-size: 15px;
}

.score-ring-inner span {
  margin-top: 4px;
  color: var(--workspace-on-hero);
  font-size: 11px;
}

.score-orb p {
  margin: 0;
  color: var(--workspace-on-hero);
  font-size: 12px;
}

.home-metric-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 14px;
  margin-bottom: 16px;
}

.home-metric {
  display: grid;
  gap: 8px;
  padding: 18px;
  text-align: left;
  background: #fff;
  border: 1px solid #e5ecf4;
  border-radius: 12px;
  box-shadow: var(--soft-shadow);
}

.home-metric > span {
  color: #647a90;
  font-size: 12px;
}

.home-metric strong {
  color: #193a5a;
  font-size: 28px;
  line-height: 1;
}

.home-metric strong small {
  color: #8396a9;
  font-size: 13px;
  font-weight: 500;
}

.home-metric p {
  display: flex;
  align-items: center;
  gap: 4px;
  margin: 0;
  color: #7b8ea2;
  font-size: 11px;
}

.home-metric.action {
  cursor: pointer;
  border-color: #dfe9f6;
  background: linear-gradient(135deg, #fbfdff, #eef5fd);
}

.home-metric.action:hover {
  border-color: #bcd6f1;
}

.metric-track {
  height: 5px;
  overflow: hidden;
  background: #eef3f9;
  border-radius: 4px;
}

.metric-track i {
  display: block;
  height: 100%;
  background: #5a9be6;
  border-radius: inherit;
}

.metric-track.success i {
  background: #30a985;
}

.home-columns {
  display: grid;
  grid-template-columns: minmax(0, 1.3fr) minmax(0, 1fr);
  gap: 16px;
}

.home-classroom-list {
  display: grid;
  gap: 11px;
}

.mini-classroom {
  padding: 14px;
  background: #f9fbfe;
  border: 1px solid #e8eef6;
  border-radius: 10px;
}

.mini-classroom-head {
  display: flex;
  justify-content: space-between;
  gap: 10px;
}

.mini-classroom h3 {
  margin: 0 0 3px;
  color: #203e5c;
  font-size: 15px;
}

.mini-classroom p {
  margin: 0;
  color: #778a9d;
  font-size: 11px;
}

.mini-classroom-progress {
  display: flex;
  justify-content: space-between;
  margin: 12px 0 6px;
  color: #6d8297;
  font-size: 11px;
}

.mini-classroom-progress strong {
  color: #24527b;
}

.mini-classroom-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin-top: 10px;
}

.mini-classroom-actions > span {
  color: #8799ab;
  font-size: 11px;
}

.leaderboard {
  display: grid;
  gap: 4px;
}

.leaderboard-row {
  display: grid;
  grid-template-columns: 28px minmax(0, 1fr) 70px 70px;
  gap: 8px;
  align-items: center;
  padding: 9px 8px;
  border-radius: 8px;
}

.leaderboard-row.me {
  background: #eef6ff;
}

.rank {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  color: #73879b;
  font-size: 12px;
  background: #f0f4f9;
  border-radius: 7px;
}

.rank-1 {
  color: #8a6100;
  background: #fff2c9;
}

.rank-2 {
  color: #5f6f80;
  background: #e9eef4;
}

.rank-3 {
  color: #8a5b35;
  background: #f8e6d8;
}

.leaderboard-person {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.leaderboard-person strong {
  color: #294660;
  font-size: 13px;
}

.leaderboard-person strong em {
  margin-left: 6px;
  padding: 1px 5px;
  color: #2d6cae;
  font-size: 10px;
  font-style: normal;
  background: #e3f0fd;
  border-radius: 4px;
}

.leaderboard-person span {
  overflow: hidden;
  color: #8598aa;
  font-size: 10px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.leaderboard-value {
  display: grid;
  justify-items: end;
}

.leaderboard-value strong {
  color: #2c5276;
  font-size: 13px;
}

.leaderboard-value span {
  color: #8b9bad;
  font-size: 10px;
}

.kind-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 12px;
}

.kind-card {
  padding: 14px;
  background: #f9fbfe;
  border: 1px solid #e8eef6;
  border-radius: 9px;
}

.kind-card-head {
  display: flex;
  justify-content: space-between;
  margin-bottom: 9px;
  color: #526a82;
  font-size: 12px;
}

.kind-card-head strong {
  color: #1f4e78;
  font-size: 15px;
}

.kind-card p {
  margin: 8px 0 0;
  color: #8395a7;
  font-size: 10px;
}

@media (max-width: 1000px) {
  .home-metric-grid,
  .kind-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
  .home-columns {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 700px) {
  .home-hero {
    grid-template-columns: 1fr;
    padding: 22px;
  }
  .score-orb {
    justify-items: start;
  }
  .home-metric-grid {
    grid-template-columns: 1fr;
  }
  .leaderboard-row {
    grid-template-columns: 24px minmax(0, 1fr) repeat(2, minmax(36px, auto));
    column-gap: 8px;
  }
}
</style>

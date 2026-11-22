<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { DataAnalysis, Refresh, School, User } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import type { TeacherHomeResponse } from '../analytics'
import { classroomStatusOptions, classroomStatusType, labelOf } from '../teaching'

const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()
const loading = ref(false)
const error = ref('')
const data = ref<TeacherHomeResponse | null>(null)

const passRate = computed(() => {
  if (!data.value?.overview.managedStudents) return 0
  return Math.round((data.value.overview.passedStudents / data.value.overview.managedStudents) * 100)
})
const openClassrooms = computed(() => data.value?.classrooms.filter(item => item.status === 'open') || [])

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
  emit('navigate', 'classroom-content', { classroomId: id })
}

function openStatistics(id: string) {
  emit('navigate', 'classrooms', { statistics: id })
}

onMounted(load)
</script>

<template>
  <section class="page-heading">
    <div>
      <h1>{{ data ? `${data.teacher.displayName}，教学概览` : '教师首页' }}</h1>
    </div>
    <div class="heading-actions">
      <el-button :icon="DataAnalysis" @click="emit('navigate', 'skill-graph')">学生技能图谱</el-button>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新数据</el-button>
    </div>
  </section>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />

  <div v-loading="loading">
    <template v-if="data">
      <section class="teacher-hero">
        <div class="teacher-hero-copy">
          <h2>课堂概况</h2>
          <p>{{ openClassrooms.length }} 个课堂正在进行</p>
          <div class="hero-metrics">
            <div>
              <strong>{{ data.today.activeStudents }}</strong><span>今日活跃学生</span>
            </div>
            <div>
              <strong>{{ data.today.passedStudents }}</strong><span>今日达标学生</span>
            </div>
            <div>
              <strong>{{ data.overview.passedStudents }}</strong><span>总体达标学生</span>
            </div>
          </div>
        </div>
        <div class="hero-progress">
          <el-progress type="dashboard" :percentage="passRate" :width="152" :stroke-width="12" color="#78dac4">
            <template #default="{ percentage }">
              <strong>{{ percentage }}%</strong>
              <span>学生达标率</span>
            </template>
          </el-progress>
        </div>
      </section>

      <div class="teacher-metrics">
        <article>
          <el-icon><User /></el-icon>
          <span>管理学生</span>
          <strong>{{ data.overview.managedStudents }}</strong>
          <p>所属班级 {{ data.overview.managedClasses }} 个</p>
        </article>
        <article>
          <el-icon><School /></el-icon>
          <span>我的课堂</span>
          <strong>{{ data.overview.managedClassrooms }}</strong>
          <p>进行中 {{ openClassrooms.length }} 个</p>
        </article>
        <article>
          <el-icon><DataAnalysis /></el-icon>
          <span>资源学习率</span>
          <strong>{{ data.overview.learningRate }}%</strong>
          <p>资源达标率 {{ data.overview.resourcePassRate }}%</p>
        </article>
        <button @click="emit('navigate', 'skill-graph')">
          <el-icon><DataAnalysis /></el-icon>
          <span>学生技能</span>
          <strong>查看图谱</strong>
          <p>按学生检索课程技能掌握情况</p>
        </button>
      </div>

      <div class="teacher-columns">
        <section class="surface">
          <div class="card-heading">
            <div>
              <h2>我管理的课堂</h2>
              <p>快速进入课堂内容或查看学生统计</p>
            </div>
            <el-button text type="primary" @click="emit('navigate', 'classrooms')">课堂管理</el-button>
          </div>
          <div v-if="data.classrooms.length" class="classroom-rows">
            <article v-for="item in data.classrooms.slice(0, 6)" :key="item.id" class="classroom-row">
              <div class="classroom-main">
                <div>
                  <h3>{{ item.name }}</h3>
                  <p>{{ item.courseName }} · {{ item.studentCount }} 名学生</p>
                </div>
                <el-tag :type="classroomStatusType(item.status)" size="small">
                  {{ labelOf(classroomStatusOptions, item.status) }}
                </el-tag>
              </div>
              <div class="classroom-metrics">
                <div>
                  <span>班级达标率</span><strong>{{ item.classPassRate }}%</strong>
                </div>
                <div>
                  <span>结课时间</span><strong class="date">{{ dateTime(item.closeAt) }}</strong>
                </div>
              </div>
              <el-progress :percentage="item.classPassRate" :stroke-width="6" :show-text="false" />
              <div class="classroom-actions">
                <el-button link type="primary" @click="openStatistics(item.id)">查看统计</el-button>
                <el-button size="small" type="primary" plain @click="enterClassroom(item.id)">进入课堂</el-button>
              </div>
            </article>
          </div>
          <el-empty v-else description="暂无课堂" :image-size="72">
            <el-button type="primary" @click="emit('navigate', 'classrooms')">创建课堂</el-button>
          </el-empty>
        </section>

        <section class="surface">
          <div class="card-heading">
            <div>
              <h2>学生学习排行</h2>
              <p>按学习资源数与资源达标率排序</p>
            </div>
          </div>
          <div v-if="data.leaderboard.length" class="teacher-leaderboard">
            <div v-for="(row, index) in data.leaderboard" :key="row.userId" class="leader-row">
              <span class="leader-rank" :class="`rank-${index + 1}`">{{ index + 1 }}</span>
              <div class="leader-person">
                <strong>{{ row.name }}</strong>
                <span>{{ row.className || '未分班' }} · {{ row.number }}</span>
              </div>
              <div class="leader-stat">
                <strong>{{ row.learned }}</strong><span>学习</span>
              </div>
              <div class="leader-stat">
                <strong>{{ row.passRate }}%</strong><span>达标</span>
              </div>
            </div>
          </div>
          <el-empty v-else description="暂无学生学习数据" :image-size="72" />
        </section>
      </div>
    </template>
  </div>
</template>

<style scoped>
.teacher-hero {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 210px;
  gap: 20px;
  align-items: center;
  padding: 26px 30px;
  margin-bottom: 16px;
  color: #f5fbff;
  background: var(--workspace-hero);
  border-radius: 12px;
}

.teacher-hero-copy h2 {
  margin: 0 0 7px;
  color: inherit;
  font-size: 22px;
}

.teacher-hero-copy > p {
  margin: 0;
  color: var(--workspace-on-hero);
  font-size: 13px;
}

.hero-metrics {
  display: flex;
  flex-wrap: wrap;
  gap: 9px;
  margin-top: 20px;
}

.hero-metrics > div {
  flex: 1 1 115px;
  min-width: 0;
  padding: 10px 14px;
  background: rgba(255, 255, 255, 0.09);
  border: 1px solid rgba(255, 255, 255, 0.12);
  border-radius: 9px;
}

.hero-metrics strong {
  display: block;
  font-size: 24px;
}

.hero-metrics span {
  color: var(--workspace-on-hero);
  font-size: 11px;
}

.hero-progress {
  display: grid;
  place-items: center;
}

.hero-progress strong {
  display: block;
  color: #f2f9ff;
  font-size: 28px;
}

.hero-progress span {
  color: var(--workspace-on-hero);
  font-size: 11px;
}

.teacher-metrics {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 14px;
  margin-bottom: 16px;
}

.teacher-metrics article,
.teacher-metrics button {
  display: grid;
  grid-template-columns: 30px minmax(0, 1fr);
  gap: 2px 8px;
  align-items: center;
  padding: 17px;
  text-align: left;
  background: #fff;
  border: 1px solid #e5ecf4;
  border-radius: 12px;
  box-shadow: var(--soft-shadow);
}

.teacher-metrics .el-icon {
  grid-row: span 3;
  color: #5a95cf;
  font-size: 24px;
}

.teacher-metrics span {
  color: #6a8096;
  font-size: 11px;
}

.teacher-metrics strong {
  color: #1a3c5b;
  font-size: 22px;
  line-height: 1.15;
}

.teacher-metrics p {
  grid-column: 2;
  margin: 0;
  color: #8a9bad;
  font-size: 10px;
}

.teacher-metrics button {
  cursor: pointer;
  background: linear-gradient(135deg, #fbfdff, #eff6fe);
  border-color: #dce9f7;
}

.teacher-columns {
  display: grid;
  grid-template-columns: minmax(0, 1.45fr) minmax(0, 1fr);
  gap: 16px;
}

.classroom-rows {
  display: grid;
  gap: 11px;
}

.classroom-row {
  padding: 14px;
  background: #f9fbfe;
  border: 1px solid #e8eef6;
  border-radius: 10px;
}

.classroom-main {
  display: flex;
  justify-content: space-between;
  gap: 12px;
}

.classroom-main h3 {
  margin: 0 0 3px;
  color: #203f5c;
  font-size: 15px;
}

.classroom-main p {
  margin: 0;
  color: #7b8da0;
  font-size: 11px;
}

.classroom-metrics {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  margin: 11px 0 7px;
}

.classroom-metrics > div {
  display: flex;
  align-items: baseline;
  gap: 8px;
}

.classroom-metrics span {
  color: #7d90a3;
  font-size: 10px;
}

.classroom-metrics strong {
  color: #25517a;
  font-size: 13px;
}

.classroom-metrics .date {
  color: #687f95;
  font-size: 10px;
}

.classroom-actions {
  display: flex;
  justify-content: flex-end;
  gap: 4px;
  margin-top: 8px;
}

.teacher-leaderboard {
  display: grid;
  gap: 4px;
}

.leader-row {
  display: grid;
  grid-template-columns: 27px minmax(0, 1fr) 58px 58px;
  gap: 8px;
  align-items: center;
  padding: 9px 7px;
  border-radius: 8px;
}

.leader-row:nth-child(odd) {
  background: #f8fafd;
}

.leader-rank {
  display: grid;
  place-items: center;
  width: 23px;
  height: 23px;
  color: #72859a;
  font-size: 11px;
  background: #edf2f7;
  border-radius: 7px;
}

.rank-1 {
  color: #8a6100;
  background: #fff2c9;
}

.rank-2 {
  color: #5f6f80;
  background: #e8eef5;
}

.rank-3 {
  color: #8a5b35;
  background: #f8e5d7;
}

.leader-person {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.leader-person strong {
  color: #29465f;
  font-size: 13px;
}

.leader-person span {
  overflow: hidden;
  color: #899bae;
  font-size: 10px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.leader-stat {
  display: grid;
  justify-items: end;
}

.leader-stat strong {
  color: #2b5277;
  font-size: 12px;
}

.leader-stat span {
  color: #90a0b1;
  font-size: 9px;
}

@media (max-width: 1000px) {
  .teacher-metrics {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
  .teacher-columns {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 700px) {
  .teacher-hero {
    grid-template-columns: 1fr;
    padding: 22px;
  }
  .hero-progress {
    justify-items: start;
  }
  .teacher-metrics {
    grid-template-columns: 1fr;
  }
}
</style>

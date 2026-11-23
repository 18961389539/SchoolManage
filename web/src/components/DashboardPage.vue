<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  ArrowRight,
  Connection,
  Cpu,
  DataAnalysis,
  FolderOpened,
  Monitor,
  Plus,
  Refresh,
  School,
  User,
  UserFilled
} from '@element-plus/icons-vue'
import { api, post, messageOf, dateTime } from '../api'
import { appState } from '../store'
import type { AdminHomeResponse } from '../analytics'
import type { BootstrapResult, DashboardResponse } from '../types'
import TableEmpty from './TableEmpty.vue'
const emit = defineEmits<{ navigate: [key: string] }>()
const loading = ref(false),
  creating = ref(false),
  error = ref(''),
  updatedAt = ref(''),
  admin = ref<AdminHomeResponse | null>(null),
  data = ref<DashboardResponse | null>(null)
const stats = [
  { key: 'students', label: '学生档案', icon: User, route: 'students', tone: 'blue' },
  { key: 'teachers', label: '教师档案', icon: UserFilled, route: 'teachers', tone: 'teal' },
  { key: 'classes', label: '教学班级', icon: School, route: 'classes', tone: 'indigo' },
  { key: 'devices', label: '实训设备', icon: Monitor, route: 'devices', tone: 'amber' }
] as const
const quickActions = [
  { label: '学生档案', description: '新增账号与批量导入', icon: User, route: 'students' },
  { label: '设备终端', description: '查看设备与连接状态', icon: Monitor, route: 'devices' },
  { label: '备份与恢复', description: '备份和恢复平台数据', icon: FolderOpened, route: 'backups' },
  { label: '服务器信息', description: '查看主机运行状态', icon: Cpu, route: 'server' }
]
const resourceStats = computed(() => [
  { label: '专业目录', value: data.value?.counts.professions, route: 'professions' },
  { label: '机柜资源', value: data.value?.counts.cabinets, route: 'cabinets' },
  { label: '实训节点', value: data.value?.counts.nodes, route: 'nodes' }
])
const todayMetrics = computed(() => {
  if (!admin.value) return []
  const { today } = admin.value
  return [
    { label: '今日活跃班级', value: today.activeClasses, unit: '个' },
    { label: '今日活跃学生', value: today.activeStudents, unit: '人' },
    { label: '今日达标学生', value: today.passedStudents, unit: '人' }
  ]
})
const overviewMetrics = computed(() => {
  if (!admin.value) return []
  const { overview } = admin.value
  return [
    { label: '总体达标学生', value: overview.passedStudents, unit: `/ ${overview.managedStudents} 人` },
    { label: '管理班级', value: overview.managedClasses, unit: '个' },
    { label: '开放课堂', value: overview.openClassrooms, unit: `/ ${overview.totalClassrooms} 个` },
    { label: '资源达标率', value: overview.resourcePassRate, unit: '%' }
  ]
})

const classCount = computed(() => data.value?.counts.classes ?? admin.value?.overview.managedClasses ?? 0)
const teacherCount = computed(() => data.value?.counts.teachers ?? 0)

const classRankingEmptyTitle = computed(() => (classCount.value ? '暂无班级学习数据' : '还没有教学班级'))
const classRankingEmptyHint = computed(() =>
  classCount.value
    ? '学生加入课堂并开始学习后，将按达标率排序。'
    : '先在班级管理中创建班级并分配学生，学习数据会出现在这里。'
)
const teacherActivityEmptyTitle = computed(() => (teacherCount.value ? '近 30 天暂无教师活跃记录' : '还没有教师档案'))
const teacherActivityEmptyHint = computed(() =>
  teacherCount.value
    ? '教师管理课堂或进行系统操作后，会显示在这里。'
    : '先在教师管理中创建教师档案，活跃情况会出现在这里。'
)
const isFresh = computed(() => {
  const counts = data.value?.counts
  return !!counts && !counts.students && !counts.teachers && !counts.classes && !counts.devices && !counts.professions
})

async function load() {
  loading.value = true
  error.value = ''
  try {
    const [dashboard, home] = await Promise.all([
      api<DashboardResponse>('/dashboard'),
      api<AdminHomeResponse>('/home/admin')
    ])
    data.value = dashboard
    admin.value = home
    updatedAt.value = new Date().toISOString()
    if (data.value.schoolName) appState.schoolName = data.value.schoolName
  } catch (err) {
    error.value = messageOf(err)
  } finally {
    loading.value = false
  }
}
async function createSimulation() {
  try {
    await ElMessageBox.confirm(
      '将创建带有“模拟”标识的机柜、设备、节点和虚拟机。它们可以用于验证终端连接与资源操作，不会创建真实虚拟机。重复操作不会重复创建默认资源。',
      '创建内置模拟环境',
      { confirmButtonText: '创建模拟环境', cancelButtonText: '取消', type: 'info' }
    )
    creating.value = true
    const result = await post<BootstrapResult>('/simulation/bootstrap')
    ElMessage.success(result.message || '模拟环境已就绪')
    await load()
  } catch (err) {
    if (err !== 'cancel' && err !== 'close') ElMessage.error(messageOf(err))
  } finally {
    creating.value = false
  }
}
onMounted(load)
</script>
<template>
  <section class="page-heading dashboard-heading">
    <div>
      <h1>控制台</h1>
    </div>
    <el-button :icon="Refresh" :loading="loading" @click="load">刷新数据</el-button>
  </section>
  <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon class="section-notice" />
  <el-skeleton v-if="loading && !data" :rows="4" animated class="dashboard-skeleton surface" />

  <div v-if="data" class="stat-grid" role="group" aria-label="组织与设备">
    <button v-for="stat in stats" :key="stat.key" type="button" class="stat-card" @click="emit('navigate', stat.route)">
      <span class="stat-icon" :class="stat.tone" aria-hidden="true">
        <el-icon :size="20"><component :is="stat.icon" /></el-icon>
      </span>
      <span class="stat-copy">
        <span>{{ stat.label }}</span>
        <strong>{{ data.counts[stat.key] }}</strong>
      </span>
      <el-icon class="stat-arrow" aria-hidden="true"><ArrowRight /></el-icon>
    </button>
  </div>

  <div class="dashboard-columns dashboard-primary" :class="{ 'has-overview': admin }">
    <section v-if="admin" class="overview-panel surface" aria-labelledby="overview-heading">
      <div class="overview-today">
        <div class="overview-header">
          <div class="overview-title">
            <span class="overview-icon" aria-hidden="true"
              ><el-icon><DataAnalysis /></el-icon
            ></span>
            <h2 id="overview-heading">教学运行概况</h2>
          </div>
          <time v-if="updatedAt" :datetime="updatedAt">更新于 {{ dateTime(updatedAt) }}</time>
        </div>
        <dl class="overview-metrics">
          <div v-for="metric in todayMetrics" :key="metric.label">
            <dt>{{ metric.label }}</dt>
            <dd>
              {{ metric.value }} <small>{{ metric.unit }}</small>
            </dd>
          </div>
        </dl>
      </div>
      <dl class="overview-totals">
        <div v-for="metric in overviewMetrics" :key="metric.label">
          <dt>{{ metric.label }}</dt>
          <dd>
            {{ metric.value }} <small>{{ metric.unit }}</small>
          </dd>
        </div>
      </dl>
    </section>

    <section class="surface dashboard-shortcuts" aria-labelledby="quick-actions-heading">
      <div class="card-heading">
        <h2 id="quick-actions-heading">常用操作</h2>
      </div>
      <div class="quick-actions">
        <button
          v-for="action in quickActions"
          :key="action.route"
          type="button"
          @click="emit('navigate', action.route)"
        >
          <span class="quick-icon" aria-hidden="true"><component :is="action.icon" /></span>
          <span class="quick-action-copy">
            <strong>{{ action.label }}</strong><small>{{ action.description }}</small>
          </span>
          <el-icon class="quick-action-arrow" aria-hidden="true"><ArrowRight /></el-icon>
        </button>
      </div>
    </section>
  </div>

  <section v-if="isFresh" class="start-guide surface" aria-labelledby="start-guide-heading">
    <div class="start-guide-copy">
      <h2 id="start-guide-heading">首次使用</h2>
      <p>从导入学生、准备实训资源和组课开始。</p>
    </div>
    <ol class="start-steps">
      <li>
        <div><strong>导入学生与班级</strong><span>CSV 批量导入，或逐个新增账号。</span></div>
        <el-button link type="primary" @click="emit('navigate', 'students')">去导入</el-button>
      </li>
      <li>
        <div><strong>创建模拟环境</strong><span>生成机柜、设备、节点与虚拟机。</span></div>
        <el-button link type="primary" :loading="creating" @click="createSimulation">创建</el-button>
      </li>
      <li>
        <div><strong>发布课程并开课</strong><span>课程发布后即可用于课堂。</span></div>
        <el-button link type="primary" @click="emit('navigate', 'courses')">去组课</el-button>
      </li>
    </ol>
  </section>

  <div class="dashboard-columns dashboard-resources">
    <section class="surface simulation-card">
      <div class="card-heading">
        <div>
          <h2>内置模拟环境</h2>
        </div>
        <el-icon :size="27" class="simulation-icon"><Connection /></el-icon>
      </div>
      <p>使用模拟机柜、设备和虚拟机验证终端连接、资源回收与启停流程。</p>
      <div class="simulation-actions">
        <el-button type="primary" :icon="Plus" :loading="creating" @click="createSimulation">创建模拟环境</el-button>
        <el-button text @click="emit('navigate', 'devices')">查看已有设备</el-button>
      </div>
    </section>
    <section class="surface dashboard-inventory" aria-labelledby="inventory-heading">
      <div class="card-heading"><h2 id="inventory-heading">资源统计</h2></div>
      <div class="resource-summary">
        <button v-for="item in resourceStats" :key="item.route" type="button" @click="emit('navigate', item.route)">
          <span>{{ item.label }}</span><strong>{{ item.value ?? '—' }}</strong>
        </button>
      </div>
    </section>
  </div>
  <div v-if="admin" class="dashboard-columns admin-panels">
    <section class="surface">
      <div class="card-heading">
        <div>
          <h2>班级达标排行</h2>
          <p>按班级学生资源达标率排序</p>
        </div>
      </div>
      <div v-if="admin.classRanking.length" class="ranking-list">
        <div v-for="(item, index) in admin.classRanking" :key="item.classId" class="ranking-row">
          <span class="rank-index" :class="{ top: index < 3 }">{{ index + 1 }}</span>
          <div class="rank-name">
            <strong>{{ item.className }}</strong>
            <small>{{ item.professionName || '未设置专业' }} · {{ item.studentCount }} 人</small>
          </div>
          <el-progress
            class="rank-progress"
            :percentage="item.passRate"
            :stroke-width="6"
            :show-text="false"
            :color="item.passRate >= 60 ? '#2f9d7a' : '#d6a347'"
          />
          <span class="rank-value">{{ item.passRate }}%</span>
        </div>
      </div>
      <el-empty v-else :description="classRankingEmptyTitle" :image-size="70">
        <p class="empty-hint">{{ classRankingEmptyHint }}</p>
        <el-button type="primary" plain @click="emit('navigate', classCount ? 'classrooms' : 'classes')">
          {{ classCount ? '去课堂中心' : '去班级管理' }}
        </el-button>
      </el-empty>
    </section>
    <section class="surface">
      <div class="card-heading">
        <div>
          <h2>教师活跃</h2>
          <p>近 30 天平台操作与课堂管理</p>
        </div>
      </div>
      <ul v-if="admin.teacherActivity.length" class="teacher-activity">
        <li v-for="row in admin.teacherActivity" :key="row.userName">
          <div>
            <strong>{{ row.displayName }}</strong>
            <small>{{ row.userName }} · {{ row.role === 'Admin' ? '管理员' : '教师' }}</small>
          </div>
          <div class="teacher-activity-metrics">
            <span>{{ row.managedClasses }} 个班级 · {{ row.auditCount }} 次操作</span>
            <small>{{ row.lastActiveAt ? dateTime(row.lastActiveAt) : '暂无记录' }}</small>
          </div>
        </li>
      </ul>
      <el-empty v-else :description="teacherActivityEmptyTitle" :image-size="70">
        <p class="empty-hint">{{ teacherActivityEmptyHint }}</p>
        <el-button type="primary" plain @click="emit('navigate', 'teachers')">去教师管理</el-button>
      </el-empty>
    </section>
  </div>
  <section v-if="data" class="surface section-gap">
    <div class="card-heading">
      <div>
        <h2>最近操作</h2>
        <p>平台关键操作的最新记录</p>
      </div>
      <el-button text type="primary" @click="emit('navigate', 'audits')">全部日志</el-button>
    </div>
    <el-table :data="data.recentAudits || []" class="recent-table" empty-text="">
      <template #empty>
        <TableEmpty title="暂无操作记录" hint="账号、资源和系统配置的关键操作发生后，会显示在这里。">
          <template #action>
            <el-button type="primary" plain @click="emit('navigate', 'audits')">查看全部日志</el-button>
          </template>
        </TableEmpty>
      </template>
      <el-table-column label="时间（北京时间）" width="220"
        ><template #default="{ row }">{{ dateTime(row.at) }}</template></el-table-column
      ><el-table-column prop="userName" label="账号" min-width="130" /><el-table-column
        prop="action"
        label="操作"
        min-width="180" /><el-table-column prop="target" label="目标" min-width="220" show-overflow-tooltip
    /></el-table>
    <p class="scroll-hint">左右滑动表格可查看完整列</p>
  </section>
</template>

<style scoped>
.dashboard-heading h1 {
  margin: 0;
  font-size: clamp(26px, 3vw, 34px);
}

.stat-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 16px;
  margin-bottom: 20px;
}

.stat-card {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  align-items: center;
  gap: 13px;
  min-height: 94px;
  padding: 15px 16px;
  border: 1px solid var(--surface-border);
  border-radius: 12px;
  background: #fff;
  box-shadow: var(--surface-shadow);
  text-align: left;
  color: #1b3548;
  transition:
    border-color 0.16s ease,
    background 0.16s ease,
    box-shadow 0.16s ease,
    transform 0.16s ease;
}

.stat-card:hover,
.quick-actions > button:hover,
.resource-summary > button:hover {
  border-color: #a9cec4;
  background: #f4faf8;
  box-shadow: 0 8px 20px #17324612;
  transform: translateY(-1px);
}

.stat-icon,
.quick-icon,
.overview-icon {
  display: grid;
  place-items: center;
  flex: none;
  border-radius: 10px;
}

.stat-icon {
  width: 43px;
  height: 43px;
}

.stat-icon.blue {
  background: #e9f1ff;
  color: #3264a0;
}

.stat-icon.teal {
  background: #e2f3ed;
  color: #1d7564;
}

.stat-icon.indigo {
  background: #f0ecfc;
  color: #6755a1;
}

.stat-icon.amber {
  background: #fbf0df;
  color: #945f23;
}

.stat-copy {
  display: grid;
  gap: 4px;
  min-width: 0;
}

.stat-copy span,
.resource-summary span {
  color: var(--text-muted);
  font-size: 13px;
}

.stat-copy strong {
  font-size: 27px;
  line-height: 1.1;
  font-weight: 650;
  font-variant-numeric: tabular-nums;
  overflow-wrap: anywhere;
}

.stat-arrow,
.quick-action-arrow {
  color: var(--workspace-accent);
  font-size: 14px;
}

.dashboard-columns {
  display: grid;
  gap: 20px;
}

.dashboard-primary.has-overview {
  grid-template-columns: minmax(0, 1.9fr) minmax(0, 1fr);
}

.dashboard-resources,
.admin-panels {
  grid-template-columns: repeat(2, minmax(0, 1fr));
  margin-top: 20px;
}

.dashboard-shortcuts .card-heading,
.dashboard-inventory .card-heading {
  padding-bottom: 12px;
}

.quick-actions {
  display: grid;
  gap: 5px;
  padding: 0 15px 14px;
}

.quick-actions > button {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  align-items: center;
  gap: 11px;
  padding: 6px 9px;
  border: 1px solid transparent;
  border-radius: 9px;
  background: #fff;
  text-align: left;
  transition:
    border-color 0.16s ease,
    background 0.16s ease,
    box-shadow 0.16s ease,
    transform 0.16s ease;
}

.quick-icon {
  width: 36px;
  height: 36px;
  background: #eaf4f1;
  color: var(--workspace-accent);
}

.quick-icon svg {
  width: 18px;
  height: 18px;
}

.quick-action-copy {
  display: grid;
  gap: 3px;
}

.quick-action-copy strong {
  color: #264155;
  font-size: 14px;
}

.quick-action-copy small {
  color: var(--text-muted);
  font-size: 12px;
}

.resource-summary {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 9px;
  padding: 0 24px 22px;
}

.resource-summary > button {
  display: grid;
  justify-items: start;
  align-content: start;
  gap: 7px;
  padding: 12px;
  border: 1px solid var(--surface-border);
  border-radius: 9px;
  background: #f6faf8;
  text-align: left;
  transition:
    border-color 0.16s ease,
    background 0.16s ease,
    box-shadow 0.16s ease,
    transform 0.16s ease;
}

.resource-summary strong {
  font-size: 23px;
  color: #234256;
  font-variant-numeric: tabular-nums;
  overflow-wrap: anywhere;
}

.simulation-card {
  padding-bottom: 22px;
  background: linear-gradient(135deg, #fff 50%, #eaf5f0);
}

.simulation-card .card-heading {
  padding-bottom: 12px;
}

.simulation-card p {
  margin: 0 24px 15px;
  font-size: 14px;
}

.simulation-icon {
  color: var(--workspace-accent);
}

.simulation-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 10px;
  margin: 0 24px;
}

.simulation-actions .el-button + .el-button {
  margin-left: 0;
}

.scroll-hint {
  display: none;
}

.empty-hint {
  max-width: 36ch;
  margin: 0 auto 12px;
  color: var(--el-text-color-regular);
  font-size: 12px;
  line-height: 1.5;
  text-align: center;
}
.start-guide {
  display: grid;
  grid-template-columns: minmax(0, 0.85fr) minmax(0, 2fr);
  align-items: center;
  gap: 10px 28px;
  padding: 20px 24px;
  margin-top: 20px;
}

.start-guide h2 {
  margin: 0 0 6px;
  font-size: 17px;
}

.start-guide p {
  margin: 0;
  font-size: 13px;
  line-height: 1.6;
}

.start-steps {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.start-steps li {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 7px;
  padding: 12px 14px;
  border: 1px solid #dce7eb;
  border-radius: 9px;
  background: #f6faf8;
}

.start-steps li > div {
  display: grid;
  gap: 3px;
}

.start-steps strong {
  color: #2b4257;
  font-size: 13px;
}

.start-steps span {
  color: #64788a;
  font-size: 12px;
  line-height: 1.55;
}

.start-steps .el-button {
  min-height: 0;
  padding: 0;
}

@media (max-width: 1000px) {
  .start-guide {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 640px) {
  .start-steps {
    grid-template-columns: 1fr;
  }

  .start-steps li {
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    padding: 9px 12px;
  }

  .start-steps li > div {
    display: block;
  }

  .start-steps span {
    display: none;
  }
}

.dashboard-skeleton {
  margin-bottom: 20px;
  padding: 26px;
}

.overview-panel {
  display: flex;
  flex-direction: column;
  border-color: #17454a;
  color: #fff;
  background: var(--workspace-hero);
}

.overview-today {
  flex: 1;
  padding: 22px 24px;
}

.overview-header {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 6px 14px;
  margin-bottom: 18px;
}

.overview-title {
  display: flex;
  align-items: center;
  gap: 10px;
}

.overview-icon {
  width: 35px;
  height: 35px;
  background: #a2e7d92a;
  color: #b8ebdf;
  font-size: 19px;
}

.overview-header h2 {
  margin: 0;
  color: #f5fbff;
  font-size: 20px;
}

.overview-header time,
.overview-metrics dt,
.overview-totals dt {
  color: var(--workspace-on-hero);
  font-size: 12px;
}

.overview-metrics,
.overview-totals {
  display: grid;
  gap: 12px;
  margin: 0;
}

.overview-metrics {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.overview-metrics > div {
  padding: 13px 14px;
  border: 1px solid #d6f3f333;
  border-radius: 9px;
  background: #dcfbfa13;
}

.overview-metrics dd,
.overview-totals dd {
  margin: 6px 0 0;
  font-weight: 650;
  font-variant-numeric: tabular-nums;
  overflow-wrap: anywhere;
}

.overview-metrics dd {
  font-size: clamp(22px, 3vw, 32px);
  line-height: 1.2;
}

.overview-metrics small,
.overview-totals small {
  font-size: 12px;
  font-weight: 500;
}

.overview-metrics small,
.overview-totals small {
  color: var(--workspace-on-hero);
}

.overview-totals {
  grid-template-columns: repeat(4, minmax(0, 1fr));
  padding: 16px 24px 18px;
  border-top: 1px solid #d6f3f333;
  background: #0a303568;
}

.overview-totals dd {
  color: #f5fbff;
  font-size: 20px;
}

.overview-totals > div {
  min-width: 0;
}

.ranking-list {
  display: grid;
  gap: 5px;
  padding: 0 24px 20px;
}

.ranking-row {
  display: grid;
  grid-template-columns: auto minmax(0, 1.1fr) minmax(38px, 1fr) 3em;
  align-items: center;
  gap: 9px;
  padding: 9px 0;
}

.ranking-row + .ranking-row,
.teacher-activity li + li {
  border-top: 1px solid #e4eeea;
}

.rank-index {
  display: grid;
  width: 24px;
  height: 24px;
  color: var(--text-muted);
  background: #eef6f4;
  border-radius: 6px;
  place-items: center;
  font-size: 12px;
  font-weight: 600;
}

.rank-index.top {
  color: var(--workspace-accent);
  background: #e1f0ed;
}

.rank-name strong,
.teacher-activity strong {
  display: block;
  overflow: hidden;
  color: #233b50;
  font-size: 13px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.rank-name small,
.teacher-activity small {
  display: block;
  margin-top: 4px;
  color: var(--text-muted);
  font-size: 11px;
  overflow-wrap: anywhere;
}

.rank-progress {
  min-width: 0;
}

.rank-value {
  color: #32546b;
  font-size: 12px;
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.teacher-activity {
  max-height: 320px;
  overflow: auto;
  list-style: none;
  margin: 0;
  padding: 0 24px 18px;
}

.teacher-activity li {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  align-items: start;
  gap: 12px;
  padding: 11px 0;
}

.teacher-activity-metrics {
  text-align: right;
  color: #32546b;
  font-size: 12px;
}

@media (max-width: 1050px) {
  .dashboard-primary.has-overview {
    grid-template-columns: minmax(0, 1fr);
  }

  .quick-actions {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 950px) {
  .stat-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 640px) {
  .scroll-hint {
    display: block;
    margin: 0;
    padding: 0 16px 14px;
    color: var(--text-muted);
    font-size: 12px;
  }

  .dashboard-heading {
    display: flex;
    align-items: flex-start;
    gap: 10px;
  }

  .dashboard-heading > .el-button {
    flex: none;
    margin-top: 3px;
  }

  .dashboard-resources,
  .admin-panels,
  .quick-actions {
    grid-template-columns: minmax(0, 1fr);
  }

  .stat-grid {
    gap: 10px;
  }

  .stat-card {
    grid-template-columns: auto minmax(0, 1fr);
    gap: 9px;
    min-height: 76px;
    padding: 10px;
  }

  .stat-icon {
    width: 35px;
    height: 35px;
  }

  .stat-copy strong {
    font-size: 23px;
  }

  .stat-arrow {
    display: none;
  }

  .overview-today {
    padding: 16px;
  }

  .overview-header {
    margin-bottom: 12px;
  }

  .overview-metrics,
  .overview-totals {
    gap: 8px;
  }

  .overview-metrics > div {
    padding: 10px 7px;
  }

  .overview-totals {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    padding: 12px 16px;
  }

  .ranking-list,
  .teacher-activity,
  .resource-summary {
    padding-inline: 16px;
  }

  .simulation-card p,
  .simulation-actions {
    margin-inline: 19px;
  }
}

@media (prefers-reduced-motion: reduce) {
  .stat-card,
  .quick-actions > button,
  .resource-summary > button {
    transition: none;
    transform: none;
  }
}
</style>

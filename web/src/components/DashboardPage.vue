<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Refresh, Plus, User, School, Monitor, Connection, Document, ArrowRight, Cpu } from '@element-plus/icons-vue'
import { api, post, messageOf, dateTime } from '../api'
import type { AdminHomeResponse } from '../analytics'
import type { BootstrapResult, DashboardResponse } from '../types'
const emit = defineEmits<{ navigate: [key: string] }>()
const loading = ref(false),
  creating = ref(false),
  error = ref(''),
  updatedAt = ref(''),
  admin = ref<AdminHomeResponse | null>(null),
  data = ref<DashboardResponse | null>(null)
const stats = [
  { key: 'students', label: '学生档案', icon: User, route: 'students', tone: 'blue' },
  { key: 'teachers', label: '教师档案', icon: User, route: 'teachers', tone: 'teal' },
  { key: 'classes', label: '教学班级', icon: School, route: 'classes', tone: 'indigo' },
  { key: 'devices', label: '实训设备', icon: Monitor, route: 'devices', tone: 'amber' }
] as const
const resourceStats = computed(() => [
  { label: '专业目录', value: data.value?.counts.professions, route: 'professions' },
  { label: '机柜资源', value: data.value?.counts.cabinets, route: 'cabinets' },
  { label: '集群节点', value: data.value?.counts.nodes, route: 'nodes' }
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
    if (data.value.schoolName)
      window.dispatchEvent(new CustomEvent('school-name-changed', { detail: data.value.schoolName }))
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
      <p v-if="data?.schoolName">{{ data.schoolName }}</p>
    </div>
    <el-button :icon="Refresh" :loading="loading" @click="load">刷新数据</el-button>
  </section>
  <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon class="section-notice" />
  <el-skeleton v-if="loading && !data" :rows="4" animated class="dashboard-skeleton surface" />
  <section v-if="admin" class="overview-panel surface" aria-labelledby="overview-heading">
    <div class="overview-today">
      <div class="overview-header">
        <h2 id="overview-heading">教学运行概况</h2>
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

  <div v-if="data" class="stat-grid" aria-label="组织与设备">
    <button v-for="stat in stats" :key="stat.key" class="stat-card" @click="emit('navigate', stat.route)">
      <div class="stat-card-top">
        <span>{{ stat.label }}</span><span class="stat-icon" :class="stat.tone"
          ><el-icon :size="22"><component :is="stat.icon" /></el-icon
        ></span>
      </div>
      <div class="stat-value">
        <strong>{{ data.counts[stat.key] }}</strong>
        <el-icon aria-hidden="true"><ArrowRight /></el-icon>
      </div>
    </button>
  </div>
  <div class="dashboard-columns dashboard-actions">
    <section class="surface">
      <div class="card-heading">
        <div>
          <h2>常用操作</h2>
        </div>
      </div>
      <div class="quick-actions">
        <button @click="emit('navigate', 'students')">
          <span class="quick-icon"><User /></span>
          <div><strong>学生档案</strong><span>新增账号与批量导入</span></div></button
        ><button @click="emit('navigate', 'devices')">
          <span class="quick-icon"><Monitor /></span>
          <div><strong>设备终端</strong><span>查看资源与远程连接</span></div></button
        ><button @click="emit('navigate', 'backups')">
          <span class="quick-icon"><Document /></span>
          <div><strong>备份与恢复</strong><span>保护当前业务数据</span></div></button
        ><button @click="emit('navigate', 'server')">
          <span class="quick-icon"><Cpu /></span>
          <div><strong>服务器信息</strong><span>查看真实主机状态</span></div>
        </button>
      </div>
      <div class="resource-summary">
        <button v-for="item in resourceStats" :key="item.route" @click="emit('navigate', item.route)">
          <strong>{{ item.value ?? '—' }}</strong><span>{{ item.label }}</span>
        </button>
      </div>
    </section>
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
      <el-empty v-else description="暂无班级学习数据" :image-size="70" />
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
      <el-empty v-else description="暂无教师数据" :image-size="70" />
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
    <el-table :data="data.recentAudits || []" empty-text="暂无操作记录"
      ><el-table-column label="时间（北京时间）" width="220"
        ><template #default="{ row }">{{ dateTime(row.at) }}</template></el-table-column
      ><el-table-column prop="userName" label="账号" min-width="130" /><el-table-column
        prop="action"
        label="操作"
        min-width="180" /><el-table-column prop="target" label="目标" min-width="220" show-overflow-tooltip
    /></el-table>
  </section>
</template>

<style scoped>
.dashboard-skeleton {
  margin-bottom: 20px;
  padding: 26px;
}

.overview-panel {
  margin-bottom: 18px;
}

.overview-today {
  padding: 22px 24px 20px;
  background: var(--workspace-hero);
  color: #fff;
}

.overview-header {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  justify-content: space-between;
  gap: 6px 14px;
  margin-bottom: 16px;
}

.overview-header h2 {
  margin: 0;
  color: #f5fbff;
  font-size: 19px;
}

.overview-header time,
.overview-metrics dt {
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
  padding: 12px 14px;
  border: 1px solid #d6f3f333;
  border-radius: 9px;
  background: #dcfbfa13;
}

.overview-metrics dd,
.overview-totals dd {
  margin: 5px 0 0;
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

.overview-metrics small {
  color: var(--workspace-on-hero);
}

.overview-totals {
  grid-template-columns: repeat(4, minmax(0, 1fr));
  padding: 15px 24px;
}

.overview-totals dt,
.overview-totals small {
  color: var(--text-muted);
  font-size: 12px;
}

.overview-totals dd {
  color: #203b4d;
  font-size: 20px;
}

.overview-totals > div {
  min-width: 0;
}

.dashboard-actions,
.admin-panels {
  margin-top: 18px;
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
  border-top: 1px solid #ebf0f3;
}

.rank-index {
  display: grid;
  width: 24px;
  height: 24px;
  color: var(--text-muted);
  background: #edf3f5;
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

@media (max-width: 640px) {
  .dashboard-heading {
    display: flex;
    align-items: flex-start;
    gap: 10px;
  }

  .dashboard-heading > .el-button {
    flex: none;
    margin-top: 3px;
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
    padding: 9px 8px;
  }

  .overview-totals {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    padding: 12px 16px;
  }

  .ranking-list,
  .teacher-activity {
    padding-inline: 16px;
  }
}
</style>

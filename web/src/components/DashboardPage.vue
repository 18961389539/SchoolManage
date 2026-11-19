<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Refresh, Plus, User, School, Monitor, Connection, Document, CircleCheck, Cpu } from '@element-plus/icons-vue'
import { api, post, messageOf, dateTime } from '../api'
import type { BootstrapResult, DashboardResponse } from '../types'
const emit = defineEmits<{ navigate: [key: string] }>()
const loading = ref(false),
  creating = ref(false),
  error = ref(''),
  data = ref<DashboardResponse>({
    schoolName: '',
    counts: { professions: 0, classes: 0, teachers: 0, students: 0, cabinets: 0, devices: 0, nodes: 0 },
    recentAudits: [],
    server: { machineName: '', os: '', framework: '' },
    environment: '',
    version: ''
  })
const stats = [
  { key: 'students', label: '学生档案', icon: User, route: 'students', tone: 'blue' },
  { key: 'teachers', label: '教师档案', icon: User, route: 'teachers', tone: 'teal' },
  { key: 'classes', label: '教学班级', icon: School, route: 'classes', tone: 'indigo' },
  { key: 'devices', label: '实训设备', icon: Monitor, route: 'devices', tone: 'amber' }
] as const
const resourceStats = computed(() => [
  { label: '专业目录', value: data.value.counts?.professions, route: 'professions' },
  { label: '机柜资源', value: data.value.counts?.cabinets, route: 'cabinets' },
  { label: '集群节点', value: data.value.counts?.nodes, route: 'nodes' }
])
async function load() {
  loading.value = true
  error.value = ''
  try {
    data.value = await api<DashboardResponse>('/dashboard')
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
  <section class="page-heading">
    <div>
      <div class="eyebrow">工作空间 / 资源总览</div>
      <h1>控制台</h1>
      <p>从组织信息到实训资源，掌握校内平台的当前状态。</p>
    </div>
    <el-button :icon="Refresh" @click="load">刷新数据</el-button>
  </section>
  <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon class="section-notice" />
  <div class="dashboard-welcome">
    <div>
      <span class="welcome-label">TRAINING RESOURCE WORKSPACE</span>
      <h2>让实训资源，有序连接。</h2>
      <p>统一维护教学组织、设备连接与虚拟资源。所有统计均来自当前平台数据。</p>
      <div class="welcome-status">
        <span class="live-dot"></span>服务已连接<span class="welcome-divider"></span>校内私有化部署
      </div>
    </div>
    <div class="welcome-visual" aria-hidden="true">
      <div class="rack rack-one"><i /><i /><i /></div>
      <div class="rack rack-two"><i /><i /><i /></div>
      <div class="visual-orbit"></div>
    </div>
  </div>
  <div class="stat-grid" v-loading="loading">
    <button v-for="stat in stats" :key="stat.key" class="stat-card" @click="emit('navigate', stat.route)">
      <div class="stat-card-top">
        <span>{{ stat.label }}</span><span class="stat-icon" :class="stat.tone"
          ><el-icon :size="22"><component :is="stat.icon" /></el-icon
        ></span>
      </div>
      <div class="stat-value">{{ data.counts?.[stat.key] ?? '—' }}<span>项</span></div>
      <div class="stat-card-bottom">查看{{ stat.label }}</div>
    </button>
  </div>
  <div class="dashboard-columns">
    <section class="surface">
      <div class="card-heading">
        <div>
          <h2>常用操作</h2>
          <p>从这里开始组织和资源管理</p>
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
          <div><strong>创建备份</strong><span>保护当前业务数据</span></div></button
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
          <span class="small-tag">内置模拟环境</span>
          <h2>先把操作流程跑通</h2>
        </div>
        <el-icon :size="27" class="simulation-icon"><Connection /></el-icon>
      </div>
      <p>无需串口控制器或虚拟化平台，即可使用模拟设备终端与虚拟资源，验证机柜管理、连接回收和资源启停。</p>
      <div class="simulation-note">
        <el-icon><CircleCheck /></el-icon>模拟数据始终带有明确标识
      </div>
      <el-button type="primary" :icon="Plus" :loading="creating" @click="createSimulation">创建模拟环境</el-button
      ><el-button text @click="emit('navigate', 'devices')">查看已有设备</el-button>
    </section>
  </div>
  <section class="surface section-gap">
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

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue'
import { Refresh } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, dateTime, messageOf, post } from '../api'
import ExamPracticalPanel from './ExamPracticalPanel.vue'

interface Project {
  paperQuestionId: string
  stem: string
  sortOrder: number
  deployed: boolean
  status: 'pending' | 'failed' | 'deployed'
  message: string
  attemptedAt: string | null
  deployedAt: string | null
  restartedAt: string | null
}

const props = defineProps<{ url: string }>()
const projects = ref<Project[]>([])
const selectedId = ref('')
const canRestart = ref(false)
const loaded = ref(false)
const loading = ref(false)
const restarting = ref('')
const revision = ref(0)
const error = ref('')
const detailHeading = ref<HTMLElement>()
const selected = computed(() => projects.value.find(project => project.paperQuestionId === selectedId.value))
const deployedCount = computed(() => projects.value.filter(project => project.deployed).length)
const failedCount = computed(() => projects.value.filter(project => project.status === 'failed').length)
let timer = 0
let request = 0
let fetching = false

async function load(silent = false) {
  if (silent && (fetching || restarting.value)) return
  const current = ++request
  fetching = true
  if (!silent) loading.value = true
  try {
    const next = await api<{ items: Project[]; canRestart: boolean }>(props.url)
    if (current !== request) return
    projects.value = next.items
    canRestart.value = next.canRestart
    if (!selected.value?.deployed) selectedId.value = ''
    loaded.value = true
    error.value = ''
  } catch (failure) {
    if (current === request) error.value = messageOf(failure)
  } finally {
    if (current === request) {
      fetching = false
      loading.value = false
    }
  }
}

async function showProject(project: Project) {
  if (selectedId.value === project.paperQuestionId) {
    selectedId.value = ''
    return
  }
  selectedId.value = project.paperQuestionId
  await nextTick()
  detailHeading.value?.focus({ preventScroll: true })
  detailHeading.value?.scrollIntoView({ block: 'nearest' })
}

async function restart(project: Project) {
  try {
    await ElMessageBox.confirm('重启会清除该考生在本题保存的配置。', '重启实操环境', {
      type: 'warning',
      confirmButtonText: '重启',
      cancelButtonText: '取消'
    })
  } catch {
    return
  }
  if (restarting.value) return
  restarting.value = project.paperQuestionId
  try {
    await post(`${props.url}/${project.paperQuestionId}/restart`)
    if (selectedId.value === project.paperQuestionId) revision.value++
    ElMessage.success('环境已恢复到部署时的配置')
    await load()
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    restarting.value = ''
  }
}

onMounted(() => {
  void load()
  timer = window.setInterval(() => void load(true), 5000)
})
onUnmounted(() => {
  request++
  window.clearInterval(timer)
})
</script>

<template>
  <div class="deployment-panel">
    <div class="deployment-toolbar">
      <div>
        <h3>测评项目</h3>
        <p v-if="loaded" class="deployment-count">
          <span>共 {{ projects.length }} 项</span>
          <span>{{ deployedCount }} 项已部署</span>
          <span v-if="failedCount" class="failed-count">{{ failedCount }} 项部署失败</span>
        </p>
      </div>
      <el-button :icon="Refresh" :loading="loading" :disabled="!!restarting" @click="load()">刷新项目</el-button>
    </div>
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" />
    <el-skeleton v-if="loading && !loaded" :rows="3" animated />
    <el-empty v-else-if="loaded && !projects.length" description="暂无模拟实操项目" :image-size="64" />

    <ul v-if="projects.length" v-loading="loading" class="project-list" aria-label="测评项目">
      <li
        v-for="project in projects"
        :key="project.paperQuestionId"
        class="project-row"
        :class="{ 'is-selected': selectedId === project.paperQuestionId }"
      >
        <div class="project-main">
          <h4>第 {{ project.sortOrder + 1 }} 题 · {{ project.stem }}</h4>
          <p v-if="project.message">{{ project.message }}</p>
        </div>
        <div class="project-status">
          <el-tag :type="project.deployed ? 'success' : project.status === 'failed' ? 'danger' : 'info'" effect="light">
            {{ project.deployed ? '已部署' : project.status === 'failed' ? '部署失败' : '待部署' }}
          </el-tag>
          <small v-if="project.restartedAt || project.attemptedAt || project.deployedAt">
            {{ dateTime(project.restartedAt || project.attemptedAt || project.deployedAt) }}
          </small>
        </div>
        <div v-if="project.deployed" class="project-actions">
          <el-button
            link
            type="primary"
            :aria-expanded="selectedId === project.paperQuestionId"
            @click="showProject(project)"
            >{{ selectedId === project.paperQuestionId ? '收起环境' : '查看环境' }}</el-button
          >
          <el-button
            v-if="canRestart"
            link
            type="danger"
            :disabled="!!restarting"
            :loading="restarting === project.paperQuestionId"
            @click="restart(project)"
            >重启</el-button
          >
        </div>
      </li>
    </ul>

    <section v-if="selected" class="project-detail">
      <h3 ref="detailHeading" tabindex="-1">第 {{ selected.sortOrder + 1 }} 题的设备配置</h3>
      <ExamPracticalPanel
        :key="`${selected.paperQuestionId}:${revision}`"
        :url="`${url}/${selected.paperQuestionId}`"
        read-only
      />
    </section>
  </div>
</template>

<style scoped>
.deployment-panel {
  display: grid;
  gap: 14px;
  color: var(--el-text-color-primary);
}

.deployment-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px 16px;
  flex-wrap: wrap;
}

.deployment-toolbar h3,
.project-detail h3 {
  margin: 0;
  font-size: 15px;
}

.deployment-count {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  margin: 3px 0 0;
  color: var(--el-text-color-regular);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.failed-count {
  color: var(--el-color-danger);
}

.project-list {
  display: grid;
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.project-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(130px, auto) minmax(98px, auto);
  align-items: center;
  gap: 10px 14px;
  padding: 13px 16px;
  border: 1px solid var(--el-border-color);
  border-radius: 9px;
  background: #fff;
}

.project-row.is-selected {
  border-color: var(--el-color-primary-light-5);
  background: var(--el-color-primary-light-9);
}

.project-main h4 {
  margin: 0;
  color: var(--el-text-color-primary);
  font-size: 14px;
  line-height: 1.55;
  overflow-wrap: anywhere;
}

.project-main p,
.project-status small {
  margin: 4px 0 0;
  color: var(--el-text-color-regular);
  font-size: 12px;
  line-height: 1.5;
  overflow-wrap: anywhere;
}

.project-status {
  display: grid;
  justify-items: start;
}

.project-status small {
  font-variant-numeric: tabular-nums;
}

.project-actions {
  display: flex;
  align-items: center;
  justify-content: end;
  gap: 12px;
}

.project-actions .el-button {
  margin: 0;
}

.project-detail {
  min-width: 0;
  padding: 14px 16px;
  border: 1px solid var(--el-border-color);
  border-radius: 9px;
}

.project-detail h3 {
  margin-bottom: 10px;
}

@media (max-width: 600px) {
  .project-row {
    grid-template-columns: minmax(0, 1fr) auto;
    padding: 12px;
  }

  .project-main {
    grid-column: 1 / -1;
  }

  .project-detail {
    padding: 12px;
  }
}
</style>

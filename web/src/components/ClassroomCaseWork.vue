<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { api, messageOf, post, put } from '../api'
import type { CaseDevice, CaseGrade, CaseTopologyBlueprint } from '../caseWork'
import type { LearningRecord } from '../types'
import CaseGradeHistory from './CaseGradeHistory.vue'
import VmConsoleDialog from './VmConsoleDialog.vue'
import ClassroomLiveShare from './ClassroomLiveShare.vue'
import CaseTopologyView from './CaseTopologyView.vue'

const props = defineProps<{ url: string; startUrl?: string; hasRecord?: boolean }>()
const emit = defineEmits<{ started: [record: LearningRecord]; finished: []; saved: [] }>()

interface Work {
  sessionId: string
  status: 'active' | 'saved' | 'ended'
  workId: string
  revision: number
  devices: CaseDevice[]
  topology: CaseTopologyBlueprint | null
  grades?: CaseGrade[]
  virtualMachine: { id: string; name: string; status: string } | null
}

const work = ref<Work | null>(null)
const machineVisible = ref(false)
const action = ref('')
const error = ref('')
const selected = ref('')
const draft = ref<CaseDevice['configuration'] | null>(null)
const readOnly = computed(() => !props.startUrl)
const assistance = ref(false)
const canEdit = computed(() => !readOnly.value || assistance.value)
let refreshTimer = 0
const device = computed(() => work.value?.devices.find(item => item.id === selected.value))
const dirty = computed(() => JSON.stringify(draft.value) !== JSON.stringify(device.value?.configuration ?? null))
const active = computed(() => work.value?.status === 'active' && !!work.value.workId)
const grading = computed(() => action.value === 'trial' || action.value === 'finish')
const gradingProgress = ref(0)
const gradingLabel = computed(() => {
  if (gradingProgress.value === 100) return '评分完成'
  if (gradingProgress.value === 30) return '正在按评分表评分'
  return '正在保存实操配置'
})

function selectDevice(id: string) {
  selected.value = id
  draft.value = device.value ? JSON.parse(JSON.stringify(device.value.configuration)) : null
}

async function readWork() {
  work.value = await api<Work>(props.url)
  const next = work.value.devices.find(item => item.id === selected.value) || work.value.devices[0]
  selectDevice(next?.id || '')
}

async function run(name: string, operation: () => Promise<void>) {
  if (action.value) return
  action.value = name
  error.value = ''
  try {
    await operation()
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    action.value = ''
  }
}

function start() {
  if (!props.startUrl) return
  const url = props.startUrl
  void run('start', async () => {
    emit('started', await post<LearningRecord>(url))
    await readWork()
  })
}

function version() {
  return { workId: work.value?.workId, revision: work.value?.revision }
}

async function apply() {
  if (!work.value || !device.value || !draft.value || !dirty.value) return
  const configuration = draft.value
  const { revision } = await put<{ revision: number }>(`${props.url}/devices/${device.value.id}`, {
    ...version(),
    configuration
  })
  work.value.revision = revision
  device.value.configuration = JSON.parse(JSON.stringify(configuration))
}

function save() {
  void run('save', async () => {
    await apply()
    await post(`${props.url}/save`, version())
    if (work.value) {
      work.value.status = 'saved'
      work.value.devices = []
      selectDevice('')
    }
    emit('saved')
  })
}

async function score(final: boolean) {
  if (final) {
    try {
      await ElMessageBox.confirm('提交后将销毁本次实操环境，并保留评分结果。', '销毁评分', {
        type: 'warning',
        confirmButtonText: '销毁并评分',
        cancelButtonText: '继续实操'
      })
    } catch {
      return
    }
  }
  const path = final ? 'finish' : 'trial'
  await run(path, async () => {
    gradingProgress.value = 0
    await apply()
    gradingProgress.value = 30
    const grade = await post<CaseGrade>(`${props.url}/${path}`, version())
    if (work.value) {
      work.value.grades = [grade, ...(work.value.grades || []).filter(item => item.id !== grade.id)]
      if (final) {
        work.value.status = 'ended'
        work.value.devices = []
        selectDevice('')
      }
    }
    gradingProgress.value = 100
    if (final) emit('finished')
  })
}

onMounted(() => {
  if (props.hasRecord || readOnly.value) void run('load', readWork)
})

watch(assistance, allowed => {
  window.clearInterval(refreshTimer)
  if (!allowed) return
  const refresh = () => {
    if (!dirty.value && !action.value && work.value?.status === 'active') void run('load', readWork)
  }
  refresh()
  refreshTimer = window.setInterval(refresh, 3000)
})
onUnmounted(() => window.clearInterval(refreshTimer))
</script>

<template>
  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" />
  <div class="case-controls">
    <el-button v-if="readOnly || error" :disabled="!!action" @click="run('load', readWork)">刷新环境</el-button>
    <el-button
      v-if="!readOnly && !active"
      type="primary"
      :disabled="!!action"
      :loading="action === 'start'"
      @click="start"
    >
      {{ work?.status === 'saved' ? '恢复实操' : work?.status === 'ended' ? '再次实操' : '开始实操' }}
    </el-button>
    <el-tag v-if="work?.status === 'saved'" type="success">环境已保存</el-tag>
    <el-button
      v-if="work?.virtualMachine && work.status === 'active'"
      :disabled="work.virtualMachine.status !== 'running'"
      @click="machineVisible = true"
      >连接虚拟机</el-button
    >
  </div>
  <ClassroomLiveShare
    v-if="work && (readOnly || active)"
    :key="`${work.sessionId}:${work.workId}`"
    :session-id="work.sessionId"
    :monitor="readOnly"
    @assistance="assistance = $event"
  />
  <VmConsoleDialog
    v-if="work?.virtualMachine && work.status === 'active'"
    v-model="machineVisible"
    scope="classroom"
    :target-id="work.sessionId"
    :machine-name="work.virtualMachine.name"
  />
  <template v-if="active || readOnly">
    <CaseTopologyView v-if="work?.topology" :topology="work.topology" :devices="work.devices" />
    <el-empty v-if="work && !work.devices.length" description="当前实操环境没有可用的模拟设备" :image-size="60" />
    <template v-if="draft && work?.devices.length">
      <el-select :model-value="selected" :disabled="!!action || dirty" aria-label="选择模拟设备" @change="selectDevice">
        <el-option v-for="item in work.devices" :key="item.id" :label="item.name" :value="item.id" />
      </el-select>
      <el-form label-position="top" class="case-device-form">
        <el-form-item label="主机名">
          <el-input v-model="draft.hostname" maxlength="63" :readonly="!canEdit" :disabled="!!action" />
        </el-form-item>
        <el-form-item v-for="(port, name) in draft.interfaces" :key="name" :label="name">
          <div class="case-port">
            <el-switch
              v-model="port.enabled"
              :disabled="!canEdit || !!action"
              active-text="启用"
              inactive-text="停用"
            />
            <el-input
              v-model="port.description"
              maxlength="100"
              placeholder="接口说明"
              :readonly="!canEdit"
              :disabled="!!action"
            />
          </div>
        </el-form-item>
      </el-form>
    </template>
    <div v-if="active && canEdit" class="case-controls">
      <el-button :disabled="!dirty || !!action" @click="run('apply', apply)">应用配置</el-button>
      <el-button v-if="dirty" :disabled="!!action" @click="selectDevice(selected)">撤销修改</el-button>
      <template v-if="!readOnly">
        <el-button :disabled="!!action" @click="save">保存退出</el-button>
        <el-button :disabled="!!action" @click="score(false)">尝试评分</el-button>
        <el-button type="primary" :disabled="!!action" @click="score(true)">销毁评分</el-button>
      </template>
    </div>
    <div v-if="grading" role="status">
      <span>正在核对实操配置</span>
      <el-progress :percentage="gradingProgress" :format="() => gradingLabel" />
    </div>
  </template>
  <CaseGradeHistory v-if="work?.grades?.length" :grades="work.grades" />
</template>

<style scoped>
.case-controls {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  margin: 12px 0;
}
.case-controls :deep(.el-button + .el-button) {
  margin-left: 0;
}
.case-device-form {
  margin-top: 14px;
}
.case-port {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  width: 100%;
}
.case-port :deep(.el-input) {
  flex: 1 1 200px;
}
</style>

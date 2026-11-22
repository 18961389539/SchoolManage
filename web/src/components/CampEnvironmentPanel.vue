<script setup lang="ts">
import { defineAsyncComponent, ref, watch } from 'vue'
import { Monitor, Refresh } from '@element-plus/icons-vue'
import { api, messageOf } from '../api'
import { virtualMachineStatusText } from '../camp'
import type { CampEnvironment } from '../types'
import VmConsoleDialog from './VmConsoleDialog.vue'

const TerminalDialog = defineAsyncComponent(() => import('./TerminalDialog.vue'))
const DeviceWebConsole = defineAsyncComponent(() => import('./DeviceWebConsole.vue'))

const props = defineProps<{ sourceUrl: string; emptyText?: string; attemptId?: string }>()

const environment = ref<CampEnvironment | null>(null)
const loading = ref(false)
const error = ref('')
const terminalDevice = ref<Record<string, unknown> | null>(null)
const webDevice = ref<{ id: string; name: string } | null>(null)
const machine = ref<CampEnvironment['virtualMachines'][number] | null>(null)
const machineVisible = ref(false)

function connectMachine(row: CampEnvironment['virtualMachines'][number]) {
  machine.value = row
  machineVisible.value = true
}

async function load() {
  if (!props.sourceUrl) return
  loading.value = true
  error.value = ''
  try {
    environment.value = await api<CampEnvironment>(props.sourceUrl)
  } catch (failure) {
    environment.value = null
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

watch(
  () => props.sourceUrl,
  url => {
    if (url) void load()
  },
  { immediate: true }
)

function openTerminal(device: { id: string; name: string }) {
  terminalDevice.value = { ...device, connectionType: 'simulated', isSimulated: true }
}
</script>

<template>
  <div class="camp-environment">
    <div class="environment-toolbar">
      <div v-if="environment" class="environment-node">
        <span>实训节点</span>
        <strong>{{ environment.node?.name || '未配置启动节点' }}</strong>
        <el-tag v-if="environment.node" :type="environment.node.enabled ? 'success' : 'info'" size="small">
          {{ environment.node.enabled ? '已启用' : '已停用' }}
        </el-tag>
      </div>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新环境</el-button>
    </div>
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" />
    <div v-else-if="loading && !environment" class="environment-loading"><el-skeleton :rows="4" animated /></div>
    <template v-else-if="environment">
      <el-table :data="environment.devices" row-key="id" empty-text="当前节点暂无可用模拟设备">
        <el-table-column prop="name" label="设备" min-width="170" show-overflow-tooltip />
        <el-table-column prop="cabinetName" label="机柜" min-width="120" show-overflow-tooltip />
        <el-table-column label="操作" width="220">
          <template #default="{ row }">
            <el-button link type="primary" :icon="Monitor" @click="openTerminal(row as { id: string; name: string })">
              打开终端
            </el-button>
            <el-button link type="primary" @click="webDevice = row as { id: string; name: string }">Web 管理</el-button>
          </template>
        </el-table-column>
      </el-table>
      <el-table :data="environment.virtualMachines" row-key="id" empty-text="当前节点暂无虚拟机">
        <el-table-column label="虚拟机" min-width="180">
          <template #default="{ row }">
            <div class="machine-name">
              <span>{{ row.name }}</span>
              <el-tag v-if="row.selected" size="small" type="warning" effect="plain">测评机</el-tag>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="105">
          <template #default="{ row }">
            <el-tag :type="row.status === 'running' ? 'success' : 'info'" size="small">
              {{ virtualMachineStatusText(row.status) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="配置" min-width="180">
          <template #default="{ row }">{{ row.cpuCount }} 核 · {{ row.memoryMb }} MB · {{ row.diskGb }} GB</template>
        </el-table-column>
        <el-table-column v-if="attemptId" label="操作" width="110">
          <template #default="{ row }">
            <el-button
              v-if="row.selected"
              link
              type="primary"
              :disabled="row.status !== 'running'"
              @click="connectMachine(row as CampEnvironment['virtualMachines'][number])"
              >连接</el-button
            >
          </template>
        </el-table-column>
      </el-table>
      <p v-if="emptyText" class="environment-hint">{{ emptyText }}</p>
    </template>
  </div>

  <TerminalDialog
    v-if="terminalDevice"
    :key="String(terminalDevice.id)"
    :device="terminalDevice"
    @close="terminalDevice = null"
  />
  <DeviceWebConsole v-if="webDevice" :device="webDevice" @close="webDevice = null" />
  <VmConsoleDialog
    v-if="machine && attemptId"
    v-model="machineVisible"
    scope="camp"
    :target-id="attemptId"
    :machine-name="machine.name"
  />
</template>

<style scoped>
.camp-environment {
  display: grid;
  gap: 12px;
}

.environment-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 8px;
}

.environment-node {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 9px;
  color: #56697c;
  font-size: 12px;
}

.environment-node strong {
  color: #2a4a67;
  font-size: 13px;
}

.environment-loading {
  padding: 12px 0;
}

.machine-name {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 7px;
}

.environment-hint {
  margin: 0;
  color: #64788a;
  font-size: 12px;
}
</style>

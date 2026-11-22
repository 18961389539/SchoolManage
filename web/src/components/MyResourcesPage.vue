<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, ref } from 'vue'
import { Monitor, Refresh } from '@element-plus/icons-vue'
import { api, messageOf } from '../api'
import type { AssignedDevice, AssignedVirtualMachine, MyResourcesResponse } from '../types'
import VmConsoleDialog from './VmConsoleDialog.vue'

const TerminalDialog = defineAsyncComponent(() => import('./TerminalDialog.vue'))
const DeviceWebConsole = defineAsyncComponent(() => import('./DeviceWebConsole.vue'))

const resources = ref<MyResourcesResponse>()
const loading = ref(true)
const error = ref('')
const activeDevice = ref<AssignedDevice | null>(null)
const webDevice = ref<AssignedDevice | null>(null)
const machine = ref<AssignedVirtualMachine | null>(null)
const machineVisible = ref(false)
const terminalDevice = computed(() =>
  activeDevice.value ? { ...activeDevice.value, connectionType: 'simulated' } : null
)
let request: AbortController | undefined

async function load() {
  request?.abort()
  const current = new AbortController()
  request = current
  loading.value = true
  error.value = ''
  try {
    const result = await api<MyResourcesResponse>('/my-resources', { signal: current.signal })
    if (request === current) resources.value = result
  } catch (failure) {
    if (!current.signal.aborted) {
      resources.value = undefined
      error.value = messageOf(failure)
    }
  } finally {
    if (request === current) {
      loading.value = false
      request = undefined
    }
  }
}

function closeTerminal() {
  activeDevice.value = null
  void load()
}

function openMachine(row: AssignedVirtualMachine) {
  machine.value = row
  machineVisible.value = true
}

function deviceStatus(device: AssignedDevice) {
  if (!device.canAccess) return '当前不可连接'
  if (device.sessionActive) return '正在使用'
  return '可连接'
}

function machineStatus(status: string) {
  if (status === 'running') return '运行中'
  if (status === 'stopped') return '已停止'
  return '状态未知'
}

onMounted(load)
onBeforeUnmount(() => request?.abort())
</script>

<template>
  <section class="page-heading">
    <div>
      <h1>我的实训资源</h1>
      <p>查看已分配的模拟节点、设备和虚拟机。</p>
    </div>
    <el-button :icon="Refresh" :loading="loading" @click="load">刷新资源</el-button>
  </section>

  <section v-if="error" class="surface resource-empty">
    <el-result icon="error" title="实训资源加载失败" :sub-title="error">
      <template #extra><el-button type="primary" @click="load">重试</el-button></template>
    </el-result>
  </section>
  <section v-else-if="!resources && loading" class="surface resource-empty" aria-live="polite">
    <p>正在加载实训资源…</p>
    <el-skeleton :rows="3" animated aria-hidden="true" />
  </section>
  <section v-else-if="!resources?.nodes.length" class="surface resource-empty">
    <el-empty description="暂未分配实训资源">
      <p>请联系平台管理员，将你的账号分配到实训节点。</p>
    </el-empty>
  </section>
  <div v-else class="my-resource-sections" :aria-busy="loading">
    <section class="surface" aria-labelledby="assigned-nodes">
      <div class="resource-heading">
        <h2 id="assigned-nodes">实训节点</h2>
        <span>{{ resources.nodes.length }} 个节点</span>
      </div>
      <ul class="assigned-nodes">
        <li v-for="node in resources.nodes" :key="node.id">
          <strong>{{ node.name }}</strong>
          <el-tag :type="node.enabled ? 'success' : 'info'" effect="light" size="small">
            {{ node.enabled ? '已启用' : '已停用' }}
          </el-tag>
        </li>
      </ul>
    </section>

    <section class="surface" aria-labelledby="assigned-devices">
      <div class="resource-heading">
        <h2 id="assigned-devices">实训设备</h2>
        <span>{{ resources.devices.length }} 台设备</span>
      </div>
      <el-table :data="resources.devices" row-key="id" empty-text="已分配节点暂未关联实训设备">
        <el-table-column prop="name" label="设备名称" min-width="170" show-overflow-tooltip />
        <el-table-column prop="nodeName" label="实训节点" min-width="150" show-overflow-tooltip />
        <el-table-column prop="cabinetName" label="所属机柜" min-width="130" show-overflow-tooltip />
        <el-table-column label="状态" width="115">
          <template #default="{ row }">
            <el-tag :type="!row.canAccess ? 'info' : row.sessionActive ? 'warning' : 'success'" size="small">
              {{ deviceStatus(row as AssignedDevice) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="210">
          <template #default="{ row }">
            <el-button
              link
              type="primary"
              :icon="Monitor"
              :disabled="loading || !row.canAccess || row.sessionActive"
              :aria-label="`打开${row.name}终端`"
              @click="activeDevice = row as AssignedDevice"
            >
              打开终端
            </el-button>
            <el-button
              v-if="row.isSimulated"
              link
              type="primary"
              :disabled="loading || !row.canAccess"
              :aria-label="`打开${row.name}Web 管理`"
              @click="webDevice = row as AssignedDevice"
            >
              Web 管理
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </section>

    <section class="surface" aria-labelledby="assigned-machines">
      <div class="resource-heading">
        <h2 id="assigned-machines">虚拟机</h2>
        <span>{{ resources.virtualMachines.length }} 台</span>
      </div>
      <el-table :data="resources.virtualMachines" row-key="id" empty-text="已分配节点暂无模拟虚拟机">
        <el-table-column prop="name" label="虚拟机名称" min-width="155" show-overflow-tooltip />
        <el-table-column prop="nodeName" label="实训节点" min-width="135" show-overflow-tooltip />
        <el-table-column prop="ipAddress" label="IP 地址" min-width="130" />
        <el-table-column label="状态" width="106">
          <template #default="{ row }">
            <el-tag :type="row.status === 'running' ? 'success' : 'info'" size="small">
              {{ machineStatus(row.status) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="配置" min-width="172">
          <template #default="{ row }">{{ row.cpuCount }} 核 · {{ row.memoryMb }} MB · {{ row.diskGb }} GB</template>
        </el-table-column>
        <el-table-column label="操作" width="118">
          <template #default="{ row }">
            <el-button
              link
              type="primary"
              :icon="Monitor"
              :disabled="!row.canAccess || row.status !== 'running'"
              @click="openMachine(row as AssignedVirtualMachine)"
              >连接</el-button
            >
          </template>
        </el-table-column>
      </el-table>
    </section>
  </div>

  <TerminalDialog v-if="terminalDevice" :key="terminalDevice.id" :device="terminalDevice" @close="closeTerminal" />
  <DeviceWebConsole v-if="webDevice" :device="webDevice" @close="webDevice = null" @changed="load" />
  <VmConsoleDialog
    v-if="machine"
    v-model="machineVisible"
    scope="machine"
    :target-id="machine.id"
    :machine-name="machine.name"
  />
</template>

<style scoped>
.my-resource-sections {
  display: grid;
  min-width: 0;
  gap: 22px;
}

.resource-heading {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 8px 16px;
  padding: 20px 22px 14px;
}

.resource-heading h2 {
  margin: 0;
  color: #1c334e;
  font-size: 17px;
}

.resource-heading span {
  color: #596d85;
  font-size: 13px;
}

.assigned-nodes {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 245px), 1fr));
  gap: 10px;
  padding: 0 22px 20px;
  margin: 0;
  list-style: none;
}

.assigned-nodes li {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  background: #f6f8fc;
  border: 1px solid #e8eef5;
  border-radius: 7px;
}

.assigned-nodes strong {
  overflow-wrap: anywhere;
  font-size: 14px;
}

.resource-empty {
  padding: 24px;
}

.resource-empty p {
  color: #596d85;
  font-size: 14px;
}
</style>

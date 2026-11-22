<script setup lang="ts">
import { computed } from 'vue'
import type { CaseDevice, CaseTopologyBlueprint } from '../caseWork'

const props = defineProps<{
  topology: CaseTopologyBlueprint
  devices: { id: string; name: string; configuration?: CaseDevice['configuration'] }[]
}>()
const byId = computed(() => new Map(props.devices.map(item => [item.id, item])))
const rows = computed(() =>
  props.topology.links.map(link => {
    const from = byId.value.get(link.fromDeviceId)
    const to = byId.value.get(link.toDeviceId)
    const fromPort = from?.configuration?.interfaces[link.fromPort]
    const toPort = to?.configuration?.interfaces[link.toPort]
    return {
      network: link.network,
      from: `${from?.name ?? '设备不可用'} / ${link.fromPort}`,
      to: `${to?.name ?? '设备不可用'} / ${link.toPort}`,
      state: fromPort && toPort ? (fromPort.enabled && toPort.enabled ? 'up' : 'down') : null
    }
  })
)
</script>

<template>
  <section aria-label="部署拓扑">
    <h3>部署拓扑</h3>
    <p>{{ topology.deviceIds.length }} 台设备 · {{ rows.length }} 条连线</p>
    <el-table v-if="rows.length" :data="rows" size="small">
      <el-table-column prop="network" label="网络" min-width="95" />
      <el-table-column prop="from" label="起点接口" min-width="145" />
      <el-table-column prop="to" label="终点接口" min-width="145" />
      <el-table-column v-if="rows.some(row => row.state)" label="链路" width="80">
        <template #default="{ row }">
          <el-tag v-if="row.state" size="small" :type="row.state === 'up' ? 'success' : 'warning'">
            {{ row.state === 'up' ? '连通' : '断开' }}
          </el-tag>
          <span v-else>—</span>
        </template>
      </el-table-column>
    </el-table>
  </section>
</template>

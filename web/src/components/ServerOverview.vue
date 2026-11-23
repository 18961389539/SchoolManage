<script setup lang="ts">
import { computed, ref } from 'vue'
import { Connection, Cpu, Monitor, Odometer } from '@element-plus/icons-vue'
import { dateTime } from '../api'
import type { Row } from '../api'

const props = defineProps<{ data: Row | null; loading?: boolean }>()

const networkFilter = ref<'active' | 'all'>('active')
const gb = (bytes?: number) => (bytes == null ? '—' : `${(bytes / 1024 ** 3).toFixed(1)} GB`)
const percent = (value?: number) => (value == null ? '—' : `${Math.round(value)}%`)
const mbps = (value?: number) => (value ? `${value} Mbps` : '—')
const list = (value?: unknown[]) => (value?.length ? value.join('、') : '—')

function uptime(seconds?: number) {
  if (seconds == null) return '—'
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  if (days) return `${days} 天 ${hours} 小时`
  if (hours) return `${hours} 小时 ${minutes} 分钟`
  return `${minutes} 分钟`
}

const cpu = computed(() => props.data?.cpu || {})
const memory = computed(() => props.data?.memory || {})
const processors = computed<Row[]>(() => cpu.value.processors || [])
const modules = computed<Row[]>(() => memory.value.modules || [])
const networks = computed<Row[]>(() => props.data?.networks || [])
// Virtual adapters (WAN Miniport, filter drivers, loopback) share the physical MAC and pollute the list.
const physicalTypes = new Set(['Ethernet', 'Wireless80211'])
const virtualName = /Filter Driver|QoS Packet|Virtual WiFi|WFP|Miniport|Pseudo|Loopback/i
const isPhysical = (row: Row) =>
  row.status === 'Up' &&
  physicalTypes.has(String(row.type)) &&
  !!row.macAddress &&
  !virtualName.test(`${row.description || ''} ${row.name || ''}`)
const activeNetworks = computed(() => {
  const seen = new Set<string>()
  return networks.value.filter(row => {
    if (!isPhysical(row)) return false
    const mac = String(row.macAddress)
    if (seen.has(mac)) return false
    seen.add(mac)
    return true
  })
})
const activeCount = computed(() => activeNetworks.value.length)
const visibleNetworks = computed(() => {
  const rows = networkFilter.value === 'active' ? activeNetworks.value : [...networks.value]
  const score = (row: Row) => (isPhysical(row) ? 0 : row.status === 'Up' ? 1 : 2)
  return rows.sort((a, b) => score(a) - score(b) || String(a.name).localeCompare(String(b.name)))
})
</script>

<template>
  <div v-if="!data" class="surface server-empty">
    <el-skeleton v-if="loading" :rows="5" animated />
    <el-empty v-else description="暂未获取服务器信息">
      <p class="server-empty-hint">请稍后刷新本页重试。采集不到的字段会标记为不可获取。</p>
    </el-empty>
  </div>
  <template v-else>
    <el-alert
      v-if="data.warnings?.length"
      :title="`采集提示：${data.warnings.join('；')}`"
      type="warning"
      show-icon
      :closable="false"
      class="section-notice"
    />

    <section class="surface server-summary">
      <div class="summary-grid">
        <div>
          <span
            ><el-icon><Monitor /></el-icon>主机名称</span
          >
          <strong>{{ data.hostName || '—' }}</strong>
        </div>
        <div>
          <span
            ><el-icon><Monitor /></el-icon>操作系统</span
          >
          <strong class="wide">{{ data.osDescription || data.operatingSystem || '—' }}</strong>
        </div>
        <div>
          <span
            ><el-icon><Cpu /></el-icon>CPU 核心</span
          >
          <strong>{{ cpu.physicalCoreCount ?? '—' }} 物理 / {{ cpu.logicalCoreCount ?? '—' }} 逻辑</strong>
        </div>
        <div>
          <span
            ><el-icon><Odometer /></el-icon>内存</span
          >
          <strong>{{ gb(memory.totalBytes) }}</strong>
        </div>
        <div>
          <span
            ><el-icon><Connection /></el-icon>运行时间</span
          >
          <strong>{{ uptime(data.uptimeSeconds) }}</strong>
        </div>
        <div>
          <span>采集时间</span>
          <strong class="wide">{{ dateTime(data.collectedAt) }}</strong>
        </div>
      </div>
      <div class="summary-meta">
        <span>系统架构 {{ data.architecture || '—' }}</span>
        <span>启动于 {{ dateTime(data.bootTime) }}</span>
        <el-tag v-if="data.isSimulated" type="warning" size="small" effect="plain">模拟资源</el-tag>
        <el-tag v-else type="success" size="small" effect="plain">真实主机</el-tag>
      </div>
    </section>

    <div class="server-blocks">
      <section class="surface server-block">
        <div class="block-heading">
          <div>
            <h2>处理器</h2>
            <p>{{ cpu.processorCount ?? processors.length }} 颗物理处理器</p>
          </div>
          <span class="block-metric">{{ percent(cpu.usagePercent) }}</span>
        </div>
        <div class="usage-track" aria-hidden="true">
          <i :style="{ width: `${Math.min(100, cpu.usagePercent || 0)}%` }" />
        </div>
        <el-table :data="processors" size="small" empty-text="采集不到处理器明细">
          <el-table-column prop="name" label="型号" min-width="220" show-overflow-tooltip />
          <el-table-column label="物理核心" width="100" align="right">
            <template #default="{ row }">{{ row.coreCount ?? '—' }}</template>
          </el-table-column>
          <el-table-column label="逻辑核心" width="100" align="right">
            <template #default="{ row }">{{ row.logicalCoreCount ?? '—' }}</template>
          </el-table-column>
          <el-table-column label="使用率" width="90" align="right">
            <template #default="{ row }">{{ percent(row.usagePercent) }}</template>
          </el-table-column>
        </el-table>
        <p class="table-scroll-hint server-scroll-hint">左右滑动表格可查看完整列</p>
      </section>

      <section class="surface server-block">
        <div class="block-heading">
          <div>
            <h2>内存</h2>
            <p>可用 {{ gb(memory.availableBytes) }} / 共 {{ gb(memory.totalBytes) }}</p>
          </div>
          <span class="block-metric">{{ percent(memory.usagePercent) }}</span>
        </div>
        <div class="usage-track" aria-hidden="true">
          <i :style="{ width: `${Math.min(100, memory.usagePercent || 0)}%` }" />
        </div>
        <el-table :data="modules" size="small" empty-text="采集不到内存条明细">
          <el-table-column prop="slot" label="插槽" width="110" />
          <el-table-column label="容量" width="100" align="right">
            <template #default="{ row }">{{ gb(row.capacityBytes) }}</template>
          </el-table-column>
          <el-table-column prop="type" label="类型" width="90" />
          <el-table-column label="主频" width="100" align="right">
            <template #default="{ row }">{{ row.speedMhz ? `${row.speedMhz} MHz` : '—' }}</template>
          </el-table-column>
          <el-table-column prop="manufacturer" label="厂商" min-width="140" show-overflow-tooltip />
        </el-table>
        <p class="table-scroll-hint server-scroll-hint">左右滑动表格可查看完整列</p>
      </section>
    </div>

    <section class="surface server-block server-network">
      <div class="block-heading">
        <div>
          <h2>网络接口</h2>
          <p>共 {{ networks.length }} 个，在用 {{ activeCount }} 个</p>
        </div>
        <el-radio-group v-model="networkFilter" size="small">
          <el-radio-button value="active">在用接口</el-radio-button>
          <el-radio-button value="all">全部</el-radio-button>
        </el-radio-group>
      </div>
      <el-table :data="visibleNetworks" size="small" row-key="macAddress" max-height="460" scrollbar-always-on>
        <el-table-column type="expand">
          <template #default="{ row }">
            <div class="network-detail">
              <div>
                <span>IPv6</span>
                <p>{{ list(row.ipv6) }}</p>
              </div>
              <div>
                <span>网关</span>
                <p>{{ list(row.gateways) }}</p>
              </div>
              <div>
                <span>DNS</span>
                <p>{{ list(row.dnsServers) }}</p>
              </div>
              <div v-if="row.addresses?.length" class="address-list">
                <div v-for="address in row.addresses" :key="address.address" class="address-row">
                  <code>{{ address.address }}</code>
                  <span
                    >{{ address.family }} · 掩码 {{ address.subnetMask || '—' }} · 前缀
                    {{ address.prefixLength ?? '—' }}</span
                  >
                </div>
              </div>
            </div>
          </template>
        </el-table-column>
        <el-table-column prop="name" label="接口" width="140" show-overflow-tooltip />
        <el-table-column prop="description" label="说明" min-width="220" show-overflow-tooltip />
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag :type="row.status === 'Up' ? 'success' : 'info'" size="small" effect="plain">
              {{ row.status === 'Up' ? '已连接' : '未连接' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="macAddress" label="MAC 地址" width="150" />
        <el-table-column label="IPv4" min-width="130">
          <template #default="{ row }">{{ list(row.ipv4) }}</template>
        </el-table-column>
        <el-table-column label="速率" width="100" align="right">
          <template #default="{ row }">{{ mbps(row.speedMbps) }}</template>
        </el-table-column>
        <el-table-column prop="type" label="类型" width="130" show-overflow-tooltip />
      </el-table>
      <p class="table-scroll-hint server-scroll-hint">左右滑动表格可查看完整列</p>
    </section>
  </template>
</template>

<style scoped>
.server-empty {
  padding: 36px 24px;
  text-align: center;
}

.server-empty-hint {
  max-width: 36ch;
  margin: 8px auto 0;
  color: var(--el-text-color-regular);
  font-size: 12px;
  line-height: 1.5;
}

.server-summary {
  padding: 22px 24px 18px;
}

.summary-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 18px 26px;
}

.summary-grid > div {
  display: grid;
  gap: 5px;
  min-width: 0;
}

.summary-grid span {
  display: flex;
  align-items: center;
  gap: 6px;
  color: #64788a;
  font-size: 12px;
}

.summary-grid strong {
  overflow: hidden;
  color: #1f3b54;
  font-size: 15px;
  font-weight: 650;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.summary-grid strong.wide {
  font-size: 14px;
}

.summary-meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 18px;
  margin-top: 18px;
  padding-top: 14px;
  border-top: 1px solid #e4eeea;
  color: #64788a;
  font-size: 12px;
}

.server-blocks {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
  margin-top: 16px;
}

.server-block {
  min-width: 0;
}

.block-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 18px 22px 12px;
}

.block-heading h2 {
  margin: 0 0 4px;
  font-size: 16px;
}

.block-heading p {
  margin: 0;
  color: #64788a;
  font-size: 12px;
}

.block-metric {
  color: #146d66;
  font-size: 22px;
  font-weight: 650;
  font-variant-numeric: tabular-nums;
}

.usage-track {
  height: 5px;
  margin: 0 22px 14px;
  overflow: hidden;
  background: #e8eeea;
  border-radius: 4px;
}

.usage-track i {
  display: block;
  height: 100%;
  background: linear-gradient(90deg, #2f9d8b, #4cb8a6);
  border-radius: inherit;
}

.server-network {
  margin-top: 16px;
}

.network-detail {
  display: grid;
  gap: 10px;
  padding: 6px 16px 14px 48px;
}

.network-detail > div:not(.address-list) {
  display: grid;
  grid-template-columns: 60px minmax(0, 1fr);
  gap: 12px;
  align-items: baseline;
}

.network-detail span {
  color: #64788a;
  font-size: 12px;
}

.network-detail p {
  margin: 0;
  color: #34495e;
  font-size: 13px;
  line-height: 1.7;
  overflow-wrap: anywhere;
}

.address-list {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 6px;
  margin-top: 4px;
}

.address-row {
  display: grid;
  width: 100%;
  grid-template-columns: minmax(0, 320px) minmax(0, 1fr);
  gap: 12px;
  align-items: baseline;
  padding: 7px 10px;
  background: #f3f8f6;
  border-radius: 6px;
}

.address-row code {
  color: #2f455c;
  font-size: 12px;
  overflow-wrap: anywhere;
}

.address-row span {
  color: #64788a;
  font-size: 12px;
}

@media (max-width: 1000px) {
  .server-blocks {
    grid-template-columns: 1fr;
  }
}

.server-scroll-hint {
  display: none;
}

@media (max-width: 700px) {
  /* 展开行继承表格自然宽度（窄屏下可达 1008px），必须锁回视口内，否则地址信息落在可视区外。 */
  .network-detail {
    position: sticky;
    left: 0;
    box-sizing: border-box;
    width: calc(100vw - 38px);
    padding: 6px 12px 14px 20px;
  }

  .network-detail > div:not(.address-list) {
    grid-template-columns: 48px minmax(0, 1fr);
  }

  .address-row {
    grid-template-columns: minmax(0, 1fr);
    gap: 2px;
  }

  .server-scroll-hint {
    display: block;
    margin: 8px 0 0;
  }
}
</style>

<script setup lang="ts">
import { computed } from 'vue'
import { dateTime } from '../api'
defineOptions({ name: 'DataRecord' })
const props = defineProps<{ data: any; unavailableText?: string }>()
const labels: Record<string, string> = {
  id: '记录 ID',
  name: '名称',
  userName: '账号',
  displayName: '显示名称',
  kind: '档案类型',
  number: '学号 / 工号',
  role: '角色',
  className: '所属班级',
  classId: '班级 ID',
  professionId: '专业 ID',
  professionName: '所属专业',
  gender: '性别',
  phone: '手机号码',
  email: '电子邮箱',
  code: '代码',
  remark: '备注',
  host: '主机地址',
  port: '端口',
  provider: '资源平台',
  enabled: '允许资源分配',
  isSimulated: '模拟资源',
  success: '是否成功',
  message: '说明',
  status: '状态',
  health: '健康状态',
  cpuCount: 'CPU 核心数',
  cpuUsage: 'CPU 使用率',
  cpuUsagePercent: 'CPU 使用率 (%)',
  memoryMb: '内存 (MB)',
  memoryTotalMb: '总内存 (MB)',
  memoryUsedMb: '已用内存 (MB)',
  memoryUsage: '内存使用率',
  memoryUsagePercent: '内存使用率 (%)',
  diskGb: '磁盘 (GB)',
  virtualMachineCount: '虚拟机数量',
  accountCount: '账号数量',
  virtualMachineQuota: '虚拟机配额',
  accountQuota: '账号配额',
  nodeName: '节点名称',
  nodeId: '节点 ID',
  connectionType: '连接方式',
  cabinetName: '所属机柜',
  deviceTypeName: '设备类型',
  serialPortNumber: '串口编号',
  webPort: 'Web 端口',
  webProtocol: 'Web 协议',
  jumpHost: '跳板机地址',
  jumpPort: '跳板机端口',
  jumpUserName: '跳板机账号',
  hasJumpPassword: '已保存跳板机凭据',
  hasPassword: '已保存连接凭据',
  sessionActive: '终端会话',
  occupiedBy: '占用账号',
  operatingSystem: '操作系统',
  osDescription: '操作系统',
  os: '操作系统',
  machineName: '服务器名称',
  architecture: '系统架构',
  processorCount: '物理处理器数',
  uptime: '运行时间',
  uptimeSeconds: '运行时间 (秒)',
  processUptimeSeconds: '进程运行时间 (秒)',
  bootTime: '系统启动时间',
  timestamp: '采集时间',
  sampledAt: '采集时间',
  capturedAt: '采集时间',
  at: '时间',
  createdAt: '创建时间',
  updatedAt: '更新时间',
  cpu: 'CPU 信息',
  cpus: 'CPU 信息',
  memory: '内存信息',
  memoryModules: '内存条信息',
  network: '网络信息',
  networkInterfaces: '网卡信息',
  interfaces: '网卡信息',
  disks: '磁盘信息',
  model: '型号',
  manufacturer: '厂商',
  cores: '物理核心数',
  logicalProcessors: '逻辑核心数',
  clockSpeedMhz: '主频 (MHz)',
  speedMhz: '主频 (MHz)',
  capacityBytes: '容量 (字节)',
  totalBytes: '总容量 (字节)',
  availableBytes: '可用容量 (字节)',
  usedBytes: '已用容量 (字节)',
  sizeBytes: '容量 (字节)',
  macAddress: 'MAC 地址',
  ipv4: 'IPv4 地址',
  ipv6: 'IPv6 地址',
  subnetMask: '子网掩码',
  speed: '速率',
  speedMbps: '速率 (Mbps)',
  description: '说明',
  address: '地址',
  addresses: '地址列表',
  gateway: '网关',
  error: '错误信息',
  errors: '错误信息',
  items: '结果列表',
  total: '总数',
  running: '运行中',
  stopped: '已停止',
  releasedSessions: '已释放会话数',
  fileName: '文件名称',
  size: '大小',
  newLine: '换行符',
  newline: '换行符',
  backspace: '回退键',
  resources: '授权资源',
  expiresAt: '有效期至',
  issuedAt: '签发时间',
  licenseId: '许可证编号',
  machineCode: '设备特征码',
  valid: '是否有效',
  reason: '说明',
  schoolName: '学校名称',
  product: '产品',
  version: '版本',
  licensed: '授权状态',
  uploadedAt: '上传时间',
  subject: '授权对象',
  detail: '详情',
  action: '操作',
  target: '目标',
  type: '类型',
  state: '状态',
  usage: '使用情况',
  physicalMemory: '物理内存',
  totalMemoryBytes: '总内存 (字节)',
  availableMemoryBytes: '可用内存 (字节)',
  workingSetBytes: '进程内存 (字节)',
  processId: '进程 ID',
  runtime: '运行时',
  isWindows: 'Windows 系统',
  ipv4Addresses: 'IPv4 地址',
  ipv6Addresses: 'IPv6 地址',
  physicalCores: '物理核心数',
  logicalCores: '逻辑核心数',
  frequencyMhz: '频率 (MHz)',
  memoryType: '内存类型',
  bankLabel: '插槽',
  deviceLocator: '设备位置',
  capacity: '容量',
  speedBps: '网卡速率 (bps)',
  operationalStatus: '连接状态',
  mac: 'MAC 地址',
  dataDirectory: '数据目录',
  backupDirectory: '备份目录'
}
const entries = computed(() =>
  props.data && typeof props.data === 'object' && !Array.isArray(props.data)
    ? Object.entries(props.data).filter(
        ([key]) => !['password', 'passwordHash', 'securityStamp', 'jumpPassword', 'csrfToken'].includes(key)
      )
    : []
)
Object.assign(labels, {
  productAuthorizationCode: '产品授权码',
  assignedAt: '分配时间',
  userId: '账号 ID',
  personId: '师生档案 ID',
  accountUsage: '账号数量 / 配额',
  virtualMachineUsage: '虚拟机数量 / 配额',
  hostName: '主机名称',
  physicalCoreCount: '物理核心总数',
  logicalCoreCount: '逻辑核心总数',
  usagePercent: '使用率 (%)',
  processors: '处理器信息',
  coreCount: '物理核心数',
  modules: '内存条信息',
  slot: '插槽',
  networks: '网卡信息',
  gateways: '网关',
  dnsServers: 'DNS 服务器',
  family: '地址类型',
  prefixLength: '前缀长度',
  warnings: '采集提示',
  totalCpu: '节点 CPU 核心数',
  totalMemoryMb: '节点内存 (MB)',
  totalDiskGb: '节点磁盘 (GB)',
  diskUsagePercent: '磁盘使用率 (%)',
  runningCount: '运行中虚拟机数',
  healthStatus: '健康状态',
  metricsSource: '指标来源',
  collectedAt: '采集时间',
  publicKeyConfigured: '已配置供应商公钥',
  mode: '环境模式',
  importedAt: '导入时间',
  authorizationCode: '资源授权码',
  quantity: '授权数量',
  activeSessionCount: '活动会话数',
  deviceCount: '设备数量',
  ipAddress: 'IP 地址',
  systemTime: '系统时间',
  portNumber: '端口编号',
  released: '已释放会话数',
  level: '测试范围',
  legacy: '旧版许可证记录'
})
const isDateKey = (key: string) => key === 'at' || key === 'bootTime' || /(?:At|timestamp|systemTime)$/.test(key)
const label = (key: string) => {
  const text = labels[key] || key
  return isDateKey(key) ? `${text}（北京时间）` : text
}
const values: Record<string, string> = {
  simulated: '模拟环境',
  simulation: '模拟器',
  running: '运行中',
  stopped: '已停止',
  ready: '就绪',
  disabled: '已停用',
  healthy: '正常',
  Admin: '管理员',
  Teacher: '教师',
  Student: '学生',
  'simulator-resource-reservation': '模拟资源分配统计',
  unsupported: '未适配',
  teacher: '教师',
  student: '学生'
}
const scalar = (key: string, value: any) =>
  value === null || value === undefined
    ? (props.unavailableText ?? '—')
    : value === ''
      ? '—'
      : typeof value === 'boolean'
        ? value
          ? '是'
          : '否'
        : isDateKey(key)
          ? dateTime(value)
          : [
                'status',
                'state',
                'healthStatus',
                'role',
                'kind',
                'provider',
                'connectionType',
                'mode',
                'metricsSource',
                'level'
              ].includes(key)
            ? values[String(value)] || String(value)
            : String(value)
</script>
<template>
  <div v-if="data === null || data === undefined" class="empty-inline">暂无数据</div>
  <div v-else-if="Array.isArray(data)" class="data-array">
    <div v-if="!data.length" class="empty-inline">暂无记录</div>
    <div v-for="(item, index) in data" :key="index" class="data-array-item">
      <DataRecord v-if="typeof item === 'object'" :data="item" :unavailable-text="unavailableText" /><span v-else>{{
        item
      }}</span>
    </div>
  </div>
  <dl v-else-if="typeof data === 'object'" class="data-record">
    <div
      v-for="[key, value] in entries"
      :key="key"
      class="data-record-entry"
      :class="{ 'nested-entry': value !== null && typeof value === 'object' }"
    >
      <dt>{{ label(key) }}</dt>
      <dd>
        <DataRecord
          v-if="value !== null && typeof value === 'object'"
          :data="value"
          :unavailable-text="unavailableText"
        />
        <span v-else>{{ scalar(key, value) }}</span>
      </dd>
    </div>
  </dl>
  <span v-else>{{ data }}</span>
</template>

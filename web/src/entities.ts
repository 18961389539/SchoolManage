import type { Row } from './api'
export interface Field {
  key: string
  label: string
  type?: string
  required?: boolean
  requiredWhen?: { key: string; value: any }
  options?: { label: string; value: any; disabled?: boolean }[]
  source?: string
  placeholder?: string
  min?: number
  max?: number
  default?: any
  createOnly?: boolean
  help?: string
  visibleWhen?: { key: string; value: string }
}
export interface Column {
  key: string
  label: string
  width?: number
}
export interface Entity {
  title: string
  singular: string
  subtitle: string
  endpoint: string
  kind?: string
  fields: Field[]
  filters?: Field[]
  columns: Column[]
  icon: string
}
const connectionOptions = [
  { label: '内置模拟设备', value: 'simulated' },
  { label: 'SSH（暂未提供适配）', value: 'ssh', disabled: true },
  { label: 'Telnet（需真实设备联调）', value: 'telnet' },
  { label: 'TCP 串口流（需真实设备联调）', value: 'tcp' }
]
const gender = [
  { label: '未设置', value: '' },
  { label: '男', value: '男' },
  { label: '女', value: '女' }
]
const remark: Field = { key: 'remark', label: '备注', type: 'textarea' }
const profession: Field = {
  key: 'professionId',
  label: '所属专业',
  required: true,
  type: 'select',
  source: '/professions'
}
const schoolClass: Field = { key: 'classId', label: '所属班级', type: 'select', source: '/classes' }
const teacherRole: Field = {
  key: 'role',
  label: '所属角色',
  type: 'select',
  required: true,
  default: 'Teacher',
  options: [
    { label: '教师', value: 'Teacher' },
    { label: '管理员', value: 'Admin' }
  ]
}
const person = (kind: string): Entity => ({
  title: kind === 'teacher' ? '教师管理' : '学生管理',
  singular: kind === 'teacher' ? '教师' : '学生',
  subtitle: kind === 'teacher' ? '维护教师档案、平台账号与班级关联。' : '统一管理学生档案与账号，支持导入前数据校验。',
  endpoint: '/people',
  kind,
  icon: 'User',
  filters: kind === 'teacher' ? [schoolClass, teacherRole] : [schoolClass],
  columns: [
    { key: 'name', label: '姓名' },
    { key: 'userName', label: '平台账号' },
    { key: 'number', label: kind === 'teacher' ? '教师工号' : '学号' },
    { key: 'className', label: '所属班级' },
    ...(kind === 'teacher' ? [{ key: 'role', label: '所属角色' }] : []),
    { key: 'gender', label: '性别', width: 75 },
    { key: 'phone', label: '手机号码' },
    { key: 'email', label: '电子邮箱' }
  ],
  fields: [
    { key: 'name', label: '姓名', required: true },
    { key: 'userName', label: '平台账号', required: true, placeholder: '字母、数字或下划线' },
    { key: 'number', label: kind === 'teacher' ? '教师工号' : '学号', required: true },
    schoolClass,
    { key: 'gender', label: '性别', type: 'select', options: gender },
    { key: 'phone', label: '手机号码' },
    { key: 'email', label: '电子邮箱', type: 'email' },
    kind === 'teacher'
      ? teacherRole
      : { ...teacherRole, default: 'Student', options: [{ label: '学生', value: 'Student' }] },
    {
      key: 'password',
      label: '初始密码',
      type: 'password',
      required: true,
      createOnly: true,
      help: '12–128 位，同时包含字母和数字，请单独告知本人。'
    }
  ]
})
export const entities: Record<string, Entity> = {
  professions: {
    title: '专业管理',
    singular: '专业',
    subtitle: '维护专业目录，为班级和教学资源提供统一归属。',
    endpoint: '/professions',
    icon: 'Collection',
    columns: [
      { key: 'name', label: '专业名称' },
      { key: 'code', label: '专业代码' },
      { key: 'remark', label: '备注' }
    ],
    fields: [
      { key: 'name', label: '专业名称', required: true },
      { key: 'code', label: '专业代码', required: true },
      remark
    ]
  },
  classes: {
    title: '班级管理',
    singular: '班级',
    subtitle: '按专业组织班级，管理师生档案的班级关联。',
    endpoint: '/classes',
    icon: 'School',
    filters: [profession],
    columns: [
      { key: 'name', label: '班级名称' },
      { key: 'professionName', label: '所属专业' },
      { key: 'remark', label: '备注' }
    ],
    fields: [{ key: 'name', label: '班级名称', required: true }, profession, remark]
  },
  teachers: person('teacher'),
  students: person('student'),
  'device-types': {
    title: '设备类型',
    singular: '设备类型',
    subtitle: '配置设备分类和命令行输入兼容参数。',
    endpoint: '/device-types',
    icon: 'Grid',
    columns: [
      { key: 'name', label: '类型名称' },
      { key: 'newline', label: '换行符' },
      { key: 'backspace', label: '回退键' },
      { key: 'remark', label: '备注' }
    ],
    fields: [
      { key: 'name', label: '类型名称', required: true },
      {
        key: 'newline',
        label: '换行符',
        type: 'select',
        default: 'CRLF',
        options: [
          { label: 'CRLF', value: 'CRLF' },
          { label: 'CR', value: 'CR' },
          { label: 'LF', value: 'LF' }
        ]
      },
      {
        key: 'backspace',
        label: '回退键',
        type: 'select',
        default: 'DEL',
        options: [
          { label: 'BS (0x08)', value: 'BS' },
          { label: 'DEL (0x7F)', value: 'DEL' }
        ]
      },
      remark
    ]
  },
  cabinets: {
    title: '机柜管理',
    singular: '机柜',
    subtitle: '管理实训机柜、串口控制器与跳板机连接信息。',
    endpoint: '/cabinets',
    icon: 'OfficeBuilding',
    columns: [
      { key: 'name', label: '机柜名称' },
      { key: 'host', label: '控制器地址' },
      { key: 'port', label: '端口', width: 85 },
      { key: 'jumpHost', label: '跳板机地址' },
      { key: 'jumpUserName', label: '跳板机账号' },
      { key: 'remark', label: '备注' }
    ],
    fields: [
      { key: 'name', label: '机柜名称', required: true },
      { key: 'isSimulated', label: '模拟机柜', type: 'switch', default: true },
      {
        key: 'host',
        label: '控制器地址',
        required: true,
        requiredWhen: { key: 'isSimulated', value: false },
        default: 'simulator.local',
        placeholder: 'IP 地址或主机名',
        help: '模拟机柜可留空；真实机柜必须填写有效地址。'
      },
      { key: 'port', label: '控制器端口', type: 'number', min: 1, max: 65535, default: 23 },
      { key: 'jumpHost', label: '跳板机地址' },
      { key: 'jumpPort', label: '跳板机端口', type: 'number', default: 22, min: 1, max: 65535 },
      { key: 'jumpUserName', label: '跳板机账号' },
      { key: 'jumpPassword', label: '跳板机密码', type: 'password', help: '编辑时留空表示保留现有凭据。' },
      remark
    ]
  },
  devices: {
    title: '实训设备',
    singular: '设备',
    subtitle: '查看设备连接信息，打开终端并开展实训操作。',
    endpoint: '/devices',
    icon: 'Monitor',
    columns: [
      { key: 'name', label: '设备名称' },
      { key: 'cabinetName', label: '所属机柜' },
      { key: 'deviceTypeName', label: '设备类型' },
      { key: 'nodeName', label: '实训节点' },
      { key: 'connectionType', label: '连接方式', width: 130 },
      { key: 'host', label: '连接地址' },
      { key: 'port', label: '端口', width: 85 }
    ],
    fields: [
      { key: 'name', label: '设备名称', required: true },
      { key: 'cabinetId', label: '所属机柜', required: true, type: 'select', source: '/cabinets' },
      { key: 'deviceTypeId', label: '设备类型', required: true, type: 'select', source: '/device-types' },
      {
        key: 'connectionType',
        label: '连接方式',
        type: 'select',
        required: true,
        default: 'simulated',
        options: connectionOptions
      },
      {
        key: 'nodeId',
        label: '实训节点',
        type: 'select',
        source: '/nodes?provider=simulated',
        visibleWhen: { key: 'connectionType', value: 'simulated' },
        help: '关联模拟节点后，该节点已分配的师生可在个人工作空间访问本设备。'
      },
      { key: 'host', label: '连接地址', placeholder: '模拟设备使用内置连接服务' },
      { key: 'port', label: '连接端口', type: 'number', min: 1, max: 65535, default: 23 },
      { key: 'serialPortNumber', label: '串口编号', type: 'number', min: 1, default: 1 },
      {
        key: 'webProtocol',
        label: 'Web 协议',
        type: 'select',
        default: 'http',
        options: [
          { label: 'HTTP', value: 'http' },
          { label: 'HTTPS', value: 'https' }
        ]
      },
      { key: 'webPort', label: 'Web 端口', type: 'number', default: 80, min: 1, max: 65535 },
      { key: 'userName', label: '连接账号' },
      { key: 'password', label: '连接密码', type: 'password', help: '编辑时留空表示保留现有凭据。' },
      remark
    ]
  },
  nodes: {
    title: '集群节点',
    singular: '节点',
    subtitle: '维护节点连接信息，按适配器读取资源和健康状态。',
    endpoint: '/nodes',
    icon: 'Connection',
    columns: [
      { key: 'name', label: '节点名称' },
      { key: 'host', label: '节点地址' },
      { key: 'provider', label: '资源平台', width: 150 },
      { key: 'enabled', label: '启用状态', width: 105 },
      { key: 'healthStatus', label: '健康状态', width: 105 },
      { key: 'cpuUsagePercent', label: 'CPU 使用率', width: 120 },
      { key: 'memoryUsagePercent', label: '内存使用率', width: 120 },
      { key: 'diskUsagePercent', label: '磁盘使用率', width: 120 },
      { key: 'accountUsage', label: '账号数量 / 配额', width: 155 },
      { key: 'virtualMachineUsage', label: '虚拟机数量 / 配额', width: 170 },
      { key: 'remark', label: '备注' }
    ],
    fields: [
      { key: 'name', label: '节点名称', required: true },
      {
        key: 'provider',
        label: '资源平台',
        type: 'select',
        required: true,
        default: 'simulated',
        options: [
          { label: '内置模拟节点', value: 'simulated' },
          { label: 'Hyper-V', value: 'hyperv' },
          { label: 'Proxmox VE（暂未提供适配）', value: 'proxmox', disabled: true }
        ]
      },
      { key: 'host', label: '节点地址', required: true, default: 'simulator.local' },
      { key: 'totalCpu', label: '节点 CPU 核心数', type: 'number', default: 16, min: 1, max: 4096 },
      { key: 'totalMemoryMb', label: '节点内存 (MB)', type: 'number', default: 32768, min: 128, max: 16777216 },
      { key: 'totalDiskGb', label: '节点磁盘 (GB)', type: 'number', default: 512, min: 1, max: 1048576 },
      {
        key: 'accountQuota',
        label: '账号配额',
        type: 'number',
        required: true,
        default: 200,
        min: 0,
        max: 100000,
        help: '0 表示暂停新增；配额不能低于已分配账号数。'
      },
      {
        key: 'virtualMachineQuota',
        label: '虚拟机配额',
        type: 'number',
        required: true,
        default: 50,
        min: 0,
        max: 100000,
        help: '包括已停止的虚拟机，不能低于已创建数量。'
      },
      { key: 'port', label: '接口端口', type: 'number', default: 8006, min: 1, max: 65535 },
      { key: 'userName', label: '接口账号' },
      { key: 'password', label: '接口密码 / 令牌', type: 'password', help: '仅写入，列表不回传凭据。' },
      { key: 'enabled', label: '允许资源分配', type: 'switch', default: true },
      remark
    ]
  }
}
export const newEntity = (entity: Entity): Row => {
  const model: Row = {}
  for (const field of entity.fields)
    model[field.key] = field.default ?? (field.type === 'number' ? 0 : field.type === 'switch' ? false : '')
  if (entity.kind) model.kind = entity.kind
  return model
}

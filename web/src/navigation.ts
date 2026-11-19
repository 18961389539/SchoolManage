import type { Component } from 'vue'
import {
  Collection,
  Connection,
  Cpu,
  Document,
  FolderOpened,
  Grid,
  Key,
  Monitor,
  OfficeBuilding,
  School,
  Setting,
  User,
  UserFilled
} from '@element-plus/icons-vue'

export interface NavItem {
  key: string
  label: string
  icon: Component
}

export interface NavGroup {
  label: string
  items: NavItem[]
}

export const navigation: NavGroup[] = [
  { label: '工作空间', items: [{ key: 'dashboard', label: '控制台', icon: Grid }] },
  {
    label: '教学组织',
    items: [
      { key: 'professions', label: '专业管理', icon: Collection },
      { key: 'classes', label: '班级管理', icon: School },
      { key: 'teachers', label: '教师管理', icon: UserFilled },
      { key: 'students', label: '学生管理', icon: User }
    ]
  },
  {
    label: '实训资源',
    items: [
      { key: 'device-types', label: '设备类型', icon: Grid },
      { key: 'cabinets', label: '机柜管理', icon: OfficeBuilding },
      { key: 'devices', label: '实训设备', icon: Monitor },
      { key: 'nodes', label: '集群节点', icon: Connection },
      { key: 'virtual-machines', label: '虚拟机管理', icon: Cpu }
    ]
  },
  {
    label: '运行维护',
    items: [
      { key: 'server', label: '服务器信息', icon: Monitor },
      { key: 'audits', label: '操作日志', icon: Document },
      { key: 'backups', label: '备份与恢复', icon: FolderOpened },
      { key: 'licenses', label: '许可证管理', icon: Key },
      { key: 'settings', label: '系统设置', icon: Setting }
    ]
  }
]

export const navigationItems = navigation.flatMap(group => group.items)

export function findNavItem(key: string): NavItem | undefined {
  return navigationItems.find(item => item.key === key)
}

export function findNavGroup(key: string): NavGroup | undefined {
  return navigation.find(group => group.items.some(item => item.key === key))
}

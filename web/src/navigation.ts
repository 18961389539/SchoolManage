import type { Component } from 'vue'
import {
  Collection,
  Connection,
  Cpu,
  DataAnalysis,
  Document,
  EditPen,
  FolderOpened,
  Grid,
  HomeFilled,
  Key,
  Monitor,
  OfficeBuilding,
  Reading,
  School,
  Setting,
  Share,
  Suitcase,
  Tickets,
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

const teachingItems: NavItem[] = [
  { key: 'courses', label: '课程管理', icon: Collection },
  { key: 'exam-questions', label: '题库管理', icon: EditPen },
  { key: 'exam-papers', label: '试卷管理', icon: Document },
  { key: 'exams', label: '考试管理', icon: Tickets },
  { key: 'classrooms', label: '课堂管理', icon: School },
  { key: 'resources-overview', label: '资源概览', icon: Document }
]

export const navigation: NavGroup[] = [
  { label: '工作空间', items: [{ key: 'dashboard', label: '控制台', icon: Grid }] },
  { label: '教学中心', items: teachingItems },
  {
    label: '岗位训练营',
    items: [
      { key: 'camp-positions', label: '岗位管理', icon: Suitcase },
      { key: 'camp-reports', label: '能力报告', icon: DataAnalysis },
      { key: 'camp-records', label: '测评记录', icon: Reading },
      { key: 'camp-monitor', label: '测评监控', icon: Monitor }
    ]
  },
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

const teacherNavigation: NavGroup[] = [
  {
    label: '教学空间',
    items: [
      { key: 'teacher-home', label: '教学首页', icon: HomeFilled },
      { key: 'classrooms', label: '我的课堂', icon: School },
      { key: 'skill-graph', label: '技能图谱', icon: Share },
      { key: 'exams', label: '考试管理', icon: Tickets },
      { key: 'resources-overview', label: '资源概览', icon: Document },
      { key: 'my-resources', label: '我的实训资源', icon: Monitor }
    ]
  },
  {
    label: '岗位训练营',
    items: [
      { key: 'camp-reports', label: '能力报告', icon: DataAnalysis },
      { key: 'camp-records', label: '测评记录', icon: Reading },
      { key: 'camp-monitor', label: '测评监控', icon: Monitor }
    ]
  }
]

const studentNavigation: NavGroup[] = [
  {
    label: '学习空间',
    items: [
      { key: 'student-home', label: '学习首页', icon: HomeFilled },
      { key: 'my-exams', label: '我的考试', icon: Tickets },
      { key: 'exam-records', label: '考试动态', icon: Reading },
      { key: 'my-classrooms', label: '我的课堂', icon: School },
      { key: 'skill-graph', label: '技能图谱', icon: Share },
      { key: 'resources-overview', label: '资源概览', icon: Document },
      { key: 'my-camp', label: '岗位训练', icon: Suitcase },
      { key: 'camp-history', label: '测评历史', icon: DataAnalysis },
      { key: 'my-resources', label: '我的实训资源', icon: Monitor }
    ]
  }
]

export const navigationItems = navigation.flatMap(group => group.items)

export function navigationFor(role?: string): NavGroup[] {
  if (role === 'Teacher') return teacherNavigation
  if (role === 'Student') return studentNavigation
  return navigation
}

export function homeRoute(role?: string): string {
  if (role === 'Teacher') return 'teacher-home'
  if (role === 'Student') return 'student-home'
  return 'dashboard'
}

export function findNavItem(key: string, role = 'Admin'): NavItem | undefined {
  return navigationFor(role)
    .flatMap(group => group.items)
    .find(item => item.key === key)
}

export function findNavGroup(key: string, role = 'Admin'): NavGroup | undefined {
  return navigationFor(role).find(group => group.items.some(item => item.key === key))
}

/** Label lookup that ignores the signed-in role, for headers and routing notices. */
export function findAnyNavItem(key: string): NavItem | undefined {
  for (const groups of [navigation, teacherNavigation, studentNavigation])
    for (const group of groups) {
      const item = group.items.find(entry => entry.key === key)
      if (item) return item
    }
  return undefined
}

/** Group lookup that ignores the signed-in role, so a 403 keeps the other role's breadcrumb. */
export function findAnyNavGroup(key: string): NavGroup | undefined {
  for (const groups of [navigation, teacherNavigation, studentNavigation]) {
    const group = groups.find(entry => entry.items.some(item => item.key === key))
    if (group) return group
  }
  return undefined
}

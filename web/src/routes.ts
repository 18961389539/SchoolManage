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
import type { Row } from './api'
import { entities } from './entities'
import { lazyPage } from './lazyPage'

const EntityPage = lazyPage(() => import('./components/EntityPage.vue'))
const DashboardPage = lazyPage(() => import('./components/DashboardPage.vue'))
const OperationsPage = lazyPage(() => import('./components/OperationsPage.vue'))
const MyResourcesPage = lazyPage(() => import('./components/MyResourcesPage.vue'))
const CourseLibraryPage = lazyPage(() => import('./components/CourseLibraryPage.vue'))
const ClassroomCenterPage = lazyPage(() => import('./components/ClassroomCenterPage.vue'))
const MyClassroomsPage = lazyPage(() => import('./components/MyClassroomsPage.vue'))
const ClassroomContentPage = lazyPage(() => import('./components/ClassroomContentPage.vue'))
const StudentHomePage = lazyPage(() => import('./components/StudentHomePage.vue'))
const TeacherHomePage = lazyPage(() => import('./components/TeacherHomePage.vue'))
const SkillGraphPage = lazyPage(() => import('./components/SkillGraphPage.vue'))
const StudentClassroomStatisticsPage = lazyPage(() => import('./components/StudentClassroomStatisticsPage.vue'))
const ResourcesOverviewPage = lazyPage(() => import('./components/ResourcesOverviewPage.vue'))
const ExamQuestionBanksPage = lazyPage(() => import('./components/ExamQuestionBanksPage.vue'))
const PaperLibraryPage = lazyPage(() => import('./components/PaperLibraryPage.vue'))
const ExamCenterPage = lazyPage(() => import('./components/ExamCenterPage.vue'))
const MyExamsPage = lazyPage(() => import('./components/MyExamsPage.vue'))
const ExamTakingPage = lazyPage(() => import('./components/ExamTakingPage.vue'))
const ExamRecordsPage = lazyPage(() => import('./components/ExamRecordsPage.vue'))
const CoursePreviewPage = lazyPage(() => import('./components/CoursePreviewPage.vue'))
const CampPositionsPage = lazyPage(() => import('./components/CampPositionsPage.vue'))
const MyCampPage = lazyPage(() => import('./components/MyCampPage.vue'))
const CampTakingPage = lazyPage(() => import('./components/CampTakingPage.vue'))
const CampHistoryPage = lazyPage(() => import('./components/CampHistoryPage.vue'))
const CampReportsPage = lazyPage(() => import('./components/CampReportsPage.vue'))
const CampRecordsPage = lazyPage(() => import('./components/CampRecordsPage.vue'))
const CampMonitorPage = lazyPage(() => import('./components/CampMonitorPage.vue'))
const ProfilePage = lazyPage(() => import('./components/ProfilePage.vue'))

export type RouteRole = 'Admin' | 'Teacher' | 'Student'
export type RouteAccess = 'page' | 'missing' | 'forbidden'

export interface NavItem {
  key: string
  label: string
  icon: Component
}

export interface NavGroup {
  label: string
  items: NavItem[]
}

interface NavPlacement {
  group: string
  order: number
  label: string
  icon: Component
}

const nav = (group: string, order: number, label: string, icon: Component): NavPlacement => ({
  group,
  order,
  label,
  icon
})

/** Sidebar order of the groups themselves; items sort by their placement order inside a group. */
const navGroupOrder = ['工作空间', '学习空间', '教学空间', '教学中心', '岗位训练营', '教学组织', '实训资源', '运行维护']

function groupRank(label: string): number {
  const index = navGroupOrder.indexOf(label)
  return index < 0 ? navGroupOrder.length : index
}

/** Callbacks the shell hands to pages; kept out of the table so it stays a pure description. */
export interface RouteHost {
  navigate: (key: string, params?: Row) => unknown
  openPassword: () => void
  refreshClassrooms: () => void
}

export interface RouteContext {
  /** The key that was requested — entity tables and operations screens need it. */
  key: string
  params: Row
  role?: string
  admin: boolean
  version: string
}

export interface RouteView {
  component: Component
  props?: Record<string, unknown>
  on?: Record<string, (...args: any[]) => unknown>
  /** Remount when this value changes; leave undefined to keep the instance across param changes. */
  mountKey?: string
}

export interface RouteEntry {
  /** Keys this entry answers for; one shared component serves several entity or operations keys. */
  keys: string[]
  roles?: RouteRole[]
  /** Header and breadcrumb title for pages without a sidebar entry. */
  title?: string
  /** Sidebar entry to keep highlighted; defaults to the key itself. */
  navKey?: string | ((role?: string) => string)
  /** Breadcrumb group for pages that do not sit under a sidebar entry. */
  group?: string
  /**
   * Sidebar placement. One object applies to every role the entry allows; a per-role map is for
   * pages that sit in different groups or carry different labels per role.
   */
  nav?: NavPlacement | Partial<Record<RouteRole, NavPlacement>>
  view: (context: RouteContext) => RouteView
}

interface PageOptions {
  roles?: RouteRole[]
  title?: string
  navKey?: string | ((role?: string) => string)
  group?: string
  nav?: NavPlacement | Partial<Record<RouteRole, NavPlacement>>
  props?: (context: RouteContext) => Record<string, unknown>
  on?: RouteView['on']
  mountKey?: (context: RouteContext) => string
}

function page(key: string, component: Component, options: PageOptions = {}): RouteEntry {
  return {
    keys: [key],
    roles: options.roles,
    title: options.title,
    navKey: options.navKey,
    group: options.group,
    nav: options.nav,
    view: context => ({
      component,
      props: options.props?.(context),
      on: options.on,
      mountKey: options.mountKey?.(context)
    })
  }
}

/** Admin-only operational screens share one component and differ only by page key. */
export const operationsPageKeys = ['virtual-machines', 'server', 'audits', 'backups', 'licenses', 'settings']

const operationsNav: Record<string, NavPlacement> = {
  'virtual-machines': nav('实训资源', 5, '虚拟机管理', Cpu),
  server: nav('运行维护', 1, '服务器信息', Monitor),
  audits: nav('运行维护', 2, '操作日志', Document),
  backups: nav('运行维护', 3, '备份与恢复', FolderOpened),
  licenses: nav('运行维护', 4, '许可证管理', Key),
  settings: nav('运行维护', 5, '系统设置', Setting)
}

const entityNav: Record<string, NavPlacement> = {
  professions: nav('教学组织', 1, entities.professions.title, Collection),
  classes: nav('教学组织', 2, entities.classes.title, School),
  teachers: nav('教学组织', 3, entities.teachers.title, UserFilled),
  admins: nav('教学组织', 4, entities.admins.title, Key),
  students: nav('教学组织', 5, entities.students.title, User),
  'device-types': nav('实训资源', 1, entities['device-types'].title, Grid),
  cabinets: nav('实训资源', 2, entities.cabinets.title, OfficeBuilding),
  devices: nav('实训资源', 3, entities.devices.title, Monitor),
  nodes: nav('实训资源', 4, entities.nodes.title, Connection)
}

function allows(roles: RouteRole[] | undefined, role: string | undefined): boolean {
  return roles === undefined || (!!role && roles.includes(role as RouteRole))
}

function placementFor(entry: RouteEntry | undefined, role: string): NavPlacement | undefined {
  if (!entry?.nav) return undefined
  return 'group' in entry.nav ? entry.nav : entry.nav[role as RouteRole]
}

/** Falls back to another role's placement so headers and 403 notices keep a readable name. */
function anyPlacement(entry: RouteEntry | undefined): NavPlacement | undefined {
  if (!entry?.nav) return undefined
  if ('group' in entry.nav) return entry.nav
  return Object.values(entry.nav)[0]
}

/** The home page a role lands on when the address has no page key. */
export function homeRoute(role?: string): string {
  if (role === 'Teacher') return 'teacher-home'
  if (role === 'Student') return 'student-home'
  return 'dashboard'
}

/**
 * The single description of what a route key means. Adding a page is one entry here: its
 * component, who may open it, what it is called, which sidebar item lights up and where that
 * item sits. The sidebar is derived from these entries, never maintained separately.
 */
export function createRoutes(host: RouteHost) {
  const go = { navigate: host.navigate }
  const entries: RouteEntry[] = [
    page('dashboard', DashboardPage, {
      roles: ['Admin'],
      on: go,
      nav: nav('工作空间', 1, '控制台', Grid)
    }),
    page('student-home', StudentHomePage, {
      roles: ['Student'],
      on: go,
      nav: nav('学习空间', 1, '学习首页', HomeFilled)
    }),
    page('teacher-home', TeacherHomePage, {
      roles: ['Teacher'],
      on: go,
      nav: nav('教学空间', 1, '教学首页', HomeFilled)
    }),
    page('skill-graph', SkillGraphPage, {
      roles: ['Student', 'Teacher'],
      on: go,
      props: context => ({ role: context.role, initialStudentId: context.params.studentId }),
      nav: {
        Teacher: nav('教学空间', 7, '技能图谱', Share),
        Student: nav('学习空间', 6, '技能图谱', Share)
      }
    }),
    page('courses', CourseLibraryPage, {
      roles: ['Admin'],
      on: { ...go, 'activation-changed': host.refreshClassrooms },
      nav: nav('教学中心', 1, '课程管理', Collection)
    }),
    page('course-preview', CoursePreviewPage, {
      title: '课程预览',
      navKey: 'resources-overview',
      on: go,
      mountKey: context => context.params.courseId || '',
      props: context => ({
        courseId: context.params.courseId || '',
        backKey: context.params.backKey || 'resources-overview'
      })
    }),
    page('classrooms', ClassroomCenterPage, {
      roles: ['Admin', 'Teacher'],
      on: go,
      props: context => ({ admin: context.admin, initialStatisticsId: context.params.statistics }),
      nav: {
        Admin: nav('教学中心', 5, '课堂管理', School),
        Teacher: nav('教学空间', 2, '我的课堂', School)
      }
    }),
    page('my-classrooms', MyClassroomsPage, {
      roles: ['Student'],
      on: go,
      nav: nav('学习空间', 2, '我的课堂', School)
    }),
    page('classroom-content', ClassroomContentPage, {
      title: '课堂学习',
      navKey: role => (role === 'Student' ? 'my-classrooms' : 'classrooms'),
      on: go,
      mountKey: context => `${context.params.classroomId || ''}-${context.params.resourceId || ''}`,
      props: context => ({
        classroomId: context.params.classroomId || '',
        resourceId: context.params.resourceId,
        backKey: context.params.backKey || (context.role === 'Student' ? 'my-classrooms' : 'classrooms')
      })
    }),
    page('classroom-statistics', StudentClassroomStatisticsPage, {
      roles: ['Student'],
      title: '课堂统计',
      navKey: 'my-classrooms',
      on: go,
      mountKey: context => context.params.classroomId || '',
      props: context => ({
        classroomId: context.params.classroomId || '',
        backKey: context.params.backKey || 'my-classrooms'
      })
    }),
    page('exam-questions', ExamQuestionBanksPage, {
      roles: ['Admin', 'Teacher'],
      props: context => ({ readOnly: !context.admin }),
      nav: {
        Admin: nav('教学中心', 2, '题库管理', EditPen),
        Teacher: nav('教学空间', 6, '题库', EditPen)
      }
    }),
    page('exam-papers', PaperLibraryPage, {
      roles: ['Admin', 'Teacher'],
      props: context => ({ readOnly: !context.admin }),
      nav: {
        Admin: nav('教学中心', 3, '试卷管理', Document),
        Teacher: nav('教学空间', 5, '试卷库', Document)
      }
    }),
    page('exams', ExamCenterPage, {
      roles: ['Admin', 'Teacher'],
      on: go,
      props: context => ({ admin: context.admin }),
      nav: {
        Admin: nav('教学中心', 4, '考试管理', Tickets),
        Teacher: nav('教学空间', 4, '考试管理', Tickets)
      }
    }),
    page('my-exams', MyExamsPage, {
      roles: ['Student'],
      on: go,
      nav: nav('学习空间', 4, '我的考试', Tickets)
    }),
    page('exam-taking', ExamTakingPage, {
      roles: ['Student'],
      title: '在线考试',
      navKey: 'my-exams',
      on: go,
      mountKey: context => context.params.examId || '',
      props: context => ({ examId: context.params.examId || '' })
    }),
    page('exam-records', ExamRecordsPage, {
      roles: ['Student'],
      on: go,
      mountKey: context => context.params.search || '',
      props: context => ({ initialSearch: context.params.search }),
      nav: nav('学习空间', 5, '考试记录', Reading)
    }),
    page('resources-overview', ResourcesOverviewPage, {
      on: go,
      props: context => ({ role: context.role }),
      nav: {
        Admin: nav('教学中心', 6, '资源概览', Document),
        Teacher: nav('教学空间', 8, '资源概览', Document),
        Student: nav('学习空间', 7, '资源概览', Document)
      }
    }),
    page('camp-positions', CampPositionsPage, {
      roles: ['Admin'],
      nav: nav('岗位训练营', 1, '岗位管理', Suitcase)
    }),
    page('my-camp', MyCampPage, {
      roles: ['Student'],
      on: go,
      nav: nav('学习空间', 8, '岗位训练营', Suitcase)
    }),
    page('camp-taking', CampTakingPage, {
      roles: ['Student'],
      title: '岗位测评',
      navKey: 'my-camp',
      on: go,
      mountKey: context => context.params.positionId || '',
      props: context => ({ positionId: context.params.positionId || '' })
    }),
    page('camp-history', CampHistoryPage, {
      roles: ['Student'],
      on: go,
      nav: nav('学习空间', 9, '测评历史', DataAnalysis)
    }),
    page('camp-reports', CampReportsPage, {
      roles: ['Admin', 'Teacher'],
      nav: nav('岗位训练营', 2, '能力报告', DataAnalysis)
    }),
    page('camp-records', CampRecordsPage, {
      roles: ['Admin', 'Teacher'],
      nav: nav('岗位训练营', 3, '测评记录', Reading)
    }),
    page('camp-monitor', CampMonitorPage, {
      roles: ['Admin', 'Teacher'],
      nav: nav('岗位训练营', 4, '测评监控', Monitor)
    }),
    page('profile', ProfilePage, {
      title: '个人中心',
      group: '个人空间',
      on: { password: host.openPassword }
    }),
    page('my-resources', MyResourcesPage, {
      roles: ['Teacher', 'Student'],
      nav: {
        Teacher: nav('教学空间', 3, '我的实训资源', Monitor),
        Student: nav('学习空间', 3, '我的实训资源', Monitor)
      }
    }),
    ...Object.keys(entities).map(key =>
      page(key, EntityPage, {
        roles: ['Admin'],
        nav: entityNav[key],
        on: go,
        props: context => ({ entityKey: key, cabinetId: context.params.cabinetId })
      })
    ),
    ...operationsPageKeys.map(key =>
      page(key, OperationsPage, {
        roles: ['Admin'],
        nav: operationsNav[key],
        props: context => ({ pageKey: key, productVersion: context.version })
      })
    )
  ]

  const byKey = new Map<string, RouteEntry>()
  for (const entry of entries) for (const key of entry.keys) byKey.set(key, entry)

  function navKeyOf(key: string, role?: string): string {
    const navKey = byKey.get(key)?.navKey
    if (typeof navKey === 'function') return navKey(role)
    return navKey ?? key
  }

  return {
    entries,
    access(key: string, role?: string): RouteAccess {
      const entry = byKey.get(key)
      if (!entry) return 'missing'
      return allows(entry.roles, role) ? 'page' : 'forbidden'
    },
    /** Null when the key is unknown or the role may not open it. */
    view(key: string, context: RouteContext): RouteView | null {
      const entry = byKey.get(key)
      if (!entry || !allows(entry.roles, context.role)) return null
      return entry.view(context)
    },
    label(key: string, role?: string): string {
      const entry = byKey.get(key)
      if (entry?.title) return entry.title
      return placementFor(entry, role ?? 'Admin')?.label ?? anyPlacement(entry)?.label ?? '控制台'
    },
    navKey: navKeyOf,
    group(key: string, role?: string): string {
      const entry = byKey.get(key)
      if (entry?.group) return entry.group
      const navEntry = byKey.get(navKeyOf(key, role))
      return placementFor(navEntry, role ?? 'Admin')?.group ?? anyPlacement(navEntry)?.group ?? '工作空间'
    },
    /** The sidebar for a role, derived from the same entries that answer route lookups. */
    navigation(role?: string): NavGroup[] {
      const effectiveRole = role ?? 'Admin'
      const grouped = new Map<string, Array<NavItem & { order: number }>>()
      for (const entry of entries) {
        if (!allows(entry.roles, effectiveRole)) continue
        const placement = placementFor(entry, effectiveRole)
        if (!placement) continue
        const items = grouped.get(placement.group) ?? []
        for (const key of entry.keys)
          items.push({ key, label: placement.label, icon: placement.icon, order: placement.order })
        grouped.set(placement.group, items)
      }
      return [...grouped.entries()]
        .sort(([left], [right]) => groupRank(left) - groupRank(right))
        .map(([label, items]) => ({
          label,
          items: items
            .sort((left, right) => left.order - right.order)
            .map(item => ({ key: item.key, label: item.label, icon: item.icon }))
        }))
    }
  }
}

export type RouteTable = ReturnType<typeof createRoutes>

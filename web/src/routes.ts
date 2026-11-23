import type { Component } from 'vue'
import type { Row } from './api'
import { entities } from './entities'
import { findAnyNavGroup, findAnyNavItem, findNavGroup, findNavItem } from './navigation'
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
  view: (context: RouteContext) => RouteView
}

interface PageOptions {
  roles?: RouteRole[]
  title?: string
  navKey?: string | ((role?: string) => string)
  group?: string
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

function allows(roles: RouteRole[] | undefined, role: string | undefined): boolean {
  return roles === undefined || (!!role && roles.includes(role as RouteRole))
}

/**
 * The single description of what a route key means. Adding a page is one entry here: its
 * component, who may open it, what it is called, and which sidebar item lights up.
 */
export function createRoutes(host: RouteHost) {
  const go = { navigate: host.navigate }
  const entries: RouteEntry[] = [
    page('dashboard', DashboardPage, { roles: ['Admin'], on: go }),
    page('student-home', StudentHomePage, { roles: ['Student'], on: go }),
    page('teacher-home', TeacherHomePage, { roles: ['Teacher'], on: go }),
    page('skill-graph', SkillGraphPage, {
      roles: ['Student', 'Teacher'],
      on: go,
      props: context => ({ role: context.role, initialStudentId: context.params.studentId })
    }),
    page('courses', CourseLibraryPage, {
      roles: ['Admin'],
      on: { ...go, 'activation-changed': host.refreshClassrooms }
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
      props: context => ({ admin: context.admin, initialStatisticsId: context.params.statistics })
    }),
    page('my-classrooms', MyClassroomsPage, { roles: ['Student'], on: go }),
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
    page('exam-questions', ExamQuestionBanksPage, { roles: ['Admin'] }),
    page('exam-papers', PaperLibraryPage, { roles: ['Admin'] }),
    page('exams', ExamCenterPage, { roles: ['Admin', 'Teacher'], props: context => ({ admin: context.admin }) }),
    page('my-exams', MyExamsPage, { roles: ['Student'], on: go }),
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
      props: context => ({ initialSearch: context.params.search })
    }),
    page('resources-overview', ResourcesOverviewPage, { on: go, props: context => ({ role: context.role }) }),
    page('camp-positions', CampPositionsPage, { roles: ['Admin'] }),
    page('my-camp', MyCampPage, { roles: ['Student'], on: go }),
    page('camp-taking', CampTakingPage, {
      roles: ['Student'],
      title: '岗位测评',
      navKey: 'my-camp',
      on: go,
      mountKey: context => context.params.positionId || '',
      props: context => ({ positionId: context.params.positionId || '' })
    }),
    page('camp-history', CampHistoryPage, { roles: ['Student'], on: go }),
    page('camp-reports', CampReportsPage, { roles: ['Admin', 'Teacher'] }),
    page('camp-records', CampRecordsPage, { roles: ['Admin', 'Teacher'] }),
    page('camp-monitor', CampMonitorPage, { roles: ['Admin', 'Teacher'] }),
    page('profile', ProfilePage, {
      title: '个人中心',
      group: '个人空间',
      on: { password: host.openPassword }
    }),
    page('my-resources', MyResourcesPage, { roles: ['Teacher', 'Student'] }),
    {
      keys: Object.keys(entities),
      roles: ['Admin'],
      view: context => ({
        component: EntityPage,
        props: { entityKey: context.key, cabinetId: context.params.cabinetId },
        on: go
      })
    },
    {
      keys: operationsPageKeys,
      roles: ['Admin'],
      view: context => ({
        component: OperationsPage,
        props: { pageKey: context.key, productVersion: context.version }
      })
    }
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
      const title = byKey.get(key)?.title
      return title ?? findNavItem(key, role)?.label ?? findAnyNavItem(key)?.label ?? '控制台'
    },
    navKey: navKeyOf,
    group(key: string, role?: string): string {
      const group = byKey.get(key)?.group
      if (group) return group
      const navKey = navKeyOf(key, role)
      return findNavGroup(navKey, role)?.label ?? findAnyNavGroup(navKey)?.label ?? '工作空间'
    }
  }
}

export type RouteTable = ReturnType<typeof createRoutes>

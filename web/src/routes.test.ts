import { describe, expect, it, vi } from 'vitest'
import { entities } from './entities'
import { createRoutes, operationsPageKeys, type RouteContext } from './routes'

const host = { navigate: vi.fn(), openPassword: vi.fn(), refreshClassrooms: vi.fn() }
const routes = createRoutes(host)

function context(key: string, params: Record<string, string> = {}, role = 'Admin'): RouteContext {
  return { key, params, role, admin: role === 'Admin', version: '0.1.0' }
}

const roles = ['Admin', 'Teacher', 'Student'] as const

describe('route access', () => {
  it('opens a page for an allowed role', () => {
    expect(routes.access('dashboard', 'Admin')).toBe('page')
    expect(routes.access('exams', 'Teacher')).toBe('page')
    expect(routes.access('exam-taking', 'Student')).toBe('page')
  })

  it('reports forbidden instead of redirecting when the role is wrong', () => {
    expect(routes.access('dashboard', 'Student')).toBe('forbidden')
    expect(routes.access('my-exams', 'Admin')).toBe('forbidden')
    expect(routes.access('exams', 'Student')).toBe('forbidden')
  })

  it('treats unrestricted pages as open to every signed-in account', () => {
    for (const role of roles) {
      expect(routes.access('resources-overview', role)).toBe('page')
      expect(routes.access('classroom-content', role)).toBe('page')
      expect(routes.access('profile', role)).toBe('page')
    }
  })

  it('matches the API policy for pages only teachers and students own', () => {
    expect(routes.access('my-resources', 'Student')).toBe('page')
    expect(routes.access('my-resources', 'Teacher')).toBe('page')
    expect(routes.access('my-resources', 'Admin')).toBe('forbidden')
  })

  it('opens entity tables and operations screens for administrators only', () => {
    for (const key of [...Object.keys(entities), ...operationsPageKeys]) {
      expect(routes.access(key, 'Admin')).toBe('page')
      expect(routes.access(key, 'Teacher')).toBe('forbidden')
    }
  })

  it('calls an unknown address missing for every role', () => {
    for (const role of roles) {
      expect(routes.access('no-such-page', role)).toBe('missing')
      expect(routes.access('', role)).toBe('missing')
    }
  })

  it('gives every sidebar entry to a role that may open it', () => {
    for (const role of roles) {
      for (const group of routes.navigation(role)) {
        for (const item of group.items) {
          expect(routes.access(item.key, role), `${role} 的菜单项 ${item.key} 不可访问`).toBe('page')
        }
      }
    }
  })

  it('lists a page at most once per role', () => {
    for (const role of roles) {
      const seen = new Set<string>()
      for (const group of routes.navigation(role)) {
        for (const item of group.items) {
          expect(seen.has(item.key), `${role} 的菜单重复出现 ${item.key}`).toBe(false)
          seen.add(item.key)
        }
      }
    }
  })

  it('keeps the sidebar groups and items in their established order', () => {
    const menus = {
      Admin: {
        groups: ['工作空间', '教学中心', '岗位训练营', '教学组织', '实训资源', '运行维护'],
        keys: [
          'dashboard',
          'courses',
          'exam-questions',
          'exam-papers',
          'exams',
          'classrooms',
          'resources-overview',
          'camp-positions',
          'camp-reports',
          'camp-records',
          'camp-monitor',
          'professions',
          'classes',
          'teachers',
          'admins',
          'students',
          'device-types',
          'cabinets',
          'devices',
          'nodes',
          'virtual-machines',
          'server',
          'audits',
          'backups',
          'licenses',
          'settings'
        ]
      },
      Teacher: {
        groups: ['教学空间', '岗位训练营'],
        keys: [
          'teacher-home',
          'classrooms',
          'my-resources',
          'exams',
          'exam-papers',
          'exam-questions',
          'skill-graph',
          'resources-overview',
          'camp-reports',
          'camp-records',
          'camp-monitor'
        ]
      },
      Student: {
        groups: ['学习空间'],
        keys: [
          'student-home',
          'my-classrooms',
          'my-resources',
          'my-exams',
          'exam-records',
          'skill-graph',
          'resources-overview',
          'my-camp',
          'camp-history'
        ]
      }
    } as const
    for (const role of roles) {
      const navigation = routes.navigation(role)
      expect(
        navigation.map(group => group.label),
        `${role} 的分组顺序`
      ).toEqual(menus[role].groups)
      expect(
        navigation.flatMap(group => group.items.map(item => item.key)),
        `${role} 的菜单顺序`
      ).toEqual(menus[role].keys)
    }
  })

  it('lists every placed page for a role that may open it', () => {
    for (const role of roles) {
      const keys = new Set(routes.navigation(role).flatMap(group => group.items.map(item => item.key)))
      for (const entry of routes.entries) {
        if (!entry.nav || (entry.roles && !entry.roles.includes(role))) continue
        const placement = 'group' in entry.nav ? entry.nav : entry.nav[role]
        if (!placement) continue
        for (const key of entry.keys) expect(keys.has(key), `${role} 的菜单缺少已声明的页面 ${key}`).toBe(true)
      }
    }
  })
})

describe('route table', () => {
  it('answers each key exactly once', () => {
    const seen = new Set<string>()
    for (const entry of routes.entries) {
      for (const key of entry.keys) {
        expect(seen.has(key), `路由 ${key} 被定义了两次`).toBe(false)
        seen.add(key)
      }
    }
  })

  it('gives every entry a component that a role may reach', () => {
    for (const entry of routes.entries) {
      const role = entry.roles?.[0]
      for (const key of entry.keys) {
        expect(routes.view(key, context(key, {}, role))?.component, `${key} 缺少组件`).toBeTruthy()
        expect(routes.access(key, role)).toBe('page')
      }
    }
  })

  it('refuses to build a view the role may not open', () => {
    expect(routes.view('dashboard', context('dashboard', {}, 'Student'))).toBeNull()
    expect(routes.view('no-such-page', context('no-such-page'))).toBeNull()
  })
})

describe('route labels, groups and sidebar', () => {
  it('names a page the current role cannot see', () => {
    expect(routes.label('my-exams', 'Admin')).toBe('我的考试')
    expect(routes.label('students', 'Teacher')).toBe('学生管理')
  })

  it('keeps the titles of pages without a sidebar entry', () => {
    expect(routes.label('exam-taking', 'Student')).toBe('在线考试')
    expect(routes.label('profile', 'Admin')).toBe('个人中心')
  })

  it('reads sidebar labels and groups for entity pages', () => {
    expect(routes.label('devices', 'Admin')).toBe('实训设备')
    expect(routes.group('devices', 'Admin')).toBe('实训资源')
  })

  it('keeps sub-pages inside their parent group', () => {
    expect(routes.group('exam-taking', 'Student')).toBe('学习空间')
    expect(routes.group('course-preview', 'Admin')).toBe('教学中心')
    expect(routes.group('classroom-content', 'Student')).toBe('学习空间')
    expect(routes.group('classroom-content', 'Admin')).toBe('教学中心')
    expect(routes.group('profile', 'Admin')).toBe('个人空间')
  })

  it('highlights the entry the sub-page belongs to', () => {
    expect(routes.navKey('exam-taking', 'Student')).toBe('my-exams')
    expect(routes.navKey('course-preview', 'Admin')).toBe('resources-overview')
    expect(routes.navKey('classroom-content', 'Student')).toBe('my-classrooms')
    expect(routes.navKey('classroom-content', 'Admin')).toBe('classrooms')
    expect(routes.navKey('dashboard', 'Admin')).toBe('dashboard')
  })
})

describe('route views', () => {
  it('passes page parameters through', () => {
    const exam = routes.view('exam-taking', context('exam-taking', { examId: 'e1' }, 'Student'))
    expect(exam?.props).toMatchObject({ examId: 'e1' })
    expect(exam?.mountKey).toBe('e1')
  })

  it('fills the defaults a page relies on', () => {
    const preview = routes.view('course-preview', context('course-preview', { courseId: 'c1' }))
    expect(preview?.props).toMatchObject({ courseId: 'c1', backKey: 'resources-overview' })
    const content = routes.view('classroom-content', context('classroom-content', { classroomId: 'r1' }, 'Student'))
    expect(content?.props).toMatchObject({ backKey: 'my-classrooms' })
  })

  it('binds shared screens to the key that was requested', () => {
    const table = routes.view('students', context('students', { cabinetId: 'cab-1' }))
    expect(table?.props).toMatchObject({ entityKey: 'students', cabinetId: 'cab-1' })
    const operations = routes.view('audits', context('audits'))
    expect(operations?.props).toMatchObject({ pageKey: 'audits', productVersion: '0.1.0' })
  })

  it('wires the events the shell owns', () => {
    expect(routes.view('dashboard', context('dashboard'))?.on).toHaveProperty('navigate')
    expect(routes.view('courses', context('courses'))?.on).toHaveProperty('activation-changed')
    expect(routes.view('profile', context('profile'))?.on).toHaveProperty('password')
  })
})

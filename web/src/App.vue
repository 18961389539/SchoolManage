<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import { ElMessage } from 'element-plus'
import { api, messageOf, post, setLimits, setToken, type Row } from './api'
import { createAuthForm } from './auth'
import { entities } from './entities'
import { findNavGroup, findNavItem, homeRoute, navigationFor } from './navigation'
import { probeSession } from './session'
import type { SessionResponse, SessionUser, StatusResponse } from './types'
import AppSidebar from './components/AppSidebar.vue'
import AuthView from './components/AuthView.vue'
import ChangePasswordDialog from './components/ChangePasswordDialog.vue'
import HelpDialog from './components/HelpDialog.vue'
import WorkspaceHeader from './components/WorkspaceHeader.vue'

const EntityPage = defineAsyncComponent(() => import('./components/EntityPage.vue'))
const DashboardPage = defineAsyncComponent(() => import('./components/DashboardPage.vue'))
const OperationsPage = defineAsyncComponent(() => import('./components/OperationsPage.vue'))
const MyResourcesPage = defineAsyncComponent(() => import('./components/MyResourcesPage.vue'))
const CourseLibraryPage = defineAsyncComponent(() => import('./components/CourseLibraryPage.vue'))
const ClassroomCenterPage = defineAsyncComponent(() => import('./components/ClassroomCenterPage.vue'))
const MyClassroomsPage = defineAsyncComponent(() => import('./components/MyClassroomsPage.vue'))
const ClassroomContentPage = defineAsyncComponent(() => import('./components/ClassroomContentPage.vue'))
const StudentHomePage = defineAsyncComponent(() => import('./components/StudentHomePage.vue'))
const TeacherHomePage = defineAsyncComponent(() => import('./components/TeacherHomePage.vue'))
const SkillGraphPage = defineAsyncComponent(() => import('./components/SkillGraphPage.vue'))
const StudentClassroomStatisticsPage = defineAsyncComponent(
  () => import('./components/StudentClassroomStatisticsPage.vue')
)
const ResourcesOverviewPage = defineAsyncComponent(() => import('./components/ResourcesOverviewPage.vue'))
const ExamQuestionBanksPage = defineAsyncComponent(() => import('./components/ExamQuestionBanksPage.vue'))
const PaperLibraryPage = defineAsyncComponent(() => import('./components/PaperLibraryPage.vue'))
const ExamCenterPage = defineAsyncComponent(() => import('./components/ExamCenterPage.vue'))
const MyExamsPage = defineAsyncComponent(() => import('./components/MyExamsPage.vue'))
const ExamTakingPage = defineAsyncComponent(() => import('./components/ExamTakingPage.vue'))
const ExamRecordsPage = defineAsyncComponent(() => import('./components/ExamRecordsPage.vue'))
const CoursePreviewPage = defineAsyncComponent(() => import('./components/CoursePreviewPage.vue'))
const CampPositionsPage = defineAsyncComponent(() => import('./components/CampPositionsPage.vue'))
const MyCampPage = defineAsyncComponent(() => import('./components/MyCampPage.vue'))
const CampTakingPage = defineAsyncComponent(() => import('./components/CampTakingPage.vue'))
const CampHistoryPage = defineAsyncComponent(() => import('./components/CampHistoryPage.vue'))
const CampReportsPage = defineAsyncComponent(() => import('./components/CampReportsPage.vue'))
const CampRecordsPage = defineAsyncComponent(() => import('./components/CampRecordsPage.vue'))
const CampMonitorPage = defineAsyncComponent(() => import('./components/CampMonitorPage.vue'))
const ProfilePage = defineAsyncComponent(() => import('./components/ProfilePage.vue'))

const startup = ref(true)
const startupError = ref('')
const user = ref<SessionUser | null>(null)
const initialized = ref(true)
const busy = ref(false)
const authError = ref('')
const version = ref('')
const schoolName = ref('校内实训资源平台')
const route = ref('dashboard')
const routeParams = ref<Row>({})
const sidebarOpen = ref(false)
const mobileLayout = window.matchMedia('(max-width: 1000px)')
const helpVisible = ref(false)
const passwordVisible = ref(false)
const passwordBusy = ref(false)
const passwordError = ref('')
const initialCurrentPassword = ref('')
const authForm = reactive(createAuthForm())

const isAdmin = computed(() => user.value?.role === 'Admin')
const isTeacher = computed(() => user.value?.role === 'Teacher')
const isStudent = computed(() => user.value?.role === 'Student')
const classroomsEnabled = ref(false)
let classroomsRequest = 0
const roleNavGroups = computed(() => navigationFor(user.value?.role))
const navGroups = computed(() =>
  roleNavGroups.value
    .map(group => ({
      ...group,
      items: group.items.filter(item => classroomsEnabled.value || !['classrooms', 'my-classrooms'].includes(item.key))
    }))
    .filter(group => group.items.length)
)
const homeKey = computed(() => homeRoute(user.value?.role))
const sidebarActiveKey = computed(() => {
  if (route.value === 'classroom-content') return isStudent.value ? 'my-classrooms' : 'classrooms'
  if (route.value === 'classroom-statistics') return 'my-classrooms'
  if (route.value === 'exam-taking') return 'my-exams'
  if (route.value === 'course-preview') return 'resources-overview'
  if (route.value === 'camp-taking') return 'my-camp'
  return route.value
})
const availableRoutes = computed(
  () =>
    new Set([
      ...roleNavGroups.value.flatMap(group => group.items.map(item => item.key)),
      'classroom-content',
      'classroom-statistics',
      'exam-taking',
      'course-preview',
      'camp-taking',
      'profile'
    ])
)
const currentTitle = computed(() => {
  if (route.value === 'classroom-content') return '课堂学习'
  if (route.value === 'classroom-statistics') return '课堂统计'
  if (route.value === 'exam-taking') return '在线考试'
  if (route.value === 'course-preview') return '课程预览'
  if (route.value === 'camp-taking') return '岗位测评'
  if (route.value === 'profile') return '个人中心'
  return findNavItem(route.value, user.value?.role)?.label || '控制台'
})
function resolveGroup(key: string, role?: string): string {
  const classroomGroup = findNavGroup(role === 'Student' ? 'my-classrooms' : 'classrooms', role)?.label || '教学中心'
  const specialGroups: Record<string, string> = {
    profile: '个人空间',
    'classroom-content': classroomGroup,
    'classroom-statistics': classroomGroup,
    'exam-taking': findNavGroup('my-exams', role)?.label || '学习空间',
    'course-preview': findNavGroup('resources-overview', role)?.label || '教学中心',
    'camp-taking': findNavGroup('my-camp', role)?.label || '岗位训练营'
  }
  return specialGroups[key] || findNavGroup(key, role)?.label || '工作空间'
}
const currentGroup = computed(() => resolveGroup(route.value, user.value?.role))

function readRoute() {
  const wasOpen = sidebarOpen.value
  const [path, query] = location.hash.replace(/^#\/?/, '').split('?')
  route.value = availableRoutes.value.has(path || '') ? path! : homeKey.value
  routeParams.value = Object.fromEntries(new URLSearchParams(query || ''))
  sidebarOpen.value = false
  void nextTick(() => {
    window.scrollTo(0, 0)
    if (wasOpen) focusMain()
  })
}

function navigate(key: string, params?: Row) {
  const query = params ? new URLSearchParams(params).toString() : ''
  location.hash = `${key}${query ? '?' + query : ''}`
  if (route.value === key) readRoute()
}

function focusMain() {
  document.getElementById('main-content')?.focus()
}

function closeNavigation() {
  sidebarOpen.value = false
  void nextTick(() => document.getElementById('mobile-menu-toggle')?.focus())
}

function navigateFromSidebar(key: string) {
  const wasOpen = sidebarOpen.value
  sidebarOpen.value = false
  navigate(key)
  if (wasOpen) void nextTick(focusMain)
}

function openSidebarHelp() {
  sidebarOpen.value = false
  helpVisible.value = true
}

function resetDesktopNavigation(event: MediaQueryListEvent) {
  if (!event.matches && sidebarOpen.value) {
    sidebarOpen.value = false
    void nextTick(focusMain)
  }
}

function setSession(session: SessionResponse) {
  if (user.value?.id !== session.user.id) classroomsEnabled.value = false
  user.value = session.user
  setToken(session.csrfToken || '')
  passwordError.value = ''
  readRoute()
  if (user.value?.mustChangePassword) {
    initialCurrentPassword.value = authForm.password
    passwordVisible.value = true
  }
  if (!user.value?.mustChangePassword) void refreshClassrooms()
}

async function refreshClassrooms() {
  const request = ++classroomsRequest
  const currentUserId = user.value?.id
  if (!currentUserId) return
  try {
    const availability = await api<{ classrooms: boolean }>('/teaching/availability')
    if (request === classroomsRequest && user.value?.id === currentUserId)
      classroomsEnabled.value = availability.classrooms
  } catch {
    // The classroom feature remains reachable from existing records while the menu status is unknown.
  }
}

async function boot() {
  startup.value = true
  startupError.value = ''
  try {
    const status = await api<StatusResponse>('/status')
    initialized.value = status.initialized
    version.value = String(status.version || '未知')
    setLimits(status)
    if (status.schoolName) schoolName.value = status.schoolName
    if (!status.initialized) return
    const outcome = await probeSession(() => api<SessionResponse>('/auth/me'))
    if (outcome.kind === 'authenticated') setSession(outcome.session)
    else if (outcome.kind === 'unavailable') throw new Error(outcome.message)
  } catch (error) {
    startupError.value = messageOf(error)
  } finally {
    startup.value = false
  }
}

async function authenticate() {
  if (busy.value) return
  authError.value = ''
  busy.value = true
  try {
    const result = await post<SessionResponse>(
      initialized.value ? '/auth/login' : '/setup',
      initialized.value
        ? { userName: authForm.userName, password: authForm.password }
        : {
            schoolName: authForm.schoolName,
            userName: authForm.userName,
            displayName: authForm.displayName,
            password: authForm.password
          }
    )
    setSession(result)
    if (!initialized.value) schoolName.value = authForm.schoolName
    initialized.value = true
    authForm.password = ''
    authForm.confirmation = ''
    navigate(homeRoute(result.user.role))
  } catch (error) {
    authError.value = messageOf(error)
  } finally {
    busy.value = false
  }
}

async function logout() {
  try {
    await post('/auth/logout')
    user.value = null
    setToken('')
    passwordVisible.value = false
    initialCurrentPassword.value = ''
    authForm.password = ''
  } catch (error) {
    ElMessage.error(messageOf(error))
  }
}

function openPassword() {
  passwordError.value = ''
  initialCurrentPassword.value = ''
  passwordVisible.value = true
}

async function changePassword(payload: { currentPassword: string; newPassword: string }) {
  passwordError.value = ''
  passwordBusy.value = true
  try {
    const result = await post<SessionResponse>('/auth/password', payload)
    setSession(result?.user ? result : await api<SessionResponse>('/auth/me'))
    passwordVisible.value = false
    initialCurrentPassword.value = ''
    ElMessage.success('密码已修改')
  } catch (error) {
    passwordError.value = messageOf(error)
  } finally {
    passwordBusy.value = false
  }
}

const onExpired = () => {
  if (!user.value) return
  user.value = null
  setToken('')
  passwordVisible.value = false
  passwordError.value = ''
  initialCurrentPassword.value = ''
  authError.value = '登录状态已失效，请重新登录。'
}
const onSchoolChanged = (event: Event) => {
  schoolName.value = (event as CustomEvent).detail || schoolName.value
}
const onProfileUpdated = (event: Event) => {
  const detail = (event as CustomEvent).detail
  if (user.value && detail?.displayName) user.value.displayName = detail.displayName
}
function dropdown(command: string) {
  if (command === 'profile') navigate('profile')
  else if (command === 'password') openPassword()
  else if (command === 'logout') void logout()
  else helpVisible.value = true
}

const documentTitle = computed(() => {
  if (user.value) return currentTitle.value
  if (startup.value || startupError.value) return ''
  return initialized.value ? '登录' : '初始化实训平台'
})

watch(
  documentTitle,
  title => {
    document.title = title ? `${title} · 实训资源平台` : '实训资源平台'
  },
  { immediate: true }
)
onMounted(() => {
  readRoute()
  mobileLayout.addEventListener('change', resetDesktopNavigation)
  window.addEventListener('hashchange', readRoute)
  window.addEventListener('session-expired', onExpired)
  window.addEventListener('school-name-changed', onSchoolChanged)
  window.addEventListener('profile-updated', onProfileUpdated)
  void boot()
})
onBeforeUnmount(() => {
  mobileLayout.removeEventListener('change', resetDesktopNavigation)
  window.removeEventListener('hashchange', readRoute)
  window.removeEventListener('session-expired', onExpired)
  window.removeEventListener('school-name-changed', onSchoolChanged)
  window.removeEventListener('profile-updated', onProfileUpdated)
})
</script>

<template>
  <el-config-provider :locale="zhCn">
    <div v-if="startup" class="startup-screen">
      <div class="brand-mark large-mark"><span /><span /><span /></div>
      <h1>实训资源平台</h1>
      <p>正在连接本地服务…</p>
      <el-skeleton :rows="2" animated />
    </div>
    <div v-else-if="startupError" class="startup-screen">
      <el-result icon="error" title="平台服务暂时不可用" :sub-title="startupError">
        <template #extra>
          <el-button type="primary" @click="boot">重新连接</el-button>
          <p class="startup-hint">数据保存在本机服务中，恢复连接后可继续操作。</p>
        </template>
      </el-result>
    </div>
    <AuthView
      v-else-if="!user"
      :auth="authForm"
      :initialized="initialized"
      :school-name="schoolName"
      :version="version"
      :busy="busy"
      :error="authError"
      @submit="authenticate"
      @edit="authError = ''"
    />
    <div v-else class="app-layout">
      <a class="skip-link" href="#main-content" :inert="sidebarOpen" @click.prevent="focusMain">跳到主要内容</a>
      <AppSidebar
        :open="sidebarOpen"
        :groups="navGroups"
        :route="sidebarActiveKey"
        :home-key="homeKey"
        :school-name="schoolName"
        :version="version"
        @navigate="navigateFromSidebar"
        @close="closeNavigation"
        @help="openSidebarHelp"
      />
      <div class="main-frame" :inert="sidebarOpen">
        <WorkspaceHeader
          :group="currentGroup"
          :title="currentTitle"
          :user-name="user.userName"
          :display-name="user.displayName"
          :role="user.role"
          :menu-open="sidebarOpen"
          @menu="sidebarOpen = true"
          @command="dropdown"
        />
        <main id="main-content" class="main-content" tabindex="-1">
          <template v-if="!user.mustChangePassword">
            <DashboardPage v-if="route === 'dashboard' && isAdmin" @navigate="navigate" />
            <StudentHomePage v-else-if="route === 'student-home' && isStudent" @navigate="navigate" />
            <TeacherHomePage v-else-if="route === 'teacher-home' && isTeacher" @navigate="navigate" />
            <SkillGraphPage
              v-else-if="route === 'skill-graph' && (isStudent || isTeacher)"
              :role="user.role"
              :initial-student-id="routeParams.studentId"
              @navigate="navigate"
            />
            <CourseLibraryPage
              v-else-if="route === 'courses' && isAdmin"
              @navigate="navigate"
              @activation-changed="refreshClassrooms"
            />
            <CoursePreviewPage
              v-else-if="route === 'course-preview'"
              :key="routeParams.courseId || ''"
              :course-id="routeParams.courseId || ''"
              :back-key="routeParams.backKey || 'resources-overview'"
              @navigate="navigate"
            />
            <ClassroomCenterPage
              v-else-if="route === 'classrooms' && (isAdmin || isTeacher)"
              :admin="isAdmin"
              :initial-statistics-id="routeParams.statistics"
              @navigate="navigate"
            />
            <MyClassroomsPage v-else-if="route === 'my-classrooms' && isStudent" @navigate="navigate" />
            <ClassroomContentPage
              v-else-if="route === 'classroom-content'"
              :key="`${routeParams.classroomId || ''}-${routeParams.resourceId || ''}`"
              :classroom-id="routeParams.classroomId || ''"
              :resource-id="routeParams.resourceId"
              :back-key="routeParams.backKey || (isStudent ? 'my-classrooms' : 'classrooms')"
              @navigate="navigate"
            />
            <StudentClassroomStatisticsPage
              v-else-if="route === 'classroom-statistics' && isStudent"
              :key="routeParams.classroomId || ''"
              :classroom-id="routeParams.classroomId || ''"
              :back-key="routeParams.backKey || 'my-classrooms'"
              @navigate="navigate"
            />
            <ExamQuestionBanksPage v-else-if="route === 'exam-questions' && isAdmin" />
            <PaperLibraryPage v-else-if="route === 'exam-papers' && isAdmin" />
            <ExamCenterPage v-else-if="route === 'exams' && (isAdmin || isTeacher)" :admin="isAdmin" />
            <MyExamsPage v-else-if="route === 'my-exams' && isStudent" @navigate="navigate" />
            <ExamTakingPage
              v-else-if="route === 'exam-taking' && isStudent"
              :key="routeParams.examId || ''"
              :exam-id="routeParams.examId || ''"
              @navigate="navigate"
            />
            <ExamRecordsPage
              v-else-if="route === 'exam-records' && isStudent"
              :key="routeParams.search || ''"
              :initial-search="routeParams.search"
              @navigate="navigate"
            />
            <ResourcesOverviewPage v-else-if="route === 'resources-overview'" :role="user.role" @navigate="navigate" />
            <CampPositionsPage v-else-if="route === 'camp-positions' && isAdmin" />
            <MyCampPage v-else-if="route === 'my-camp' && isStudent" @navigate="navigate" />
            <CampTakingPage
              v-else-if="route === 'camp-taking' && isStudent"
              :key="routeParams.positionId || ''"
              :position-id="routeParams.positionId || ''"
              @navigate="navigate"
            />
            <CampHistoryPage v-else-if="route === 'camp-history' && isStudent" @navigate="navigate" />
            <CampReportsPage v-else-if="route === 'camp-reports' && (isAdmin || isTeacher)" />
            <CampRecordsPage v-else-if="route === 'camp-records' && (isAdmin || isTeacher)" />
            <CampMonitorPage v-else-if="route === 'camp-monitor' && (isAdmin || isTeacher)" />
            <ProfilePage v-else-if="route === 'profile'" @password="openPassword" />
            <MyResourcesPage v-else-if="route === 'my-resources'" />
            <EntityPage
              v-else-if="isAdmin && entities[route]"
              :entity-key="route"
              :cabinet-id="routeParams.cabinetId"
              @navigate="navigate"
            />
            <OperationsPage v-else-if="isAdmin" :page-key="route" :product-version="version" />
            <el-empty v-else description="页面不存在或无权访问" />
          </template>
          <el-alert v-else title="请先修改初始密码，再访问平台功能。" type="info" show-icon :closable="false" />
        </main>
      </div>
    </div>
    <ChangePasswordDialog
      v-model="passwordVisible"
      :must-change="!!user?.mustChangePassword"
      :busy="passwordBusy"
      :error="passwordError"
      :initial-current-password="initialCurrentPassword"
      @submit="changePassword"
      @logout="logout"
    />
    <HelpDialog v-model="helpVisible" :role="user?.role || 'Student'" />
  </el-config-provider>
</template>

<style scoped>
.startup-hint {
  font-size: 12px;
  color: #56697c;
  margin: 14px 0 0;
}
</style>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import { ElMessage } from 'element-plus'
import { api, messageOf, post, type Row } from './api'
import { createAuthForm } from './auth'
import { canLeave, hasLeaveGuard } from './leaveGuard'
import { cancelPageReads } from './requestScope'
import { createRoutes, homeRoute } from './routes'
import { probeSession } from './session'
import { appState, applyServerInfo, applySession, signOut } from './store'
import type { SessionResponse, StatusResponse } from './types'
import AppSidebar from './components/AppSidebar.vue'
import AuthView from './components/AuthView.vue'
import ChangePasswordDialog from './components/ChangePasswordDialog.vue'
import HelpDialog from './components/HelpDialog.vue'
import RouteNotice from './components/RouteNotice.vue'
import WorkspaceHeader from './components/WorkspaceHeader.vue'

const startup = ref(true)
const startupError = ref('')
const user = computed(() => appState.user)
const initialized = ref(true)
const demoAccounts = ref<StatusResponse['demoAccounts']>(null)
const busy = ref(false)
const authError = ref('')
const authNotice = computed(() => authError.value || appState.authNotice)
const version = computed(() => appState.version)
const schoolName = computed(() => appState.schoolName)
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
const classroomsEnabled = ref(false)
let classroomsRequest = 0
const roleNavGroups = computed(() => routes.navigation(user.value?.role))
const navGroups = computed(() =>
  roleNavGroups.value
    .map(group => ({
      ...group,
      items: group.items.filter(item => classroomsEnabled.value || !['classrooms', 'my-classrooms'].includes(item.key))
    }))
    .filter(group => group.items.length)
)
const homeKey = computed(() => homeRoute(user.value?.role))
/**
 * Every page is described once in routes.ts — component, roles, title, sidebar entry, group.
 * Adding one is a single entry there, not a branch in this file.
 */
const routes = createRoutes({ navigate, openPassword, refreshClassrooms })
/**
 * A key that is not a page at all is a 404; a page this role cannot open is a 403. Neither
 * falls back to the home page, because a silent redirect leaves the operator guessing.
 */
const access = computed(() => routes.access(route.value, user.value?.role))
const noticeKind = computed(() => (access.value === 'forbidden' ? 'forbidden' : 'missing'))
const view = computed(() =>
  routes.view(route.value, {
    key: route.value,
    params: routeParams.value,
    role: user.value?.role,
    admin: isAdmin.value,
    version: version.value
  })
)
const routeLabel = computed(() => routes.label(route.value, user.value?.role))
const currentTitle = computed(() => {
  if (access.value === 'missing') return '页面不存在'
  if (access.value === 'forbidden') return '无权访问'
  return routeLabel.value
})
const sidebarActiveKey = computed(() => routes.navKey(route.value, user.value?.role))
const currentGroup = computed(() => routes.group(route.value, user.value?.role))

let committedHash = location.hash
let approvedHash = ''

function readRoute() {
  const wasOpen = sidebarOpen.value
  const [path, query] = location.hash.replace(/^#\/?/, '').split('?')
  route.value = path || homeKey.value
  routeParams.value = Object.fromEntries(new URLSearchParams(query || ''))
  sidebarOpen.value = false
  void nextTick(() => {
    window.scrollTo(0, 0)
    if (wasOpen) focusMain()
  })
}

/**
 * Every in-app route change runs through here so a page holding unsaved work
 * can refuse. Setting the hash fires `hashchange` asynchronously; remembering
 * the approved hash keeps the guard from asking twice for one click.
 */
async function navigate(key: string, params?: Row) {
  const query = params ? new URLSearchParams(params).toString() : ''
  const target = `${key}${query ? '?' + query : ''}`
  if (location.hash.replace(/^#\/?/, '') === target) {
    readRoute()
    return true
  }
  if (hasLeaveGuard() && !(await canLeave())) return false
  approvedHash = `#${target}`
  location.hash = target
  return true
}

async function onHashChange() {
  const target = location.hash
  if (target === committedHash) return
  const approved = approvedHash === target
  approvedHash = ''
  if (!approved && hasLeaveGuard() && !(await canLeave())) {
    if (location.hash !== committedHash)
      history.replaceState(null, '', committedHash || location.pathname + location.search)
    return
  }
  committedHash = target
  // Only a real page change abandons the previous page's reads. Re-entering the same page
  // (sign-in lands on the hash it already rendered) must not cancel the load that just began.
  const nextKey = target.replace(/^#\/?/, '').split('?')[0] || homeKey.value
  if (nextKey !== route.value) cancelPageReads()
  readRoute()
}

function focusMain() {
  document.getElementById('main-content')?.focus()
}

function closeNavigation() {
  sidebarOpen.value = false
  void nextTick(() => document.getElementById('mobile-menu-toggle')?.focus())
}

async function navigateFromSidebar(key: string) {
  const wasOpen = sidebarOpen.value
  sidebarOpen.value = false
  const left = await navigate(key)
  if (wasOpen && left) void nextTick(focusMain)
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
  if (appState.user?.id !== session.user.id) classroomsEnabled.value = false
  applySession(session)
  passwordError.value = ''
  readRoute()
  if (session.user.mustChangePassword) {
    initialCurrentPassword.value = authForm.password
    passwordVisible.value = true
  } else void refreshClassrooms()
}

async function refreshClassrooms() {
  const request = ++classroomsRequest
  const currentUserId = user.value?.id
  if (!currentUserId) return
  try {
    const availability = await api<{ classrooms: boolean }>('/teaching/availability', { pageScoped: false })
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
    const status = await api<StatusResponse>('/status', { pageScoped: false })
    initialized.value = status.initialized
    demoAccounts.value = status.demo ? (status.demoAccounts ?? null) : null
    applyServerInfo(status)
    if (!status.initialized) return
    const outcome = await probeSession(() => api<SessionResponse>('/auth/me', { pageScoped: false }))
    if (outcome.kind === 'authenticated') setSession(outcome.session)
    else if (outcome.kind === 'unavailable') throw new Error(outcome.message)
  } catch (error) {
    startupError.value = messageOf(error)
  } finally {
    startup.value = false
  }
}

function clearAuthNotice() {
  authError.value = ''
  appState.authNotice = ''
}

async function authenticate() {
  if (busy.value) return
  clearAuthNotice()
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
    if (!initialized.value) appState.schoolName = authForm.schoolName
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
  if (hasLeaveGuard() && !(await canLeave())) return
  try {
    await post('/auth/logout')
    signOut()
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
    setSession(result?.user ? result : await api<SessionResponse>('/auth/me', { pageScoped: false }))
    passwordVisible.value = false
    initialCurrentPassword.value = ''
    ElMessage.success('密码已修改')
  } catch (error) {
    passwordError.value = messageOf(error)
  } finally {
    passwordBusy.value = false
  }
}

/** The shell reacts to the store, not to window events a page had to remember to dispatch. */
watch(
  () => appState.user,
  user => {
    if (user) return
    passwordVisible.value = false
    passwordError.value = ''
    initialCurrentPassword.value = ''
  }
)
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
  window.addEventListener('hashchange', onHashChange)
  void boot()
})
onBeforeUnmount(() => {
  mobileLayout.removeEventListener('change', resetDesktopNavigation)
  window.removeEventListener('hashchange', onHashChange)
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
      :error="authNotice"
      :demo-accounts="demoAccounts"
      @submit="authenticate"
      @edit="clearAuthNotice"
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
            <component :is="view.component" v-if="view" :key="view.mountKey" v-bind="view.props" v-on="view.on" />
            <RouteNotice v-else :kind="noticeKind" :label="routeLabel" :requested="route" @home="navigate(homeKey)" />
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

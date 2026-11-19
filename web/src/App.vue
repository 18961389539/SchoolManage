<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import { ElMessage } from 'element-plus'
import { User } from '@element-plus/icons-vue'
import { api, messageOf, passwordIssue, post, setToken, type Row } from './api'
import { createAuthForm } from './auth'
import { entities } from './entities'
import { navigation, navigationItems } from './navigation'
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
const helpVisible = ref(false)
const passwordVisible = ref(false)
const passwordBusy = ref(false)
const passwordError = ref('')
const initialCurrentPassword = ref('')
const authForm = reactive(createAuthForm())

const currentTitle = computed(() => navigationItems.find(item => item.key === route.value)?.label || '控制台')
const currentGroup = computed(
  () => navigation.find(group => group.items.some(item => item.key === route.value))?.label || '工作空间'
)
const isAdmin = computed(() => user.value?.role === 'Admin')

function readRoute() {
  const [path, query] = location.hash.replace(/^#\/?/, '').split('?')
  route.value = navigationItems.some(item => item.key === path) ? path! : 'dashboard'
  routeParams.value = Object.fromEntries(new URLSearchParams(query || ''))
  sidebarOpen.value = false
  void nextTick(() => window.scrollTo(0, 0))
}

function navigate(key: string, params?: Row) {
  const query = params ? new URLSearchParams(params).toString() : ''
  location.hash = `${key}${query ? '?' + query : ''}`
  if (route.value === key) readRoute()
}

function setSession(session: SessionResponse) {
  user.value = session.user
  setToken(session.csrfToken || '')
  if (user.value?.mustChangePassword) {
    initialCurrentPassword.value = authForm.password
    passwordVisible.value = true
  }
}

async function boot() {
  startup.value = true
  startupError.value = ''
  try {
    const status = await api<StatusResponse>('/status')
    initialized.value = status.initialized
    version.value = String(status.version || '未知')
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
  authError.value = ''
  if (!authForm.userName.trim() || !authForm.password) {
    authError.value = '请填写账号和密码'
    return
  }
  if (!initialized.value) {
    if (!authForm.schoolName.trim() || !authForm.displayName.trim()) {
      authError.value = '请填写学校名称和管理员姓名'
      return
    }
    const issue = passwordIssue(authForm.password)
    if (issue) {
      authError.value = issue
      return
    }
    if (authForm.password !== authForm.confirmation) {
      authError.value = '两次输入的密码不一致'
      return
    }
  }
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
    navigate('dashboard')
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
  authError.value = '登录状态已失效，请重新登录。'
}
const onSchoolChanged = (event: Event) => {
  schoolName.value = (event as CustomEvent).detail || schoolName.value
}
function dropdown(command: string) {
  if (command === 'password') openPassword()
  else if (command === 'logout') void logout()
  else helpVisible.value = true
}

watch(
  currentTitle,
  title => {
    document.title = `${title} · 实训资源平台`
  },
  { immediate: true }
)
onMounted(() => {
  readRoute()
  window.addEventListener('hashchange', readRoute)
  window.addEventListener('session-expired', onExpired)
  window.addEventListener('school-name-changed', onSchoolChanged)
  void boot()
})
onBeforeUnmount(() => {
  window.removeEventListener('hashchange', readRoute)
  window.removeEventListener('session-expired', onExpired)
  window.removeEventListener('school-name-changed', onSchoolChanged)
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
      v-model:auth="authForm"
      :initialized="initialized"
      :version="version"
      :busy="busy"
      :error="authError"
      @submit="authenticate"
    />
    <div v-else class="app-layout">
      <AppSidebar
        v-model:open="sidebarOpen"
        :groups="navigation"
        :route="route"
        :is-admin="isAdmin"
        :school-name="schoolName"
        :version="version"
        @navigate="navigate"
        @help="helpVisible = true"
      />
      <div class="main-frame">
        <WorkspaceHeader
          :group="isAdmin ? currentGroup : '个人工作空间'"
          :title="isAdmin ? currentTitle : '个人首页'"
          :user-name="user.userName"
          :display-name="user.displayName"
          @menu="sidebarOpen = true"
          @command="dropdown"
        />
        <main class="main-content" id="main-content">
          <template v-if="isAdmin">
            <DashboardPage v-if="route === 'dashboard'" @navigate="navigate" />
            <EntityPage
              v-else-if="entities[route]"
              :entity-key="route"
              :cabinet-id="routeParams.cabinetId"
              @navigate="navigate"
            />
            <OperationsPage v-else :page-key="route" :product-version="version" />
          </template>
          <template v-else>
            <section class="page-heading">
              <div>
                <div class="eyebrow">个人工作空间</div>
                <h1>你好，{{ user.displayName || user.userName }}</h1>
                <p>欢迎登录校内实训资源平台。</p>
              </div>
              <el-button @click="openPassword">修改密码</el-button>
            </section>
            <section class="surface personal-surface">
              <div class="personal-icon">
                <el-icon :size="32"><User /></el-icon>
              </div>
              <h2>账号已连接</h2>
              <p>当前角色：{{ user.role === 'Teacher' ? '教师' : user.role === 'Student' ? '学生' : user.role }}</p>
              <p>当前账号暂无管理资源权限。需要访问实训资源时，请联系平台管理员确认授权范围。</p>
            </section>
          </template>
          <footer class="workspace-footer">
            <span>实训资源平台</span><span>Windows 本地服务 · v{{ version }}</span>
          </footer>
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
    <HelpDialog v-model="helpVisible" />
  </el-config-provider>
</template>

<style scoped>
.startup-hint {
  font-size: 12px;
  color: #93a0ae;
  margin: 14px 0 0;
}
</style>

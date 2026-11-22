<script setup lang="ts">
import { computed, nextTick, reactive, ref, watch } from 'vue'
import { ArrowRight, WarningFilled } from '@element-plus/icons-vue'
import { passwordIssue } from '../api'
import type { AuthForm } from '../auth'

const auth = defineModel<AuthForm>('auth', { required: true })
const props = defineProps<{ initialized: boolean; schoolName: string; version: string; busy: boolean; error: string }>()
const emit = defineEmits<{ submit: []; edit: [] }>()

const form = ref<HTMLFormElement | null>(null)
const serverError = ref<HTMLElement | null>(null)
const attempted = ref(false)
const reveal = reactive({ password: false, confirmation: false })

const identityFields = [
  {
    key: 'schoolName',
    id: 'auth-school',
    label: '学校名称',
    autocomplete: 'organization',
    max: 100,
    placeholder: '例如：信息工程学院'
  },
  { key: 'displayName', id: 'auth-name', label: '管理员姓名', autocomplete: 'name', max: 50 }
] as const
const userField = {
  key: 'userName',
  id: 'auth-username',
  label: '平台账号',
  autocomplete: 'username',
  max: 80,
  placeholder: '请输入平台账号'
} as const
const accountFields = computed(() => (props.initialized ? [userField] : [...identityFields, userField]))
const secretFields = computed(() => {
  const password = {
    key: 'password',
    id: 'auth-password',
    label: props.initialized ? '登录密码' : '管理员密码',
    hint: '密码',
    placeholder: props.initialized ? '请输入登录密码' : '请输入管理员密码'
  } as const
  const confirmation = {
    key: 'confirmation',
    id: 'auth-confirmation',
    label: '确认密码',
    hint: '确认密码',
    placeholder: '再次输入管理员密码'
  } as const
  return props.initialized ? [password] : [password, confirmation]
})

const issues = computed<Record<keyof AuthForm, string>>(() => ({
  schoolName: !props.initialized && !auth.value.schoolName.trim() ? '请输入学校名称' : '',
  displayName: !props.initialized && !auth.value.displayName.trim() ? '请输入管理员姓名' : '',
  userName: auth.value.userName.trim() ? '' : '请输入平台账号',
  password: passwordMessage(),
  confirmation: confirmationMessage()
}))

function passwordMessage() {
  if (!auth.value.password) return '请输入密码'
  return props.initialized ? '' : passwordIssue(auth.value.password)
}

function confirmationMessage() {
  if (props.initialized) return ''
  if (!auth.value.confirmation) return '请再次输入管理员密码'
  return auth.value.password === auth.value.confirmation ? '' : '两次输入的密码不一致'
}

function fieldError(field: keyof AuthForm) {
  return attempted.value ? issues.value[field] : ''
}

function descriptionFor(field: keyof AuthForm, id: string) {
  if (fieldError(field)) return id + '-error'
  if (!props.initialized && field === 'password') return 'auth-password-rule'
}

async function submit() {
  if (props.busy) return
  if (props.error) emit('edit')
  attempted.value = true
  if (Object.values(issues.value).some(Boolean)) {
    await nextTick()
    form.value?.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus()
    return
  }
  reveal.password = false
  reveal.confirmation = false
  emit('submit')
}

watch(
  () => props.error,
  async error => {
    if (!error) return
    await nextTick()
    serverError.value?.focus()
  }
)
</script>

<template>
  <main class="auth-layout">
    <div class="auth-card" :class="{ 'auth-setup': !initialized }">
      <header class="auth-brand-panel">
        <div class="auth-brand">
          <span class="brand-mark" aria-hidden="true"><span /><span /><span /></span>
          <strong>实训资源平台</strong>
        </div>
        <p class="auth-campus">{{ initialized && schoolName ? schoolName : '校内教学与实训管理' }}</p>
        <small v-if="version" class="auth-version">v{{ version }}</small>
      </header>

      <section class="auth-form-wrap" aria-labelledby="auth-title">
        <h1 id="auth-title">{{ initialized ? '登录工作空间' : '初始化实训平台' }}</h1>
        <p v-if="!initialized" class="auth-intro">请在部署服务器本机创建首个管理员账号。</p>

        <div v-if="error" ref="serverError" class="auth-server-error" role="alert" tabindex="-1">
          <el-icon aria-hidden="true"><WarningFilled /></el-icon>
          <span>{{ error }}</span>
        </div>

        <form ref="form" novalidate :aria-busy="busy" @submit.prevent="submit" @input="error && emit('edit')">
          <fieldset class="auth-fields" :disabled="busy">
            <div
              v-for="field in accountFields"
              :key="field.key"
              class="auth-field"
              :class="{ 'auth-identity-field': field.key !== 'userName' }"
            >
              <label :for="field.id">{{ field.label }}</label>
              <input
                :id="field.id"
                v-model="auth[field.key]"
                class="auth-control"
                :name="field.autocomplete"
                :autocomplete="field.autocomplete"
                :autocapitalize="field.key === 'userName' ? 'none' : undefined"
                :spellcheck="field.key !== 'userName'"
                :maxlength="field.max"
                :placeholder="'placeholder' in field ? field.placeholder : undefined"
                :aria-invalid="!!fieldError(field.key) || undefined"
                :aria-describedby="descriptionFor(field.key, field.id)"
                required
              />
              <p v-if="fieldError(field.key)" :id="field.id + '-error'" class="auth-error">
                {{ fieldError(field.key) }}
              </p>
            </div>

            <div v-for="field in secretFields" :key="field.key" class="auth-field">
              <label :for="field.id">{{ field.label }}</label>
              <div class="auth-control auth-password" :class="{ 'has-error': fieldError(field.key) }">
                <input
                  :id="field.id"
                  v-model="auth[field.key]"
                  :name="field.key === 'password' ? 'password' : 'passwordConfirmation'"
                  :type="reveal[field.key] ? 'text' : 'password'"
                  :autocomplete="initialized ? 'current-password' : 'new-password'"
                  autocapitalize="none"
                  :spellcheck="false"
                  maxlength="128"
                  :placeholder="field.placeholder"
                  :aria-invalid="!!fieldError(field.key) || undefined"
                  :aria-describedby="descriptionFor(field.key, field.id)"
                  required
                />
                <button
                  type="button"
                  class="auth-reveal"
                  :aria-controls="field.id"
                  :aria-label="(reveal[field.key] ? '隐藏' : '显示') + field.hint"
                  @click="reveal[field.key] = !reveal[field.key]"
                >
                  {{ reveal[field.key] ? '隐藏' : '显示' }}
                </button>
              </div>
              <p v-if="fieldError(field.key)" :id="field.id + '-error'" class="auth-error">
                {{ fieldError(field.key) }}
              </p>
              <p v-else-if="!initialized && field.key === 'password'" id="auth-password-rule" class="auth-rule">
                12–128 位，且同时包含字母和数字。
              </p>
            </div>

            <el-button type="primary" class="auth-submit" size="large" native-type="submit" :loading="busy">
              {{ initialized ? '登录平台' : '初始化并登录' }}
              <el-icon aria-hidden="true"><ArrowRight /></el-icon>
            </el-button>
          </fieldset>
        </form>

        <p v-if="initialized" class="auth-support">忘记密码请联系平台管理员。</p>
      </section>
    </div>
  </main>
</template>

<style scoped>
.auth-layout {
  display: grid;
  place-items: center;
  min-height: 100dvh;
  padding: clamp(16px, 4vw, 52px);
  background: radial-gradient(ellipse at 14% 10%, #dbeee6 0, transparent 55%), #f3f7f6;
}

.auth-card {
  display: grid;
  grid-template-columns: minmax(260px, 0.82fr) minmax(0, 1.18fr);
  width: min(100%, 940px);
  overflow: hidden;
  border: 1px solid var(--surface-border);
  border-radius: 20px;
  background: #fff;
  box-shadow: 0 18px 54px #163b3915;
}

.auth-brand-panel {
  position: relative;
  isolation: isolate;
  display: flex;
  min-width: 0;
  flex-direction: column;
  justify-content: space-between;
  gap: 20px;
  padding: clamp(24px, 3.4vw, 44px);
  overflow: hidden;
  background: var(--workspace-hero);
  color: #fff;
}

.auth-brand-panel::after {
  position: absolute;
  z-index: -1;
  right: -96px;
  bottom: -125px;
  width: 342px;
  height: 342px;
  border-radius: 50%;
  background: repeating-radial-gradient(circle, transparent 0 26px, #a5e8d330 27px 28px, transparent 29px 38px);
  content: '';
}

.auth-brand {
  display: flex;
  align-items: center;
  gap: 9px;
  color: #f3fbf8;
  font-size: 16px;
}

.auth-brand .brand-mark {
  transform: scale(0.85);
  transform-origin: left center;
}

.auth-campus {
  max-width: 12em;
  margin: 0;
  color: #f0faf6;
  font-size: clamp(22px, 2.6vw, 35px);
  font-weight: 650;
  line-height: 1.4;
  overflow-wrap: anywhere;
}

.auth-version {
  color: var(--workspace-on-hero);
  font-size: 12px;
}

.auth-form-wrap {
  align-self: center;
  min-width: 0;
  padding: clamp(24px, 5vw, 66px);
}

.auth-setup .auth-form-wrap {
  padding-block: clamp(24px, 4vw, 40px);
}

.auth-form-wrap h1 {
  margin: 0 0 23px;
  font-size: clamp(25px, 3vw, 33px);
}

.auth-setup h1 {
  margin-bottom: 6px;
}

.auth-intro {
  margin: 0 0 19px;
  font-size: 14px;
}

.auth-fields {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  min-width: 0;
  gap: 13px;
  margin: 0;
  padding: 0;
  border: 0;
}

.auth-field,
.auth-submit {
  grid-column: 1 / -1;
}

.auth-field {
  display: grid;
  align-content: start;
  min-width: 0;
  gap: 5px;
}

.auth-identity-field {
  grid-column: auto;
}

.auth-field label {
  color: #2c4659;
  font-size: 14px;
  font-weight: 600;
}

.auth-control {
  width: 100%;
  min-width: 0;
  min-height: 45px;
  padding: 0 11px;
  border: 1px solid #beced7;
  border-radius: 8px;
  background: #fff;
  color: #20374b;
}

.auth-control::placeholder,
.auth-password input::placeholder {
  color: #637686;
}

.auth-control:focus,
.auth-password:focus-within {
  outline: 2px solid var(--el-color-primary);
  outline-offset: 1px;
}

.auth-control[aria-invalid='true'],
.auth-password.has-error {
  border-color: var(--el-color-danger);
}

.auth-password {
  display: flex;
  align-items: center;
  padding: 0 4px 0 11px;
}

.auth-password input {
  width: 100%;
  min-width: 0;
  min-height: 43px;
  padding: 0;
  border: 0;
  outline: none;
  background: transparent;
  color: inherit;
}

.auth-reveal {
  flex: none;
  min-width: 45px;
  min-height: 36px;
  padding: 0 6px;
  border: 0;
  border-radius: 6px;
  background: #edf5f2;
  color: #175b52;
  font-size: 13px;
  font-weight: 600;
}

.auth-reveal:hover {
  background: #ddece6;
}

.auth-error,
.auth-rule,
.auth-support {
  margin: 0;
  font-size: 13px;
  line-height: 1.55;
}

.auth-error {
  color: #a93636;
}

.auth-server-error {
  display: flex;
  align-items: baseline;
  gap: 7px;
  margin-bottom: 14px;
  padding: 9px 11px;
  border: 1px solid #efd1ce;
  border-radius: 8px;
  background: #fff3f2;
  color: #962e2e;
  font-size: 14px;
  overflow-wrap: anywhere;
}

.auth-server-error .el-icon {
  flex: none;
}

.auth-server-error:focus {
  outline: 2px solid var(--el-color-danger);
  outline-offset: 2px;
}

.auth-submit {
  width: 100%;
  margin-top: 3px;
}

.auth-submit .el-icon {
  margin-inline-start: 7px;
}

.auth-support {
  margin-top: 15px;
  text-align: center;
}

@media (max-width: 700px) {
  .auth-layout {
    padding: 12px;
  }

  .auth-card {
    grid-template-columns: minmax(0, 1fr);
    width: min(100%, 500px);
    border-radius: 14px;
  }

  .auth-brand-panel {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    align-items: center;
    gap: 8px;
    padding: 13px 18px;
  }

  .auth-campus {
    grid-row: 2;
    grid-column: 1 / -1;
    max-width: none;
    color: var(--workspace-on-hero);
    font-size: 13px;
    font-weight: 500;
  }

  .auth-form-wrap,
  .auth-setup .auth-form-wrap {
    padding: 20px 18px;
  }
}

@media (max-width: 430px) {
  .auth-identity-field {
    grid-column: 1 / -1;
  }
}
</style>

<script setup lang="ts">
import { nextTick, reactive, ref, watch } from 'vue'
import { passwordIssue } from '../api'

const passwordPolicy = '密码须为 12–128 位，且同时包含字母和数字'
const visible = defineModel<boolean>({ required: true })
const props = defineProps<{ mustChange: boolean; busy: boolean; error: string; initialCurrentPassword: string }>()
const emit = defineEmits<{ submit: [payload: { currentPassword: string; newPassword: string }]; logout: [] }>()

const form = reactive({ currentPassword: '', newPassword: '', confirmation: '' })
const currentError = ref('')
const newError = ref('')
const confirmError = ref('')
const currentInput = ref<{ focus?: () => void }>()
const newInput = ref<{ focus?: () => void }>()

watch(visible, open => {
  if (!open) return
  form.currentPassword = props.initialCurrentPassword
  form.newPassword = ''
  form.confirmation = ''
  currentError.value = ''
  newError.value = ''
  confirmError.value = ''
  void nextTick(() => {
    const target = form.currentPassword ? newInput.value : currentInput.value
    target?.focus?.()
  })
})

function submit() {
  currentError.value = ''
  newError.value = ''
  confirmError.value = ''
  if (!form.currentPassword) {
    currentError.value = '请输入当前密码'
    return
  }
  const issue = passwordIssue(form.newPassword)
  if (issue) {
    newError.value = issue
    return
  }
  if (form.newPassword !== form.confirmation) {
    confirmError.value = '两次输入的新密码不一致'
    return
  }
  if (form.currentPassword === form.newPassword) {
    newError.value = '新密码应与当前密码不同'
    return
  }
  emit('submit', { currentPassword: form.currentPassword, newPassword: form.newPassword })
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="mustChange ? '首次登录，请修改密码' : '修改登录密码'"
    width="460px"
    :close-on-click-modal="false"
    :close-on-press-escape="!mustChange"
    :show-close="!mustChange"
  >
    <el-alert
      v-if="mustChange"
      title="为保护账号，请先修改初始密码再继续使用。"
      type="info"
      :closable="false"
      show-icon
      class="section-notice"
    />
    <el-alert v-if="error" :title="error" type="error" :closable="false" class="section-notice" />
    <el-form label-position="top" @submit.prevent="submit">
      <el-form-item label="当前密码" required :error="currentError">
        <el-input
          ref="currentInput"
          v-model="form.currentPassword"
          type="password"
          show-password
          autocomplete="current-password"
          :disabled="busy"
        />
      </el-form-item>
      <el-form-item label="新密码" required :error="newError">
        <el-input
          ref="newInput"
          v-model="form.newPassword"
          type="password"
          show-password
          autocomplete="new-password"
          :placeholder="passwordPolicy"
          maxlength="128"
          :disabled="busy"
        />
        <div class="field-help">{{ passwordPolicy }}，且不能与当前密码相同。</div>
      </el-form-item>
      <el-form-item label="确认新密码" required :error="confirmError">
        <el-input
          v-model="form.confirmation"
          type="password"
          show-password
          autocomplete="new-password"
          placeholder="再次输入新密码"
          maxlength="128"
          :disabled="busy"
        />
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button v-if="mustChange" @click="emit('logout')">退出登录</el-button>
      <el-button v-else @click="visible = false">取消</el-button>
      <el-button type="primary" :loading="busy" @click="submit">保存新密码</el-button>
    </template>
  </el-dialog>
</template>

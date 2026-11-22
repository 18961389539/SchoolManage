<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { passwordIssue, type Row } from '../api'

const passwordPolicy = '密码须为 12–128 位，且同时包含字母和数字'
const visible = defineModel<boolean>({ required: true })
const props = defineProps<{ person: Row | null; busy: boolean }>()
const emit = defineEmits<{ submit: [password: string] }>()

const password = ref('')
const localError = ref('')
const passwordInput = ref<{ focus?: () => void }>()

watch(visible, open => {
  if (!open) return
  password.value = ''
  localError.value = ''
  void nextTick(() => passwordInput.value?.focus?.())
})

function submit() {
  localError.value = ''
  const issue = passwordIssue(password.value)
  if (issue) {
    localError.value = issue
    return
  }
  emit('submit', password.value)
}
</script>

<template>
  <el-dialog v-model="visible" title="重置账号密码" width="440px" :close-on-click-modal="false">
    <p class="dialog-intro">为 {{ props.person?.name }} 设置新密码。该账号下次登录时需再次修改密码。</p>
    <el-form label-position="top" @submit.prevent="submit">
      <el-form-item label="新密码" required :error="localError">
        <el-input
          ref="passwordInput"
          v-model="password"
          type="password"
          show-password
          :placeholder="passwordPolicy"
          autocomplete="new-password"
          maxlength="128"
          :disabled="busy"
        />
        <div class="field-help">{{ passwordPolicy }}</div>
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button @click="visible = false">取消</el-button>
      <el-button type="primary" :loading="busy" @click="submit">确认重置</el-button>
    </template>
  </el-dialog>
</template>

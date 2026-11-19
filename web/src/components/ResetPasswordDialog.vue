<script setup lang="ts">
import { ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { passwordIssue, type Row } from '../api'

const visible = defineModel<boolean>({ required: true })
const props = defineProps<{ person: Row | null; busy: boolean }>()
const emit = defineEmits<{ submit: [password: string] }>()

const password = ref('')

watch(visible, open => {
  if (open) password.value = ''
})

function submit() {
  const issue = passwordIssue(password.value)
  if (issue) {
    ElMessage.warning(issue)
    return
  }
  emit('submit', password.value)
}
</script>

<template>
  <el-dialog v-model="visible" title="重置账号密码" width="440px" :close-on-click-modal="false">
    <p class="dialog-intro">为 {{ props.person?.name }} 设置新密码。该账号下次登录时需再次修改密码。</p>
    <el-input
      v-model="password"
      type="password"
      show-password
      placeholder="请输入至少 12 位的新密码"
      autocomplete="new-password"
    />
    <template #footer>
      <el-button @click="visible = false">取消</el-button>
      <el-button type="primary" :loading="busy" @click="submit">确认重置</el-button>
    </template>
  </el-dialog>
</template>

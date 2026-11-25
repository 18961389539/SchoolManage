<script setup lang="ts">
import { computed, ref } from 'vue'
import { ElMessage } from 'element-plus'

const visible = defineModel<boolean>({ required: true })
const props = defineProps<{
  title?: string
  account?: string
  password?: string
  note?: string
}>()

const copying = ref(false)
const heading = computed(() => props.title || '请立即抄写初始密码')
const explanation = computed(
  () =>
    props.note ||
    '请立即复制并当面交给本人。关闭后无法再查看这份密码，平台也不会保存明文密码。对方首次登录后需要修改密码。忘记密码时，仍由管理员在人员管理中重置。'
)

async function copyPassword() {
  const value = props.password || ''
  if (!value) return
  copying.value = true
  try {
    if (navigator.clipboard?.writeText) await navigator.clipboard.writeText(value)
    else {
      const input = document.createElement('textarea')
      input.value = value
      input.setAttribute('readonly', 'readonly')
      document.body.appendChild(input)
      input.select()
      document.execCommand('copy')
      document.body.removeChild(input)
    }
    ElMessage.success('密码已复制')
  } catch {
    ElMessage.warning('复制失败，请手动选中后复制')
  } finally {
    copying.value = false
  }
}
</script>

<template>
  <el-dialog v-model="visible" :title="heading" width="min(520px, 94vw)" append-to-body :close-on-click-modal="false">
    <p class="receipt-note">{{ explanation }}</p>
    <el-form label-position="top">
      <el-form-item v-if="account" label="账号">
        <el-input :model-value="account" readonly />
      </el-form-item>
      <el-form-item label="密码">
        <div class="receipt-password">
          <el-input :model-value="password" readonly />
          <el-button type="primary" :loading="copying" @click="copyPassword">复制密码</el-button>
        </div>
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button type="primary" @click="visible = false">我已抄写，关闭</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.receipt-note {
  margin: 0 0 16px;
  color: #56697c;
  font-size: 13px;
  line-height: 1.6;
}

.receipt-password {
  display: flex;
  gap: 8px;
  width: 100%;
}

.receipt-password :deep(.el-input) {
  flex: 1;
}
</style>

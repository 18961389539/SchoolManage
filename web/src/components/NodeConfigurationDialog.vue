<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import type { Row } from '../api'
import { shanghaiInputTime, shanghaiInputToIso } from '../time'
import { focusFirstField } from '../focusField'

const visible = defineModel<boolean>({ required: true })
const props = defineProps<{ node: Row | null; value: Row; saving: boolean }>()
const emit = defineEmits<{ submit: [payload: Row] }>()

const configuration = reactive({ ipAddress: '', subnetMask: '', gateway: '', systemTime: '' })
const updateSystemTime = ref(false)
const baseline = ref('')
const dirty = computed(
  () => JSON.stringify({ ...configuration, updateSystemTime: updateSystemTime.value }) !== baseline.value
)

const configForm = ref<{ $el?: HTMLElement }>()
function focusConfigForm() {
  focusFirstField(configForm.value?.$el)
}

watch(visible, open => {
  if (!open) return
  Object.assign(configuration, props.value, { systemTime: shanghaiInputTime(props.value.systemTime) })
  updateSystemTime.value = false
  baseline.value = JSON.stringify({ ...configuration, updateSystemTime: false })
})

async function confirmDiscard() {
  if (props.saving) return false
  if (!dirty.value) return true
  try {
    await ElMessageBox.confirm('节点配置还没有保存，关闭后不会保留。', '放弃未保存的修改？', {
      type: 'warning',
      confirmButtonText: '放弃修改',
      cancelButtonText: '继续编辑'
    })
    return true
  } catch {
    return false
  }
}

async function handleBeforeClose(done: () => void) {
  if (await confirmDiscard()) done()
}

async function cancel() {
  if (await confirmDiscard()) visible.value = false
}

function submit() {
  if (!configuration.ipAddress || !configuration.subnetMask) {
    ElMessage.warning('请填写 IP 地址与子网掩码')
    return
  }
  let systemTime: string | null = null
  if (updateSystemTime.value) {
    systemTime = shanghaiInputToIso(configuration.systemTime)
    if (!systemTime) {
      ElMessage.warning('请填写有效的北京时间')
      return
    }
  }
  emit('submit', {
    ipAddress: configuration.ipAddress,
    subnetMask: configuration.subnetMask,
    gateway: configuration.gateway,
    systemTime
  })
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="`${node?.name || '节点'} · 系统配置`"
    width="600px"
    :close-on-click-modal="false"
    :before-close="handleBeforeClose"
    @opened="focusConfigForm"
  >
    <el-alert
      title="仅修改内置模拟节点的网络参数与模拟时钟，不修改实际 Win11 主机。"
      type="info"
      :closable="false"
      show-icon
      class="section-notice"
    />
    <el-form scroll-to-error ref="configForm" label-position="top" class="entity-form">
      <el-form-item label="IPv4 地址" required>
        <el-input v-model="configuration.ipAddress" placeholder="192.0.2.10" />
      </el-form-item>
      <el-form-item label="子网掩码" required>
        <el-input v-model="configuration.subnetMask" placeholder="255.255.255.0" />
      </el-form-item>
      <el-form-item label="默认网关">
        <el-input v-model="configuration.gateway" placeholder="192.0.2.1" />
      </el-form-item>
      <el-form-item label="更新模拟系统时间"><el-switch v-model="updateSystemTime" /></el-form-item>
      <el-form-item label="模拟系统时间（北京时间）" class="full-field">
        <el-input v-model="configuration.systemTime" type="datetime-local" :disabled="!updateSystemTime" />
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button @click="cancel">取消</el-button>
      <el-button type="primary" :loading="saving" @click="submit">保存模拟配置</el-button>
    </template>
  </el-dialog>
</template>

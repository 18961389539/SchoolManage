<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { api, messageOf, put } from '../api'
import type { CaseDevice } from '../caseWork'
import VmConsoleDialog from './VmConsoleDialog.vue'

const props = defineProps<{ url: string; readOnly?: boolean; locked?: boolean }>()

function lockedReason(): string | undefined {
  if (props.locked) return '答题已锁定，不能再连接或修改配置'
}
function saveReason(): string | undefined {
  if (props.locked) return '答题已锁定，不能再修改配置'
  if (!changed.value) return '没有需要保存的修改'
}
const emit = defineEmits<{ saved: [hasWork: boolean]; dirty: [dirty: boolean] }>()

interface Environment {
  revision: number
  hasWork: boolean
  devices: CaseDevice[]
  virtualMachine?: { id: string; name: string } | null
}

const environment = ref<Environment | null>(null)
const selectedId = ref('')
const draft = ref<CaseDevice['configuration'] | null>(null)
const error = ref('')
const busy = ref(false)
const machineVisible = ref(false)
const device = computed(() => environment.value?.devices.find(item => item.id === selectedId.value))
const changed = computed(() => JSON.stringify(device.value?.configuration ?? null) !== JSON.stringify(draft.value))
watch(changed, value => emit('dirty', value))

function select(id: string) {
  selectedId.value = id
  draft.value = device.value ? JSON.parse(JSON.stringify(device.value.configuration)) : null
  emit('dirty', false)
}

function applyEnvironment(next: Environment) {
  environment.value = next
  select(next.devices.find(item => item.id === selectedId.value)?.id ?? next.devices[0]?.id ?? '')
  emit('saved', next.hasWork)
}

async function load() {
  busy.value = true
  error.value = ''
  try {
    applyEnvironment(await api<Environment>(props.url))
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    busy.value = false
  }
}

async function save() {
  if (props.readOnly || props.locked || busy.value || !device.value || !draft.value || !environment.value) return
  busy.value = true
  error.value = ''
  try {
    applyEnvironment(
      await put<Environment>(`${props.url}/devices/${device.value.id}`, {
        revision: environment.value.revision,
        configuration: draft.value
      })
    )
    ElMessage.success('实操配置已保存')
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    busy.value = false
  }
}

onMounted(load)
</script>

<template>
  <div v-loading="busy" class="exam-practical-panel">
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" />
    <div class="practical-device-toolbar">
      <el-tooltip
        v-if="environment?.virtualMachine"
        :disabled="!lockedReason()"
        :content="lockedReason()"
        placement="bottom"
      >
        <span class="disabled-action-wrap">
          <el-button :disabled="locked" type="primary" plain @click="machineVisible = true">连接模拟虚拟机</el-button>
        </span>
      </el-tooltip>
      <el-select
        v-if="environment"
        :model-value="selectedId"
        aria-label="选择模拟设备"
        :disabled="busy || changed"
        @change="select"
      >
        <el-option v-for="item in environment.devices" :key="item.id" :label="item.name" :value="item.id" />
      </el-select>
      <el-button v-if="readOnly || error" :disabled="busy" @click="load">刷新环境</el-button>
    </div>
    <el-form scroll-to-error v-if="draft" label-position="top">
      <el-form-item label="主机名">
        <el-input v-model="draft.hostname" maxlength="63" :readonly="readOnly" :disabled="busy || locked" />
      </el-form-item>
      <el-form-item v-for="(port, name) in draft.interfaces" :key="name" :label="name">
        <div class="practical-port">
          <el-switch
            v-model="port.enabled"
            :disabled="readOnly || busy || locked"
            active-text="启用"
            inactive-text="停用"
          />
          <el-input
            v-model="port.description"
            maxlength="100"
            placeholder="接口说明"
            :readonly="readOnly"
            :disabled="busy || locked"
          />
        </div>
      </el-form-item>
    </el-form>
    <div v-if="environment && !readOnly" class="practical-device-toolbar">
      <el-tooltip :disabled="busy || !saveReason()" :content="saveReason()" placement="top">
        <span class="disabled-action-wrap">
          <el-button type="primary" :disabled="busy || locked || !changed" @click="save">保存配置</el-button>
        </span>
      </el-tooltip>
      <el-button v-if="changed" :disabled="busy" @click="select(selectedId)">撤销修改</el-button>
      <span>交卷时以已保存的设备配置计分。</span>
    </div>
  </div>
  <VmConsoleDialog
    v-if="environment?.virtualMachine"
    v-model="machineVisible"
    scope="exam"
    :target-id="environment.virtualMachine.id"
    :machine-name="environment.virtualMachine.name"
  />
</template>

<style scoped>
.practical-device-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
  margin: 12px 0;
}
.practical-device-toolbar :deep(.el-select) {
  width: min(100%, 320px);
}
.practical-port {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  width: 100%;
}
.practical-port :deep(.el-input) {
  flex: 1 1 200px;
}
</style>

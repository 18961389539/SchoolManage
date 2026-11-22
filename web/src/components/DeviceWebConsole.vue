<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { Refresh, Setting } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { api, messageOf, put } from '../api'

type Device = { id: string; name: string }
type DeviceInterface = { name: string; enabled: boolean; description: string }
type WebConfiguration = Device & {
  isSimulated: boolean
  hostname: string
  interfaces: DeviceInterface[]
  enabledInterfaceCount: number
  busy: boolean
  hasStartupConfiguration: boolean
  startupHostname: string | null
  unsavedChanges: boolean
  version: string
}

const props = defineProps<{ device: Device | null }>()
const emit = defineEmits<{ close: []; changed: [] }>()
const snapshot = ref<WebConfiguration | null>(null)
const hostname = ref('')
const interfaces = ref<DeviceInterface[]>([])
const loading = ref(false)
const action = ref<'running' | 'startup' | null>(null)
const error = ref('')
let request: AbortController | null = null

const path = computed(() => (props.device ? `/devices/${encodeURIComponent(props.device.id)}/web-console` : ''))
const dirty = computed(() => {
  const saved = snapshot.value
  if (!saved) return false
  if (hostname.value !== saved.hostname || interfaces.value.length !== saved.interfaces.length) return true
  const byName = new Map(saved.interfaces.map(item => [item.name, item]))
  return interfaces.value.some(item => {
    const other = byName.get(item.name)
    return !other || item.enabled !== other.enabled || item.description !== other.description
  })
})
const canEdit = computed(() => Boolean(snapshot.value && !snapshot.value.busy && !loading.value && !action.value))

function showConfiguration(value: WebConfiguration) {
  snapshot.value = value
  hostname.value = value.hostname
  interfaces.value = value.interfaces.map(item => ({ ...item }))
}

async function refresh() {
  request?.abort()
  if (!path.value) return
  const pending = new AbortController()
  request = pending
  loading.value = true
  error.value = ''
  try {
    showConfiguration(await api<WebConfiguration>(path.value, { signal: pending.signal }))
  } catch (err) {
    if (!pending.signal.aborted) error.value = messageOf(err)
  } finally {
    if (request === pending) {
      loading.value = false
      request = null
    }
  }
}

async function save(target: 'running' | 'startup') {
  if (!canEdit.value || !snapshot.value || !path.value) return
  const endpoint = path.value
  action.value = target
  error.value = ''
  try {
    const version = snapshot.value.version
    const result =
      target === 'running'
        ? await put<WebConfiguration>(endpoint, {
            version,
            hostname: hostname.value.trim(),
            interfaces: interfaces.value
          })
        : await put<WebConfiguration>(`${endpoint}/startup`, { version })
    if (path.value === endpoint) {
      showConfiguration(result)
      emit('changed')
      ElMessage.success(target === 'running' ? '模拟运行配置已更新' : '模拟启动配置已保存')
    }
  } catch (err) {
    if (path.value === endpoint) error.value = messageOf(err)
  } finally {
    action.value = null
  }
}

watch(
  () => props.device?.id,
  () => {
    request?.abort()
    snapshot.value = null
    if (props.device) void refresh()
  },
  { immediate: true }
)
onUnmounted(() => request?.abort())
</script>

<template>
  <el-dialog
    :model-value="Boolean(device)"
    :title="device ? `${device.name} · 模拟 Web 管理` : '模拟 Web 管理'"
    width="min(46rem, 94vw)"
    :close-on-click-modal="false"
    :close-on-press-escape="!action"
    :show-close="!action"
    append-to-body
    @close="emit('close')"
  >
    <div v-loading="loading" class="device-web-content">
      <el-alert title="模拟设备" type="info" show-icon :closable="false">
        <p>此页面与模拟终端共用配置。保存为启动配置后，可通过终端的 reload 命令恢复。</p>
      </el-alert>
      <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" role="alert" />
      <el-alert
        v-if="snapshot?.busy"
        title="设备会话正在使用中。会话结束后，请刷新以编辑配置。"
        type="warning"
        show-icon
        :closable="false"
      />
      <template v-if="snapshot">
        <dl class="device-web-summary">
          <div>
            <dt>已启用接口</dt>
            <dd>{{ snapshot.enabledInterfaceCount }} / {{ snapshot.interfaces.length }}</dd>
          </div>
          <div>
            <dt>启动主机名</dt>
            <dd>{{ snapshot.startupHostname ?? '未保存' }}</dd>
          </div>
          <div>
            <dt>配置状态</dt>
            <dd>
              <el-tag :type="snapshot.unsavedChanges ? 'warning' : 'info'" size="small">
                {{
                  snapshot.unsavedChanges
                    ? '运行配置尚未保存到启动配置'
                    : snapshot.hasStartupConfiguration
                      ? '运行与启动配置一致'
                      : '使用默认配置'
                }}
              </el-tag>
            </dd>
          </div>
        </dl>
        <el-form label-position="top" @submit.prevent="save('running')">
          <el-form-item label="运行主机名">
            <el-input v-model="hostname" :disabled="!canEdit" maxlength="63" />
          </el-form-item>
          <el-table :data="interfaces" row-key="name" aria-label="模拟设备接口">
            <el-table-column prop="name" label="接口" min-width="120" />
            <el-table-column label="状态" min-width="85">
              <template #default="{ row }">
                <el-switch v-model="row.enabled" :aria-label="`${row.name} 启用`" :disabled="!canEdit" />
              </template>
            </el-table-column>
            <el-table-column label="说明" min-width="150">
              <template #default="{ row }">
                <el-input
                  v-model="row.description"
                  :aria-label="`${row.name} 说明`"
                  :disabled="!canEdit"
                  maxlength="100"
                />
              </template>
            </el-table-column>
          </el-table>
        </el-form>
      </template>
    </div>
    <template #footer>
      <div class="device-web-actions">
        <el-button :icon="Refresh" :loading="loading" :disabled="Boolean(action)" @click="refresh">刷新配置</el-button>
        <el-button
          v-if="snapshot"
          :disabled="!canEdit || dirty || (snapshot.hasStartupConfiguration && !snapshot.unsavedChanges)"
          :loading="action === 'startup'"
          @click="save('startup')"
          >保存为启动配置</el-button
        >
        <el-button
          v-if="snapshot"
          type="primary"
          :icon="Setting"
          :disabled="!canEdit || !dirty"
          :loading="action === 'running'"
          @click="save('running')"
          >应用运行配置</el-button
        >
        <el-button :disabled="Boolean(action)" @click="emit('close')">关闭</el-button>
      </div>
    </template>
  </el-dialog>
</template>

<style scoped>
.device-web-content {
  display: grid;
  gap: 1rem;
}
.device-web-content p {
  margin: 0.25rem 0 0;
}
.device-web-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 0.75rem 1.5rem;
  margin: 0;
}
.device-web-summary dt {
  color: var(--el-text-color-secondary);
  margin-bottom: 0.25rem;
}
.device-web-summary dd {
  margin: 0;
}
.device-web-actions {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 0.5rem;
}
.device-web-actions :deep(.el-button) {
  margin: 0;
}
</style>

<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { Refresh, Right } from '@element-plus/icons-vue'
import { api, dateTime, messageOf, post } from '../api'

const visible = defineModel<boolean>({ required: true })
const props = defineProps<{ scope: 'machine' | 'classroom' | 'camp' | 'exam'; targetId: string; machineName: string }>()

interface ConsoleSnapshot {
  id: string
  name: string
  uuid: string
  ipAddress: string
  startedAt: string | null
  connectionVersion: string
  canWrite: boolean
  linkUp: boolean
  lines: { sequence: number; at: string; actor: string; command: string; output: string }[]
}

const snapshot = ref<ConsoleSnapshot | null>(null)
const error = ref('')
const input = ref('')
const loading = ref(false)
const sending = ref(false)
const output = ref<HTMLElement>()
const path = computed(() => `/vm-console/${props.scope}/${props.targetId}`)
let generation = 0

async function update(data: ConsoleSnapshot) {
  const log = output.value
  const atEnd = !log || log.scrollHeight - log.scrollTop - log.clientHeight < 32
  snapshot.value = data
  if (!atEnd) return
  await nextTick()
  if (output.value) output.value.scrollTop = output.value.scrollHeight
}

async function refresh(signal?: AbortSignal) {
  if (sending.value || loading.value || !visible.value) return
  const version = generation
  loading.value = true
  try {
    const data = await api<ConsoleSnapshot>(path.value, { signal })
    if (version !== generation) return
    error.value = ''
    await update(data)
  } catch (failure) {
    if (version !== generation || signal?.aborted) return
    snapshot.value = null
    error.value = messageOf(failure)
  } finally {
    if (version === generation) loading.value = false
  }
}

async function send() {
  const command = input.value.trim()
  const current = snapshot.value
  if (!command || !current?.canWrite || sending.value) return
  const version = ++generation
  loading.value = false
  sending.value = true
  error.value = ''
  try {
    const data = await post<ConsoleSnapshot>(`${path.value}/commands`, {
      command,
      connectionVersion: current.connectionVersion
    })
    if (version !== generation) return
    input.value = ''
    await update(data)
  } catch (failure) {
    if (version === generation) error.value = messageOf(failure)
  } finally {
    if (version === generation) sending.value = false
  }
}

watch(
  () => [visible.value, path.value] as const,
  ([open], _, onCleanup) => {
    generation++
    input.value = ''
    snapshot.value = null
    error.value = ''
    loading.value = false
    sending.value = false
    if (!open) return
    const controller = new AbortController()
    void refresh(controller.signal)
    const timer = window.setInterval(() => void refresh(controller.signal), 1500)
    onCleanup(() => {
      controller.abort()
      window.clearInterval(timer)
      generation++
    })
  },
  { immediate: true }
)
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="`模拟虚拟机控制台 · ${machineName}`"
    width="min(800px, 96vw)"
    append-to-body
    destroy-on-close
  >
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" />
    <el-skeleton v-if="loading && !snapshot" :rows="4" animated />
    <template v-if="snapshot">
      <div class="vm-console-meta">
        <el-tag size="small" type="success">已连接</el-tag>
        <span>IP {{ snapshot.ipAddress }}</span>
        <span>模拟网卡 {{ snapshot.linkUp ? '启用' : '停用' }}</span>
        <span>启动于 {{ dateTime(snapshot.startedAt) }}</span>
      </div>
      <div ref="output" class="vm-console-log" role="log" aria-label="虚拟机操作记录" tabindex="0">
        <p class="console-intro">内置模拟控制台 · 输入 help 查看可用命令</p>
        <div v-for="line in snapshot.lines" :key="line.sequence" class="console-line">
          <span class="line-command">{{ line.actor }} &gt; {{ line.command }}</span>
          <pre>{{ line.output }}</pre>
        </div>
      </div>
      <el-form v-if="snapshot.canWrite" class="vm-console-form" @submit.prevent="send">
        <el-input v-model="input" aria-label="模拟命令" placeholder="输入模拟命令" maxlength="100" />
        <el-button type="primary" native-type="submit" :icon="Right" :loading="sending" :disabled="!input.trim()"
          >执行</el-button
        >
      </el-form>
      <p v-else>当前为查看模式。可通过课堂或岗位测评的监控入口操作。</p>
    </template>
    <template #footer>
      <el-button :icon="Refresh" :loading="loading && !snapshot" @click="refresh()">刷新连接</el-button>
      <el-button @click="visible = false">关闭</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.vm-console-meta,
.vm-console-form {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
  margin-block: 12px;
}
.vm-console-form :deep(.el-input) {
  flex: 1 1 260px;
}
.vm-console-log {
  min-height: 200px;
  max-height: min(46vh, 400px);
  overflow-y: auto;
  padding: 14px 18px;
  border-radius: 8px;
  background: #172335;
  color: #e5edf5;
  font-family: Consolas, monospace;
  overflow-wrap: anywhere;
}
.console-intro {
  margin-top: 0;
  color: #9ab5d0;
}
.line-command {
  color: #7fe0c5;
}
.console-line pre {
  margin: 2px 0 12px;
  white-space: pre-wrap;
  font: inherit;
}
</style>

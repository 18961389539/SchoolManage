<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { Close, Refresh } from '@element-plus/icons-vue'
import type { Row } from '../api'
const props = defineProps<{ device: Row | null }>()
const emit = defineEmits<{ close: [] }>()
const container = ref<HTMLDivElement>(),
  status = ref('未连接'),
  connected = ref(false),
  error = ref('')
let terminal: Terminal | undefined,
  fit: FitAddon | undefined,
  socket: WebSocket | undefined,
  observer: ResizeObserver | undefined
let connectionVersion = 0
const cleanup = () => {
  connectionVersion++
  if (socket) {
    socket.onopen = null
    socket.onclose = null
    socket.onerror = null
    socket.onmessage = null
    socket.close()
    socket = undefined
  }
  observer?.disconnect()
  observer = undefined
  terminal?.dispose()
  terminal = undefined
  fit = undefined
  connected.value = false
}
async function connect() {
  cleanup()
  const version = connectionVersion
  error.value = ''
  status.value = '正在连接'
  await nextTick()
  if (version !== connectionVersion || !container.value || !props.device) return
  if (props.device.connectionType === 'ssh') {
    status.value = '连接方式暂不可用'
    error.value = '当前版本尚未提供 SSH 终端适配。请使用内置模拟设备，或联系交付人员确认真实设备接入方案。'
    return
  }
  terminal = new Terminal({
    cursorBlink: true,
    fontSize: 15,
    fontFamily: 'Cascadia Code, Consolas, monospace',
    theme: { background: '#0d1929', foreground: '#dce8f7', cursor: '#60c6b9', selectionBackground: '#335374' },
    convertEol: true,
    scrollback: 3000
  })
  fit = new FitAddon()
  terminal.loadAddon(fit)
  terminal.open(container.value)
  fit.fit()
  observer = new ResizeObserver(() => {
    try {
      fit?.fit()
    } catch {}
  })
  observer.observe(container.value)
  const url = new URL(`/api/terminal/${encodeURIComponent(props.device.id)}`, location.href)
  url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:'
  socket = new WebSocket(url)
  socket.onopen = () => {
    connected.value = true
    status.value = '已连接'
    try {
      fit?.fit()
    } catch {}
    terminal?.focus()
  }
  socket.onmessage = event => {
    if (typeof event.data === 'string') terminal?.write(event.data)
  }
  socket.onerror = () => {
    error.value = '连接未建立。请检查设备状态、访问权限及当前会话占用情况。'
    status.value = '连接失败'
    connected.value = false
  }
  socket.onclose = event => {
    connected.value = false
    status.value = '连接已关闭'
    terminal?.writeln('\r\n\x1b[33m[会话已结束]\x1b[0m')
    if (event.reason) error.value = event.reason
  }
  terminal.onData(data => {
    if (socket?.readyState === WebSocket.OPEN) {
      const characters = Array.from(data)
      for (let index = 0; index < characters.length; index += 2048)
        socket.send(characters.slice(index, index + 2048).join(''))
    }
  })
}
function close() {
  cleanup()
  emit('close')
}
watch(
  () => props.device?.id,
  id => {
    if (!id) cleanup()
    else if (container.value) void connect()
  }
)
onBeforeUnmount(cleanup)
</script>
<template>
  <el-dialog
    :model-value="!!device"
    :title="`${device?.name || '设备'} · 远程终端`"
    width="min(1150px, 96vw)"
    :close-on-click-modal="false"
    class="terminal-dialog"
    @close="close"
    @opened="connect"
    ><div class="terminal-toolbar">
      <div>
        <span class="connection-indicator" :class="{ connected }"></span>{{ status
        }}<el-tag v-if="device?.isSimulated || device?.connectionType === 'simulated'" type="warning" size="small"
          >模拟设备</el-tag
        ><span class="terminal-target">{{ device?.host || '内置模拟器' }}</span>
      </div>
      <el-button :icon="Refresh" size="small" @click="connect">重新连接</el-button>
    </div>
    <el-alert v-if="error" :title="error" type="warning" :closable="false" show-icon />
    <div ref="container" class="terminal-container"></div>
    <div class="terminal-help">
      <span>{{
        device?.connectionType === 'simulated'
          ? '模拟命令：help、show version、show interfaces、enable、configure terminal、hostname、exit。'
          : '输入将发送至目标设备；关闭终端将结束当前平台会话。'
      }}</span><el-button :icon="Close" @click="close">断开并关闭</el-button>
    </div></el-dialog
  >
</template>

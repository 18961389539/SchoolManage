<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { messageOf } from '../api'

const props = defineProps<{ sessionId: string; monitor: boolean }>()
const emit = defineEmits<{ assistance: [allowed: boolean] }>()
const sharing = ref(false)
const capturing = ref(false)
const allowAssist = ref(false)
const image = ref('')
const error = ref('')
const starting = ref(false)
let timer = 0
let retryTimer = 0
let captureGeneration = 0
let stream: MediaStream | undefined
let preview: HTMLVideoElement | undefined
let socket: WebSocket | undefined
let disposed = false
let intentionalStop = false

function socketUrl() {
  const url = new URL(`/api/classrooms/case-sessions/${props.sessionId}/live/socket`, location.href)
  url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:'
  return url
}

function closeSocket() {
  const current = socket
  socket = undefined
  if (!current) return
  current.onopen = null
  current.onclose = null
  current.onerror = null
  current.onmessage = null
  current.close()
}

function pauseCapture() {
  captureGeneration++
  window.clearInterval(timer)
  timer = 0
  capturing.value = false
}

function releaseImage() {
  if (image.value.startsWith('blob:')) URL.revokeObjectURL(image.value)
  image.value = ''
}

function stop() {
  intentionalStop = true
  window.clearTimeout(retryTimer)
  pauseCapture()
  closeSocket()
  stream?.getTracks().forEach(track => track.stop())
  stream = undefined
  preview = undefined
  sharing.value = false
  releaseImage()
  emit('assistance', false)
}

function beginCapture(video: HTMLVideoElement) {
  pauseCapture()
  const generation = ++captureGeneration
  const canvas = document.createElement('canvas')
  const context = canvas.getContext('2d')
  if (!context) {
    error.value = '无法读取共享画面'
    stop()
    return
  }
  capturing.value = true
  const send = () => {
    if (generation !== captureGeneration || socket?.readyState !== WebSocket.OPEN || !video.videoWidth) return
    const scale = Math.min(1, 640 / video.videoWidth, 480 / video.videoHeight)
    canvas.width = Math.max(1, Math.round(video.videoWidth * scale))
    canvas.height = Math.max(1, Math.round(video.videoHeight * scale))
    context.drawImage(video, 0, 0, canvas.width, canvas.height)
    canvas.toBlob(blob => {
      if (generation !== captureGeneration || !blob || blob.size > 160000 || socket?.readyState !== WebSocket.OPEN) return
      socket.send(blob)
    }, 'image/jpeg', 0.55)
  }
  send()
  timer = window.setInterval(send, 1000)
}

function openPublisher() {
  closeSocket()
  const current = new WebSocket(socketUrl())
  socket = current
  current.onopen = () => {
    if (socket !== current) return
    current.send(JSON.stringify({ allowAssist: allowAssist.value }))
    sharing.value = true
    error.value = ''
  }
  current.onmessage = event => {
    if (socket !== current || typeof event.data !== 'string' || !preview) return
    let message: { type?: string }
    try {
      message = JSON.parse(event.data) as { type?: string }
    } catch {
      return
    }
    if (message.type === 'start') beginCapture(preview)
    if (message.type === 'stop') pauseCapture()
  }
  current.onerror = () => {
    if (socket === current) error.value = '画面共享连接未建立。'
  }
  current.onclose = event => {
    if (socket !== current) return
    socket = undefined
    pauseCapture()
    if (intentionalStop || disposed) return
    if (event.code === 1008) {
      error.value = event.reason || '共享连接被拒绝'
      stop()
      return
    }
    retryTimer = window.setTimeout(() => {
      if (!intentionalStop && !disposed && stream) openPublisher()
    }, 2000)
  }
}

async function start() {
  if (!navigator.mediaDevices?.getDisplayMedia) {
    error.value = '画面共享需要 HTTPS 或本机地址。'
    return
  }
  intentionalStop = false
  starting.value = true
  error.value = ''
  try {
    stream = await navigator.mediaDevices.getDisplayMedia({
      video: { width: { ideal: 640 }, height: { ideal: 360 } },
      audio: false
    })
    if (disposed) return stop()
    stream.getVideoTracks()[0]?.addEventListener('ended', stop, { once: true })
    const video = document.createElement('video')
    video.muted = true
    video.playsInline = true
    video.srcObject = stream
    await video.play()
    if (!stream || disposed) return stop()
    preview = video
    openPublisher()
  } catch (failure) {
    if (!disposed)
      error.value =
        failure instanceof DOMException && failure.name === 'NotAllowedError' ? '未开启画面共享。' : messageOf(failure)
    stop()
  } finally {
    starting.value = false
  }
}

function openMonitor() {
  closeSocket()
  const current = new WebSocket(socketUrl())
  current.binaryType = 'blob'
  socket = current
  current.onmessage = event => {
    if (socket !== current) return
    if (typeof event.data === 'string') {
      let message: { type?: string; sharing?: boolean; allowAssist?: boolean }
      try {
        message = JSON.parse(event.data) as { type?: string; sharing?: boolean; allowAssist?: boolean }
      } catch {
        return
      }
      if (message.type !== 'status') return
      sharing.value = !!message.sharing
      emit('assistance', !!message.allowAssist)
      if (!message.sharing) releaseImage()
      error.value = ''
      return
    }
    if (!(event.data instanceof Blob)) return
    const next = URL.createObjectURL(event.data)
    releaseImage()
    image.value = next
    sharing.value = true
  }
  current.onerror = () => {
    if (socket === current) error.value = '画面监控连接未建立。'
  }
  current.onclose = () => {
    if (socket !== current) return
    socket = undefined
    sharing.value = false
    releaseImage()
    emit('assistance', false)
    if (disposed) return
    retryTimer = window.setTimeout(() => {
      if (!disposed) openMonitor()
    }, 2000)
  }
}

onMounted(() => {
  if (props.monitor) openMonitor()
})
onBeforeUnmount(() => {
  disposed = true
  if (props.monitor) {
    window.clearTimeout(retryTimer)
    closeSocket()
    releaseImage()
    return
  }
  stop()
})
</script>

<template>
  <section class="case-live" aria-label="课堂实操画面共享">
    <el-alert v-if="error" :title="error" type="warning" :closable="false" />
    <template v-if="monitor">
      <img v-if="image" :src="image" alt="学生共享的实操画面" />
      <p v-else>{{ sharing ? '等待学生画面' : '学生尚未共享画面' }}</p>
    </template>
    <template v-else>
      <div class="share-controls">
        <el-checkbox v-model="allowAssist" :disabled="sharing || starting">允许教师协助修改模拟设备配置</el-checkbox>
        <el-button v-if="sharing" type="danger" plain @click="stop">停止共享</el-button>
        <el-button v-else plain :loading="starting" @click="start">共享实操画面</el-button>
        <el-tag v-if="sharing" :type="capturing ? 'success' : 'info'">{{
          capturing ? '正在传输画面' : '已就绪，教师查看时开始传输'
        }}</el-tag>
      </div>
      <p v-if="!sharing">请选择实操所在的标签页。</p>
    </template>
  </section>
</template>

<style scoped>
.case-live {
  margin: 12px 0;
  padding: 10px;
  border: 1px solid var(--el-border-color);
  border-radius: 6px;
}
.case-live img {
  display: block;
  width: 100%;
  max-height: 420px;
  object-fit: contain;
}
.case-live p {
  margin: 6px 0 0;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.share-controls {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
}
</style>

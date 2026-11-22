<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { api, messageOf, post } from '../api'

const props = defineProps<{ sessionId: string; monitor: boolean }>()
const emit = defineEmits<{ assistance: [allowed: boolean] }>()
const path = `/classrooms/case-sessions/${props.sessionId}/live`
const sharing = ref(false)
const allowAssist = ref(false)
const image = ref('')
const error = ref('')
const starting = ref(false)
let timer = 0
let token = ''
let stream: MediaStream | undefined
let disposed = false
let busy = false

function stop() {
  window.clearInterval(timer)
  stream?.getTracks().forEach(track => track.stop())
  stream = undefined
  const ended = token
  token = ''
  sharing.value = false
  emit('assistance', false)
  if (ended) void post(`${path}/stop`, { token: ended }).catch(() => {})
}

async function start() {
  if (!navigator.mediaDevices?.getDisplayMedia) {
    error.value = '画面共享需要 HTTPS 或本机地址。'
    return
  }
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
    const response = await post<{ token: string }>(path, { allowAssist: allowAssist.value })
    token = response.token
    if (!stream || disposed) return stop()
    sharing.value = true
    await startSending(video)
    emit('assistance', !!token && allowAssist.value)
  } catch (failure) {
    if (!disposed)
      error.value =
        failure instanceof DOMException && failure.name === 'NotAllowedError' ? '未开启画面共享。' : messageOf(failure)
    stop()
  } finally {
    starting.value = false
  }
}

async function startSending(video: HTMLVideoElement) {
  const canvas = document.createElement('canvas')
  const context = canvas.getContext('2d')
  if (!context) throw new Error('无法读取共享画面')
  async function send() {
    if (!context || !token || busy || !video.videoWidth) return
    const current = token
    busy = true
    try {
      const scale = Math.min(1, 640 / video.videoWidth, 480 / video.videoHeight)
      canvas.width = Math.max(1, Math.round(video.videoWidth * scale))
      canvas.height = Math.max(1, Math.round(video.videoHeight * scale))
      context.drawImage(video, 0, 0, canvas.width, canvas.height)
      const frame = canvas.toDataURL('image/jpeg', 0.55).split(',')[1]
      if (frame && frame.length <= 160000) await post(`${path}/frame`, { token: current, image: frame })
    } catch (failure) {
      if (token === current) {
        error.value = messageOf(failure)
        stop()
      }
    } finally {
      busy = false
    }
  }
  await send()
  if (token) timer = window.setInterval(() => void send(), 1000)
}

async function watchFrame() {
  if (busy || disposed) return
  busy = true
  try {
    const result = await api<{ sharing: boolean; allowAssist: boolean; image: string | null }>(path)
    if (disposed) return
    sharing.value = result.sharing
    image.value = result.image ? `data:image/jpeg;base64,${result.image}` : ''
    emit('assistance', result.allowAssist)
    error.value = ''
  } catch (failure) {
    if (disposed) return
    sharing.value = false
    image.value = ''
    emit('assistance', false)
    error.value = messageOf(failure)
  } finally {
    busy = false
  }
}

onMounted(() => {
  if (props.monitor) {
    void watchFrame()
    timer = window.setInterval(() => void watchFrame(), 1200)
  }
})
onBeforeUnmount(() => {
  disposed = true
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
        <el-tag v-if="sharing" type="success">正在共享</el-tag>
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

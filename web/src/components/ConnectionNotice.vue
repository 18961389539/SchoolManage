<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'

const CHECK_INTERVAL = 10_000
const REQUEST_TIMEOUT = 4_000
const FAILURES_BEFORE_ALERT = 2

const offline = ref(false)
const recovered = ref(false)
const checking = ref(false)
let timer: number | undefined
let failures = 0
let recoveredTimer: number | undefined

async function check(): Promise<boolean> {
  if (checking.value) return false
  checking.value = true
  const controller = new AbortController()
  const timeout = window.setTimeout(() => controller.abort(), REQUEST_TIMEOUT)
  try {
    const response = await fetch('/api/status', {
      signal: controller.signal,
      credentials: 'same-origin'
    })
    return response.ok
  } catch {
    return false
  } finally {
    window.clearTimeout(timeout)
    checking.value = false
  }
}

function flashRecovered() {
  recovered.value = true
  if (recoveredTimer) window.clearTimeout(recoveredTimer)
  recoveredTimer = window.setTimeout(() => {
    recovered.value = false
  }, 2500)
}

async function poll() {
  // The startup screen and sign-in page have their own error handling; only watch the
  // signed-in shell so a momentary hiccup during boot cannot raise a misleading banner.
  if (!document.querySelector('.app-layout')) {
    failures = 0
    if (offline.value) offline.value = false
    return
  }
  const ok = await check()
  if (ok) {
    if (failures >= FAILURES_BEFORE_ALERT && offline.value) flashRecovered()
    failures = 0
    offline.value = false
  } else {
    failures++
    if (failures >= FAILURES_BEFORE_ALERT) offline.value = true
  }
}

async function reconnect() {
  const ok = await check()
  if (ok) {
    if (offline.value) flashRecovered()
    failures = 0
    offline.value = false
  } else {
    failures = FAILURES_BEFORE_ALERT
    offline.value = true
  }
}

onMounted(() => {
  timer = window.setInterval(poll, CHECK_INTERVAL)
})
onBeforeUnmount(() => {
  if (timer) window.clearInterval(timer)
  if (recoveredTimer) window.clearTimeout(recoveredTimer)
})
</script>

<template>
  <Transition name="connection-notice">
    <div v-if="offline" class="connection-notice is-offline" role="alert" aria-live="assertive">
      <span class="connection-notice__dot" aria-hidden="true"></span>
      <span class="connection-notice__text">与本地服务的连接已中断，操作可能无法保存。</span>
      <button type="button" class="connection-notice__action" :disabled="checking" @click="reconnect">
        {{ checking ? '正在连接…' : '重新连接' }}
      </button>
    </div>
    <div v-else-if="recovered" class="connection-notice is-recovered" role="status" aria-live="polite">
      <span class="connection-notice__dot" aria-hidden="true"></span>
      <span class="connection-notice__text">连接已恢复，可以继续操作。</span>
    </div>
  </Transition>
</template>

<style scoped>
.connection-notice {
  position: fixed;
  top: 12px;
  left: 50%;
  z-index: 3000;
  display: flex;
  align-items: center;
  gap: 10px;
  max-width: calc(100vw - 32px);
  padding: 9px 14px;
  border-radius: 10px;
  box-shadow: 0 8px 24px rgba(17, 36, 59, 0.22);
  font-size: 13px;
  line-height: 1.4;
  transform: translateX(-50%);
}

.connection-notice.is-offline {
  background: #8f2d2d;
  color: #fff;
}

.connection-notice.is-recovered {
  background: #1f6b52;
  color: #fff;
}

.connection-notice__dot {
  flex: 0 0 auto;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: currentColor;
}

.connection-notice.is-offline .connection-notice__dot {
  animation: connection-notice-pulse 1.4s ease-in-out infinite;
}

.connection-notice__text {
  white-space: nowrap;
}

.connection-notice__action {
  flex: 0 0 auto;
  margin-left: 6px;
  padding: 4px 10px;
  border: 1px solid rgba(255, 255, 255, 0.7);
  border-radius: 6px;
  background: rgba(255, 255, 255, 0.1);
  color: inherit;
  font: inherit;
  cursor: pointer;
}

.connection-notice__action:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.22);
}

.connection-notice__action:disabled {
  opacity: 0.65;
  cursor: wait;
}

.connection-notice-enter-active,
.connection-notice-leave-active {
  transition:
    opacity 0.2s ease,
    transform 0.2s ease;
}

.connection-notice-enter-from,
.connection-notice-leave-to {
  opacity: 0;
  transform: translate(-50%, -10px);
}

@keyframes connection-notice-pulse {
  50% {
    opacity: 0.35;
  }
}

@media (max-width: 640px) {
  .connection-notice__text {
    white-space: normal;
  }
}
</style>

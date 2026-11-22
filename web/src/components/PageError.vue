<script setup lang="ts">
import { computed } from 'vue'
import { Refresh } from '@element-plus/icons-vue'

const props = defineProps<{ error?: Error; retry?: () => void }>()

const detail = computed(() => props.error?.message || '')

function retryLoad() {
  if (props.retry) props.retry()
  else location.reload()
}

function reload() {
  location.reload()
}
</script>

<template>
  <section class="page-error surface" role="alert">
    <h1>页面加载失败</h1>
    <p>页面资源没有加载成功，可能是网络中断，或平台刚刚更新过。</p>
    <p v-if="detail" class="page-error-detail">{{ detail }}</p>
    <div class="page-error-actions">
      <el-button :icon="Refresh" @click="retryLoad">重试</el-button>
      <el-button type="primary" @click="reload">刷新页面</el-button>
    </div>
    <p class="page-error-hint">仍无法打开时，请确认平台服务正在运行，或联系管理员。</p>
  </section>
</template>

<script setup lang="ts">
defineProps<{ label?: string }>()
</script>

<template>
  <div class="page-loading" role="status" aria-live="polite" :aria-label="label || '页面加载中'">
    <span class="page-loading-lead" aria-hidden="true" />
    <el-skeleton :rows="6" animated aria-hidden="true" />
  </div>
</template>

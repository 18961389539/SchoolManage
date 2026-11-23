<script setup lang="ts">
import { computed } from 'vue'
import { HomeFilled } from '@element-plus/icons-vue'

const props = defineProps<{ kind: 'missing' | 'forbidden'; label?: string; requested?: string }>()
const emit = defineEmits<{ home: [] }>()

const code = computed(() => (props.kind === 'forbidden' ? '403' : '404'))
const heading = computed(() => (props.kind === 'forbidden' ? '无权访问' : '页面不存在'))
const reason = computed(() =>
  props.kind === 'forbidden'
    ? `当前账号没有打开「${props.label || '该页面'}」的权限。如需使用，请联系管理员调整角色。`
    : '这个地址可能已经失效，或链接本身写错了。'
)
</script>

<template>
  <section class="route-notice surface" role="alert">
    <p class="route-notice-code">{{ code }}</p>
    <h1>{{ heading }}</h1>
    <p>{{ reason }}</p>
    <p v-if="requested" class="route-notice-path">请求的地址：#/{{ requested }}</p>
    <div class="route-notice-actions">
      <el-button type="primary" :icon="HomeFilled" @click="emit('home')">返回首页</el-button>
    </div>
  </section>
</template>

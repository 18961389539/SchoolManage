<script setup lang="ts">
import { Plus, Upload } from '@element-plus/icons-vue'
import type { Entity } from '../entities'

defineProps<{ entity: Entity; entityKey: string; isPeople: boolean }>()
const emit = defineEmits<{ import: []; create: [] }>()

const sectionLabels: Record<string, string> = {
  devices: '实训资源 / 资源管理',
  cabinets: '实训资源 / 资源管理',
  'device-types': '实训资源 / 资源管理',
  nodes: '实训资源 / 资源管理'
}
</script>

<template>
  <section class="page-heading">
    <div>
      <div class="eyebrow">
        {{ isPeople ? '教学组织 / 人员档案' : sectionLabels[entityKey] || '教学组织 / 基础资料' }}
      </div>
      <h1>{{ entity.title }}</h1>
      <p>{{ entity.subtitle }}</p>
    </div>
    <div class="heading-actions">
      <el-button v-if="isPeople" :icon="Upload" @click="emit('import')">批量导入</el-button>
      <el-button type="primary" :icon="Plus" @click="emit('create')">新增{{ entity.singular }}</el-button>
    </div>
  </section>
  <el-alert
    v-if="entityKey === 'devices' || entityKey === 'nodes'"
    class="section-notice"
    :closable="false"
    type="info"
    title="内置模拟资源会明确标注“模拟”。模拟操作仅影响平台内的资源状态，不代表真实硬件或虚拟机。"
    show-icon
  />
</template>

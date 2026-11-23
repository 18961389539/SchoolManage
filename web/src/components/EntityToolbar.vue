<script setup lang="ts">
import { Plus, Upload } from '@element-plus/icons-vue'
import type { Entity } from '../entities'

defineProps<{ entity: Entity; entityKey: string; isPeople: boolean; cabinetScoped: boolean; cabinetName?: string }>()
const emit = defineEmits<{ import: []; create: []; cabinets: [] }>()
</script>

<template>
  <section class="page-heading">
    <div>
      <h1>{{ entity.title }}</h1>
      <p v-if="cabinetName">所属机柜：{{ cabinetName }}</p>
      <p v-else>{{ entity.subtitle }}</p>
    </div>
    <div class="heading-actions">
      <el-button v-if="isPeople && entityKey !== 'admins'" :icon="Upload" @click="emit('import')">批量导入</el-button>
      <el-button v-if="cabinetScoped" @click="emit('cabinets')">返回机柜管理</el-button>
      <el-tooltip
        :disabled="!(cabinetScoped && !cabinetName)"
        :content="`请先进入所属机柜，再新增${entity.singular}`"
        placement="bottom"
      >
        <span class="disabled-action-wrap">
          <el-button type="primary" :icon="Plus" :disabled="cabinetScoped && !cabinetName" @click="emit('create')">
            新增{{ entity.singular }}
          </el-button>
        </span>
      </el-tooltip>
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

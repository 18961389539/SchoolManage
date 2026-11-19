<script setup lang="ts">
import { Close, QuestionFilled, School, User } from '@element-plus/icons-vue'
import type { NavGroup } from '../navigation'

const open = defineModel<boolean>('open', { required: true })
defineProps<{ groups: NavGroup[]; route: string; isAdmin: boolean; schoolName: string; version: string }>()
const emit = defineEmits<{ navigate: [key: string]; help: [] }>()
</script>

<template>
  <div v-if="open" class="sidebar-backdrop" @click="open = false" />
  <aside class="sidebar" :class="{ 'is-open': open }">
    <div class="sidebar-brand" @click="emit('navigate', 'dashboard')">
      <div class="brand-mark"><span /><span /><span /></div>
      <div><strong>实训资源平台</strong><span>TRAINING WORKSPACE</span></div>
      <button class="mobile-close" aria-label="关闭导航" @click.stop="open = false">
        <el-icon><Close /></el-icon>
      </button>
    </div>
    <div class="school-name" :title="schoolName">
      <span class="school-symbol"
        ><el-icon><School /></el-icon></span
      ><span>{{ schoolName }}</span>
    </div>
    <nav class="main-navigation" aria-label="主导航">
      <template v-if="isAdmin">
        <div v-for="group in groups" :key="group.label" class="nav-group">
          <div class="nav-group-label">{{ group.label }}</div>
          <button
            v-for="item in group.items"
            :key="item.key"
            :class="{ active: route === item.key }"
            @click="emit('navigate', item.key)"
          >
            <el-icon :size="19"><component :is="item.icon" /></el-icon><span>{{ item.label }}</span>
            <i v-if="route === item.key" />
          </button>
        </div>
      </template>
      <div v-else class="nav-group">
        <div class="nav-group-label">个人工作空间</div>
        <button class="active" @click="emit('navigate', 'dashboard')">
          <el-icon><User /></el-icon><span>个人首页</span>
        </button>
      </div>
    </nav>
    <div class="sidebar-bottom">
      <button @click="emit('help')">
        <el-icon><QuestionFilled /></el-icon><span>使用帮助</span>
      </button>
      <div class="sidebar-version">
        <span class="live-dot" />本地服务<span>v{{ version }}</span>
      </div>
    </div>
  </aside>
</template>

<script setup lang="ts">
import { ArrowDown, Lock, Menu, QuestionFilled, SwitchButton } from '@element-plus/icons-vue'

defineProps<{ group: string; title: string; userName: string; displayName: string }>()
const emit = defineEmits<{ menu: []; command: [command: string] }>()
</script>

<template>
  <header class="topbar">
    <div class="breadcrumb">
      <el-button :icon="Menu" class="mobile-menu" text aria-label="打开导航" @click="emit('menu')" />
      <span>{{ group }}</span><i>/</i><strong>{{ title }}</strong>
    </div>
    <div class="topbar-right">
      <span class="deployment-badge"><span class="live-dot" />校内服务</span>
      <span class="topbar-separator" />
      <el-dropdown trigger="click" @command="emit('command', $event)">
        <button class="user-menu">
          <span class="user-avatar">{{ (displayName || userName).slice(0, 1) }}</span>
          <span>{{ displayName || userName }}</span>
          <el-icon><ArrowDown /></el-icon>
        </button>
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item command="password" :icon="Lock">修改密码</el-dropdown-item>
            <el-dropdown-item command="help" :icon="QuestionFilled">使用帮助</el-dropdown-item>
            <el-dropdown-item command="logout" :icon="SwitchButton" divided>退出登录</el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
    </div>
  </header>
</template>

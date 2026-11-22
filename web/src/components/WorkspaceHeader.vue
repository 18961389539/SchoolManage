<script setup lang="ts">
import { ArrowDown, Lock, Menu, QuestionFilled, SwitchButton, User } from '@element-plus/icons-vue'

defineProps<{ group: string; title: string; userName: string; displayName: string; role: string; menuOpen: boolean }>()
const roleNames: Record<string, string> = { Admin: '管理员', Teacher: '教师', Student: '学生' }
const emit = defineEmits<{ menu: []; command: [command: string] }>()
</script>

<template>
  <header class="topbar">
    <div class="breadcrumb">
      <el-button
        id="mobile-menu-toggle"
        :icon="Menu"
        class="mobile-menu"
        text
        aria-label="打开导航"
        aria-controls="workspace-navigation"
        :aria-expanded="menuOpen"
        @click="emit('menu')"
      />
      <span class="breadcrumb-group">{{ group }}</span><i aria-hidden="true">/</i><strong>{{ title }}</strong>
    </div>
    <div class="topbar-right">
      <span class="deployment-badge"><span class="live-dot" />校内服务</span>
      <span class="topbar-separator" />
      <el-dropdown trigger="click" @command="emit('command', $event)">
        <button type="button" class="user-menu" :aria-label="`${displayName || userName}的账号菜单`">
          <span class="user-avatar" aria-hidden="true">{{ (displayName || userName).slice(0, 1) }}</span>
          <span class="user-menu-copy" aria-hidden="true">
            <strong>{{ displayName || userName }}</strong>
            <small v-if="roleNames[role]">{{ roleNames[role] }}</small>
          </span>
          <el-icon aria-hidden="true"><ArrowDown /></el-icon>
        </button>
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item command="profile" :icon="User">个人中心</el-dropdown-item>
            <el-dropdown-item command="password" :icon="Lock">修改密码</el-dropdown-item>
            <el-dropdown-item command="help" :icon="QuestionFilled">使用帮助</el-dropdown-item>
            <el-dropdown-item command="logout" :icon="SwitchButton" divided>退出登录</el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
    </div>
  </header>
</template>

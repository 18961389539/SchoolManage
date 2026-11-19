<script setup lang="ts">
import { Connection, Lock, Monitor, School, User } from '@element-plus/icons-vue'
import type { AuthForm } from '../auth'

const auth = defineModel<AuthForm>('auth', { required: true })
defineProps<{ initialized: boolean; version: string; busy: boolean; error: string }>()
const emit = defineEmits<{ submit: [] }>()
</script>

<template>
  <div class="auth-layout">
    <section class="auth-story">
      <div class="auth-brand">
        <div class="brand-mark"><span /><span /><span /></div>
        <div><strong>实训资源平台</strong><span>TRAINING RESOURCE PLATFORM</span></div>
      </div>
      <div class="auth-story-main">
        <div class="auth-story-tag">校内实训 · 统一管理</div>
        <h1>把资源连接起来，<br />让实训有序开展。</h1>
        <p>教学组织、设备终端与虚拟资源，<br />在同一个工作空间协同管理。</p>
        <div class="auth-diagram" aria-hidden="true">
          <div class="diagram-server">
            <i /><i /><i />
            <div class="diagram-light" />
          </div>
          <div class="diagram-line line-one" />
          <div class="diagram-line line-two" />
          <div class="diagram-node node-one">
            <el-icon><Monitor /></el-icon>
          </div>
          <div class="diagram-node node-two">
            <el-icon><Connection /></el-icon>
          </div>
          <div class="diagram-node node-three">
            <el-icon><School /></el-icon>
          </div>
        </div>
      </div>
      <div class="auth-story-footer">
        <span class="live-dot" />Windows 原生部署<span>·</span>浏览器访问<span>·</span>校内数据存储
      </div>
    </section>
    <main class="auth-main">
      <div class="auth-form-wrap">
        <div class="auth-mobile-brand">
          <div class="brand-mark"><span /><span /><span /></div>
          实训资源平台
        </div>
        <div class="eyebrow">{{ initialized ? 'WELCOME BACK' : 'GET STARTED' }}</div>
        <h2>{{ initialized ? '登录工作空间' : '初始化实训平台' }}</h2>
        <p class="auth-subtitle">
          {{ initialized ? '使用你的平台账号继续。' : '创建首个管理员账号，开始配置学校与实训资源。' }}
        </p>
        <el-alert
          v-if="!initialized"
          title="首次初始化请在部署服务器本机打开页面完成。"
          type="info"
          :closable="false"
          show-icon
          class="section-notice"
        />
        <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon class="section-notice" />
        <el-form label-position="top" size="large" @submit.prevent="emit('submit')">
          <el-form-item v-if="!initialized" label="学校名称">
            <el-input
              v-model="auth.schoolName"
              maxlength="100"
              placeholder="例如：信息工程学院"
              autocomplete="organization"
            />
          </el-form-item>
          <el-form-item v-if="!initialized" label="管理员姓名">
            <el-input
              v-model="auth.displayName"
              maxlength="50"
              placeholder="用于平台显示与操作记录"
              autocomplete="name"
            />
          </el-form-item>
          <el-form-item label="平台账号">
            <el-input
              v-model="auth.userName"
              :prefix-icon="User"
              autocomplete="username"
              placeholder="请输入平台账号"
              maxlength="80"
            />
          </el-form-item>
          <el-form-item :label="initialized ? '登录密码' : '管理员密码'">
            <el-input
              v-model="auth.password"
              :prefix-icon="Lock"
              type="password"
              show-password
              :autocomplete="initialized ? 'current-password' : 'new-password'"
              :placeholder="initialized ? '请输入登录密码' : '12–128 位，同时包含字母和数字'"
              maxlength="128"
            />
          </el-form-item>
          <el-form-item v-if="!initialized" label="确认密码">
            <el-input
              v-model="auth.confirmation"
              :prefix-icon="Lock"
              type="password"
              show-password
              autocomplete="new-password"
              placeholder="再次输入管理员密码"
            />
          </el-form-item>
          <el-button type="primary" class="auth-submit" size="large" native-type="submit" :loading="busy">
            {{ initialized ? '登录平台' : '初始化并登录' }}
          </el-button>
        </el-form>
        <p class="auth-support">
          {{ initialized ? '忘记密码请联系平台管理员。' : '请妥善保存管理员账号及初始部署信息。' }}
        </p>
      </div>
      <footer class="auth-footer">
        实训资源平台 <span>v{{ version }}</span>
      </footer>
    </main>
  </div>
</template>

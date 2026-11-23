<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { Key, Refresh, UserFilled } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { api, messageOf, put } from '../api'
import { appState } from '../store'
import type { ProfileResponse } from '../types'

const emit = defineEmits<{ password: [] }>()

const profile = ref<ProfileResponse | null>(null)
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const form = reactive({ name: '', number: '', gender: '', phone: '', email: '' })

const roleLabel = computed(() => {
  if (profile.value?.role === 'Admin') return '管理员'
  if (profile.value?.role === 'Teacher') return '教师'
  return '学生'
})

async function load() {
  loading.value = true
  error.value = ''
  try {
    const data = await api<ProfileResponse>('/profile')
    profile.value = data
    form.name = data.name
    form.number = data.number
    form.gender = data.gender
    form.phone = data.phone
    form.email = data.email
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

async function save() {
  if (!form.name.trim()) {
    error.value = '请填写姓名'
    return
  }
  if (profile.value?.kind !== 'admin' && !form.number.trim()) {
    error.value = '请填写学号/工号'
    return
  }
  saving.value = true
  error.value = ''
  try {
    const data = await put<ProfileResponse>('/profile', {
      name: form.name.trim(),
      number: form.number.trim(),
      gender: form.gender.trim(),
      phone: form.phone.trim(),
      email: form.email.trim()
    })
    profile.value = data
    form.name = data.name
    form.number = data.number
    form.gender = data.gender
    form.phone = data.phone
    form.email = data.email
    if (appState.user) appState.user.displayName = data.displayName
    ElMessage.success('个人信息已保存')
  } catch (failure) {
    error.value = messageOf(failure)
  } finally {
    saving.value = false
  }
}

onMounted(load)
</script>

<template>
  <section class="page-heading">
    <div>
      <h1>个人中心</h1>
      <p>维护姓名、学号/工号与联系方式；登录密码可随时在账号安全中修改。</p>
    </div>
    <div class="heading-actions">
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      <el-button type="primary" :loading="saving" @click="save">保存信息</el-button>
    </div>
  </section>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />

  <div v-loading="loading" class="profile-grid">
    <section class="surface profile-card">
      <header class="profile-card-heading">
        <span class="profile-avatar">{{ (form.name || profile?.userName || '').slice(0, 1) }}</span>
        <div>
          <h2>{{ profile?.name || profile?.displayName || '个人资料' }}</h2>
          <p>{{ profile?.userName || '—' }} · {{ roleLabel }}</p>
        </div>
        <el-tag
          :type="profile?.role === 'Admin' ? 'danger' : profile?.role === 'Teacher' ? 'warning' : 'info'"
          effect="plain"
        >
          {{ roleLabel }}
        </el-tag>
      </header>
      <el-form scroll-to-error label-position="top" class="entity-form" @submit.prevent="save">
        <div class="profile-form-grid">
          <el-form-item label="姓名" required>
            <el-input v-model="form.name" maxlength="100" show-word-limit placeholder="请输入姓名" />
          </el-form-item>
          <el-form-item
            :label="profile?.kind === 'admin' ? '工号/内部编号' : '学号/工号'"
            :required="profile?.kind !== 'admin'"
          >
            <el-input
              v-model="form.number"
              maxlength="64"
              show-word-limit
              :placeholder="profile?.kind === 'admin' ? '留空时自动生成内部编号' : '请输入学号/工号'"
            />
          </el-form-item>
          <el-form-item label="性别">
            <el-select v-model="form.gender" clearable placeholder="未填写">
              <el-option label="男" value="男" />
              <el-option label="女" value="女" />
              <el-option label="其他" value="其他" />
            </el-select>
          </el-form-item>
          <el-form-item label="手机号">
            <el-input v-model="form.phone" maxlength="30" placeholder="用于教学联系" />
          </el-form-item>
          <el-form-item label="邮箱" class="profile-wide">
            <el-input v-model="form.email" maxlength="254" placeholder="name@example.com" />
          </el-form-item>
        </div>
        <p class="field-help">学号/工号在同类身份中必须唯一，保存后立即同步到课堂与考试名单。</p>
      </el-form>
    </section>

    <aside class="surface profile-side">
      <el-icon class="side-icon"><UserFilled /></el-icon>
      <h2>账号安全</h2>
      <p>密码修改后，其他设备上的登录会话和终端连接会立即失效。</p>
      <el-button type="primary" plain :icon="Key" @click="emit('password')">修改登录密码</el-button>
      <el-divider />
      <dl class="profile-meta">
        <div>
          <dt>登录账号</dt>
          <dd>{{ profile?.userName || '—' }}</dd>
        </div>
        <div>
          <dt>身份</dt>
          <dd>{{ roleLabel }}</dd>
        </div>
        <div>
          <dt>资料范围</dt>
          <dd>{{ profile?.kind === 'admin' ? '管理员账号' : '课堂与考试名单' }}</dd>
        </div>
      </dl>
    </aside>
  </div>
</template>

<style scoped>
.profile-grid {
  display: grid;
  grid-template-columns: minmax(0, 1.7fr) minmax(260px, 0.8fr);
  gap: 18px;
  align-items: start;
}

.profile-card {
  padding: 22px;
}

.profile-card-heading {
  display: flex;
  align-items: center;
  gap: 14px;
  padding-bottom: 18px;
  border-bottom: 1px solid #dce7eb;
  margin-bottom: 18px;
}

.profile-card-heading > div {
  flex: 1;
  min-width: 0;
}

.profile-card-heading h2 {
  margin: 0;
  color: #173a58;
  font-size: 20px;
}

.profile-card-heading p {
  margin: 4px 0 0;
  color: #64788a;
  font-size: 12px;
}

.profile-avatar {
  width: 48px;
  height: 48px;
  border-radius: 14px;
  display: grid;
  place-items: center;
  background: linear-gradient(145deg, #164b4c, #1a7a6a);
  color: #fff;
  font-size: 20px;
  font-weight: 700;
}

.profile-card .entity-form {
  display: block;
}
.profile-form-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 0 16px;
}
.profile-wide {
  grid-column: 1 / -1;
}

.field-help {
  margin: 0;
  color: #64788a;
  font-size: 12px;
}

.profile-side {
  padding: 24px 22px;
}

.side-icon {
  font-size: 26px;
  color: var(--workspace-accent);
}

.profile-side h2 {
  margin: 10px 0 8px;
  color: #173a58;
  font-size: 18px;
}

.profile-side p {
  margin: 0 0 16px;
  color: #56697c;
  font-size: 13px;
  line-height: 1.7;
}

.profile-meta {
  margin: 0;
  display: grid;
  gap: 12px;
}

.profile-meta div {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  font-size: 12px;
}

.profile-meta dt {
  color: #64788a;
}

.profile-meta dd {
  margin: 0;
  color: #2a4a67;
  font-weight: 600;
}

@media (max-width: 900px) {
  .profile-grid {
    grid-template-columns: 1fr;
  }

  .profile-form-grid {
    grid-template-columns: 1fr;
  }
}
</style>

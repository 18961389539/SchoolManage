<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import { Delete, EditPen, Plus, Refresh, Search } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, dateTime, del, messageOf, post, put } from '../api'
import type { ClassroomGroup, ClassroomGroupListResponse, ClassroomStudent } from '../types'
import { focusFirstField } from '../focusField'

const props = defineProps<{ visible: boolean; classroomId: string; classroomName: string }>()
const emit = defineEmits<{ 'update:visible': [value: boolean] }>()

const groups = ref<ClassroomGroup[]>([])
const total = ref(0)
const roster = ref<ClassroomStudent[]>([])
const loading = ref(false)
const error = ref('')
const search = ref('')

const dialogVisible = ref(false)
const editing = ref<ClassroomGroup | null>(null)
const saving = ref(false)
const formError = ref('')
const form = reactive({ name: '', remark: '', userIds: [] as string[] })

const groupNameInput = ref<{ focus?: () => void; $el?: HTMLElement }>()
function focusGroupName() {
  const root = groupNameInput.value?.$el
  if (root) focusFirstField(root)
  else groupNameInput.value?.focus?.()
}

async function load() {
  if (!props.classroomId) return
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams()
    if (search.value.trim()) params.set('search', search.value.trim())
    const data = await api<ClassroomGroupListResponse>(
      `/classrooms/${props.classroomId}/groups${params.size ? `?${params.toString()}` : ''}`
    )
    groups.value = data.items
    total.value = data.total
  } catch (failure) {
    groups.value = []
    total.value = 0
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

async function loadRoster() {
  if (!props.classroomId) return
  try {
    const data = await api<{ items: ClassroomStudent[] }>(`/classrooms/${props.classroomId}/students`)
    roster.value = data.items
  } catch {
    roster.value = []
  }
}

function openCreate() {
  editing.value = null
  form.name = ''
  form.remark = ''
  form.userIds = []
  formError.value = ''
  dialogVisible.value = true
}

function openEdit(group: ClassroomGroup) {
  editing.value = group
  form.name = group.name
  form.remark = group.remark
  form.userIds = group.members.map(item => item.studentUserId)
  formError.value = ''
  dialogVisible.value = true
}

async function save() {
  if (!form.name.trim()) {
    formError.value = '请填写小组名称'
    return
  }
  saving.value = true
  formError.value = ''
  try {
    const payload = { name: form.name.trim(), remark: form.remark.trim(), userIds: form.userIds }
    if (editing.value) await put(`/classroom-groups/${editing.value.id}`, payload)
    else await post(`/classrooms/${props.classroomId}/groups`, payload)
    ElMessage.success(editing.value ? '小组已更新' : '小组已创建')
    dialogVisible.value = false
    await load()
  } catch (failure) {
    formError.value = messageOf(failure)
  } finally {
    saving.value = false
  }
}

async function remove(group: ClassroomGroup) {
  try {
    await ElMessageBox.confirm(`删除小组“${group.name}”不会影响学生学习记录，是否继续？`, '删除小组', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消'
    })
    await del(`/classroom-groups/${group.id}`)
    ElMessage.success('小组已删除')
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

watch(
  () => props.visible,
  visible => {
    if (visible) {
      search.value = ''
      void Promise.all([load(), loadRoster()])
    }
  }
)
</script>

<template>
  <el-drawer
    :model-value="visible"
    :title="`${classroomName || '课堂'} · 小组管理`"
    size="min(860px, 96vw)"
    @update:model-value="value => emit('update:visible', value)"
  >
    <div class="drawer-toolbar">
      <el-button type="primary" :icon="Plus" @click="openCreate">新建小组</el-button>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      <el-input
        v-model="search"
        class="search-box"
        clearable
        placeholder="搜索小组、成员姓名、学号或账号"
        :prefix-icon="Search"
        @keyup.enter="load"
        @clear="load"
      />
      <span class="record-count">共 {{ total }} 个小组</span>
    </div>
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
    <el-table v-loading="loading" :data="groups" row-key="id" empty-text="暂无小组，请先创建">
      <el-table-column type="expand">
        <template #default="{ row }">
          <div class="member-panel">
            <div class="member-head">
              <span>成员 {{ row.members.length }} 人</span>
              <span v-if="row.remark" class="member-remark">备注：{{ row.remark }}</span>
            </div>
            <el-table v-if="row.members.length" :data="row.members" size="small" row-key="id">
              <el-table-column prop="name" label="姓名" min-width="100" />
              <el-table-column prop="number" label="学号" min-width="120" />
              <el-table-column prop="className" label="班级" min-width="140" />
              <el-table-column prop="userName" label="账号" min-width="120" />
            </el-table>
            <p v-else class="empty-member">暂未选择成员</p>
          </div>
        </template>
      </el-table-column>
      <el-table-column prop="name" label="小组" min-width="150" />
      <el-table-column label="成员" width="90">
        <template #default="{ row }">{{ row.members.length }} 人</template>
      </el-table-column>
      <el-table-column prop="remark" label="备注" min-width="180" show-overflow-tooltip />
      <el-table-column label="创建时间" min-width="160">
        <template #default="{ row }">{{ dateTime(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column label="操作" width="150" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" :icon="EditPen" @click="openEdit(row as ClassroomGroup)">编辑</el-button>
          <el-button link type="danger" :icon="Delete" @click="remove(row as ClassroomGroup)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog
      v-model="dialogVisible"
      :title="editing ? '编辑小组' : '新建小组'"
      width="min(620px, 94vw)"
      append-to-body
      @opened="focusGroupName"
    >
      <el-alert v-if="formError" :title="formError" type="error" show-icon :closable="false" class="section-notice" />
      <el-form label-position="top" class="entity-form">
        <el-form-item label="小组名称" required>
          <el-input
            ref="groupNameInput"
            v-model="form.name"
            maxlength="100"
            show-word-limit
            placeholder="例如：第一组"
          />
        </el-form-item>
        <el-form-item label="备注">
          <el-input v-model="form.remark" type="textarea" :rows="2" maxlength="500" show-word-limit />
        </el-form-item>
        <el-form-item label="成员">
          <el-select v-model="form.userIds" multiple filterable collapse-tags placeholder="从本课堂学生中选择">
            <el-option
              v-for="student in roster"
              :key="student.userId"
              :label="`${student.name} · ${student.number || student.userName} · ${student.className}`"
              :value="student.userId"
            />
          </el-select>
          <p class="field-help">仅可选择当前课堂学生，已选 {{ form.userIds.length }} 人。</p>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">{{ editing ? '保存修改' : '创建小组' }}</el-button>
      </template>
    </el-dialog>
  </el-drawer>
</template>

<style scoped>
.member-panel {
  padding: 10px 16px 14px;
  background: #f6faf8;
}

.member-head {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 8px;
  margin-bottom: 8px;
  color: #51687f;
  font-size: 12px;
}

.member-remark {
  color: #64788a;
}

.empty-member {
  margin: 0;
  color: #64788a;
  font-size: 12px;
}
</style>

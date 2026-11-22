<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import { Connection, Delete, Plus, Refresh, Search } from '@element-plus/icons-vue'
import { focusFirstField } from '../focusField'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, dateTime, messageOf, post } from '../api'
import type {
  ClassroomCabinetAssignment,
  ClassroomCabinetAssignmentList,
  ClassroomCabinetProbe,
  ClassroomCollaborationOptions
} from '../types'

const props = defineProps<{ visible: boolean; classroomId: string; classroomName: string }>()
const emit = defineEmits<{ 'update:visible': [value: boolean] }>()

const options = ref<ClassroomCollaborationOptions>({ groups: [], resources: [], cabinets: [] })
const assignments = ref<ClassroomCabinetAssignment[]>([])
const total = ref(0)
const loading = ref(false)
const error = ref('')
const search = ref('')
const selected = ref<ClassroomCabinetAssignment[]>([])
const testing = ref(false)
const probeVisible = ref(false)
const probes = ref<ClassroomCabinetProbe[]>([])

const dialogVisible = ref(false)
const saving = ref(false)
const formError = ref('')
const form = reactive({ groupId: '', resourceId: '', cabinetIds: [] as string[] })

const assignForm = ref<{ $el?: HTMLElement }>()
function focusAssignForm() {
  focusFirstField(assignForm.value?.$el)
}
function assignCabinetReason(): string | undefined {
  if (!options.value.groups.length && !options.value.resources.length) {
    return '请先创建课堂小组，并在课程中配置案例实操题目后再分配机柜'
  }
  if (!options.value.groups.length) return '请先在“小组管理”中创建小组后再分配机柜'
  if (!options.value.resources.length) return '请先在课程中配置案例实操题目后再分配机柜'
}
function cabinetSelectionReason(action: string): string | undefined {
  if (!selected.value.length) return `请先勾选要${action}的机柜`
}

async function loadOptions() {
  if (!props.classroomId) return
  try {
    options.value = await api<ClassroomCollaborationOptions>(`/classrooms/${props.classroomId}/cabinet-options`)
  } catch (failure) {
    options.value = { groups: [], resources: [], cabinets: [] }
    error.value = messageOf(failure)
  }
}

async function loadAssignments() {
  if (!props.classroomId) return
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams()
    if (search.value.trim()) params.set('search', search.value.trim())
    const data = await api<ClassroomCabinetAssignmentList>(
      `/classrooms/${props.classroomId}/cabinet-assignments${params.size ? `?${params.toString()}` : ''}`
    )
    assignments.value = data.items
    total.value = data.total
  } catch (failure) {
    assignments.value = []
    total.value = 0
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

async function load() {
  await Promise.all([loadOptions(), loadAssignments()])
}

function openAssign() {
  form.groupId = options.value.groups[0]?.id || ''
  form.resourceId = options.value.resources[0]?.id || ''
  form.cabinetIds = []
  formError.value = ''
  dialogVisible.value = true
}

async function assign() {
  if (!form.groupId) {
    formError.value = '请选择小组'
    return
  }
  if (!form.resourceId) {
    formError.value = '请选择案例实操题目'
    return
  }
  if (!form.cabinetIds.length) {
    formError.value = '请选择至少一个机柜'
    return
  }
  saving.value = true
  formError.value = ''
  try {
    const result = await api<{ assigned: number }>(`/classrooms/${props.classroomId}/cabinet-assignments`, {
      method: 'POST',
      body: JSON.stringify(form)
    })
    ElMessage.success(`已分配 ${result.assigned} 个机柜`)
    dialogVisible.value = false
    await load()
  } catch (failure) {
    formError.value = messageOf(failure)
  } finally {
    saving.value = false
  }
}

async function cancelSelected() {
  if (!selected.value.length) {
    ElMessage.warning('请先选择机柜分配')
    return
  }
  try {
    await ElMessageBox.confirm(`将取消 ${selected.value.length} 个机柜分配，是否继续？`, '取消分配', {
      type: 'warning',
      confirmButtonText: '取消分配',
      cancelButtonText: '返回'
    })
    await post(`/classrooms/${props.classroomId}/cabinet-assignments/cancel`, {
      ids: selected.value.map(item => item.id)
    })
    ElMessage.success('分配已取消')
    selected.value = []
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') ElMessage.error(messageOf(failure))
  }
}

async function testSelected() {
  if (!selected.value.length) {
    ElMessage.warning('请先选择机柜分配')
    return
  }
  testing.value = true
  try {
    const data = await api<{ items: ClassroomCabinetProbe[] }>(
      `/classrooms/${props.classroomId}/cabinet-assignments/test`,
      { method: 'POST', body: JSON.stringify({ ids: selected.value.map(item => item.id) }) }
    )
    probes.value = data.items
    probeVisible.value = true
    const failed = data.items.filter(item => !item.success).length
    if (failed) ElMessage.warning(`${failed} 个机柜连接异常`)
    else ElMessage.success('全部机柜连接正常')
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    testing.value = false
  }
}

async function reclaim(row: ClassroomCabinetAssignment) {
  try {
    await ElMessageBox.confirm(
      `回收机柜“${row.name}”将取消分配并释放该机柜上的终端会话，不会重置设备配置。是否继续？`,
      '回收机柜',
      { type: 'warning', confirmButtonText: '回收', cancelButtonText: '取消' }
    )
    const result = await post<{ releasedSessions: number; message: string }>(
      `/classroom-cabinet-assignments/${row.id}/reclaim`
    )
    ElMessage.success(result.message)
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
      selected.value = []
      void load()
    }
  }
)
</script>

<template>
  <el-drawer
    :model-value="visible"
    :title="`${classroomName || '课堂'} · 分配机柜`"
    size="min(980px, 96vw)"
    @update:model-value="value => emit('update:visible', value)"
  >
    <div class="drawer-toolbar">
      <el-tooltip :disabled="!assignCabinetReason()" :content="assignCabinetReason()" placement="bottom">
        <span class="disabled-action-wrap">
          <el-button
            type="primary"
            :icon="Plus"
            :disabled="!options.groups.length || !options.resources.length"
            @click="openAssign"
          >
            分配机柜
          </el-button>
        </span>
      </el-tooltip>
      <el-tooltip
        :disabled="!cabinetSelectionReason('测试')"
        :content="cabinetSelectionReason('测试')"
        placement="bottom"
      >
        <span class="disabled-action-wrap">
          <el-button :icon="Connection" :loading="testing" :disabled="!selected.length" @click="testSelected"
            >批量测试</el-button
          >
        </span>
      </el-tooltip>
      <el-tooltip
        :disabled="!cabinetSelectionReason('取消分配')"
        :content="cabinetSelectionReason('取消分配')"
        placement="bottom"
      >
        <span class="disabled-action-wrap">
          <el-button type="danger" plain :icon="Delete" :disabled="!selected.length" @click="cancelSelected"
            >取消分配</el-button
          >
        </span>
      </el-tooltip>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      <el-input
        v-model="search"
        class="search-box"
        clearable
        placeholder="搜索机柜、主机、小组或题目"
        :prefix-icon="Search"
        @keyup.enter="loadAssignments"
        @clear="loadAssignments"
      />
      <span class="record-count">共 {{ total }} 个机柜</span>
    </div>
    <el-alert
      v-if="!options.groups.length || !options.resources.length"
      title="请先在“小组管理”创建小组，并在课程中配置案例实操题目后再分配机柜。"
      type="info"
      show-icon
      :closable="false"
      class="section-notice"
    />
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />
    <el-table
      v-loading="loading"
      :data="assignments"
      row-key="id"
      empty-text="暂无机柜分配"
      @selection-change="(rows: unknown) => (selected = rows as ClassroomCabinetAssignment[])"
    >
      <el-table-column type="selection" width="46" />
      <el-table-column label="机柜" min-width="160">
        <template #default="{ row }">
          <div class="table-name">
            <strong>{{ row.name }}</strong>
            <el-tag v-if="row.isSimulated" size="small" type="info" effect="plain">模拟</el-tag>
          </div>
          <p class="classroom-subline">{{ row.host }}:{{ row.port }}</p>
        </template>
      </el-table-column>
      <el-table-column label="跳板" min-width="140">
        <template #default="{ row }">
          <span v-if="row.jumpHost">{{ row.jumpHost }}:{{ row.jumpPort }}</span>
          <span v-else class="muted">不经过跳板</span>
        </template>
      </el-table-column>
      <el-table-column prop="groupName" label="小组" min-width="110" />
      <el-table-column prop="questionName" label="案例实操题目" min-width="180" show-overflow-tooltip />
      <el-table-column label="分配时间" min-width="160">
        <template #default="{ row }">{{ dateTime(row.assignedAt) }}</template>
      </el-table-column>
      <el-table-column label="操作" width="90" fixed="right">
        <template #default="{ row }">
          <el-button link type="danger" @click="reclaim(row as ClassroomCabinetAssignment)">回收</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog
      v-model="dialogVisible"
      title="分配机柜"
      width="min(620px, 94vw)"
      append-to-body
      @opened="focusAssignForm"
    >
      <el-alert v-if="formError" :title="formError" type="error" show-icon :closable="false" class="section-notice" />
      <el-form scroll-to-error ref="assignForm" label-position="top" class="entity-form">
        <el-form-item label="小组" required>
          <el-select v-model="form.groupId" filterable placeholder="选择本课堂小组">
            <el-option v-for="group in options.groups" :key="group.id" :label="group.name" :value="group.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="案例实操题目" required>
          <el-select v-model="form.resourceId" filterable placeholder="选择本课程中的案例实操题目">
            <el-option
              v-for="resource in options.resources"
              :key="resource.id"
              :label="resource.name"
              :value="resource.id"
            />
          </el-select>
        </el-form-item>
        <el-form-item label="机柜" required>
          <el-select v-model="form.cabinetIds" multiple filterable collapse-tags placeholder="选择空闲机柜">
            <el-option
              v-for="cabinet in options.cabinets"
              :key="cabinet.id"
              :label="`${cabinet.name} · ${cabinet.host}:${cabinet.port}${cabinet.assigned ? `（已分配给 ${cabinet.assignedClassroomName} · ${cabinet.assignedGroupName}）` : ''}`"
              :value="cabinet.id"
              :disabled="cabinet.assigned"
            />
          </el-select>
          <p class="field-help">机柜为全局独占分配，已分配给其他课堂或小组的机柜不可选择。</p>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="assign">确认分配</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="probeVisible" title="机柜连接测试结果" width="min(620px, 94vw)" append-to-body>
      <el-table :data="probes" row-key="assignmentId">
        <el-table-column prop="name" label="机柜" min-width="140" />
        <el-table-column label="结果" width="100">
          <template #default="{ row }">
            <el-tag :type="row.success ? 'success' : 'danger'" size="small">{{ row.success ? '正常' : '异常' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="模拟" width="80">
          <template #default="{ row }">{{ row.isSimulated ? '是' : '否' }}</template>
        </el-table-column>
        <el-table-column prop="message" label="说明" min-width="220" show-overflow-tooltip />
      </el-table>
      <template #footer>
        <el-button type="primary" @click="probeVisible = false">关闭</el-button>
      </template>
    </el-dialog>
  </el-drawer>
</template>

<style scoped>
.muted {
  color: #64788a;
  font-size: 12px;
}
</style>

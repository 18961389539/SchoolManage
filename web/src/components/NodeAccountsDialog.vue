<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage, ElMessageBox, type TableInstance } from 'element-plus'
import { Plus, Refresh, Search } from '@element-plus/icons-vue'
import { api, post, del, dateTime, messageOf, type Row } from '../api'
import type { AccountAssignment, AccountCandidate, NodeAccountsResponse, Paged } from '../types'

const props = defineProps<{ node: Row | null }>()
const emit = defineEmits<{ close: []; changed: [] }>()
const visible = computed({
  get: () => !!props.node,
  set: value => {
    if (!value) emit('close')
  }
})
const nodeInfo = ref<Row>({})
const candidatesRef = ref<TableInstance>()
const assigned = ref<AccountAssignment[]>([]),
  candidates = ref<AccountCandidate[]>([]),
  selected = ref<AccountCandidate[]>([])
const assignedSearch = ref(''),
  candidateSearch = ref(''),
  candidateKind = ref('')
const assignedPage = ref(1),
  candidatePage = ref(1),
  assignedTotal = ref(0),
  candidateTotal = ref(0)
const loadingAssigned = ref(false),
  loadingCandidates = ref(false),
  saving = ref(false)
const assignedError = ref(''),
  candidateError = ref(''),
  actionError = ref('')
const activeTab = ref('assigned')
let assignedVersion = 0,
  candidateVersion = 0
const accountCount = computed(() => Number(nodeInfo.value.accountCount ?? props.node?.accountCount ?? 0))
const accountQuota = computed(() => Number(nodeInfo.value.accountQuota ?? props.node?.accountQuota ?? 0))
const available = computed(() => Math.max(0, accountQuota.value - accountCount.value))
const enabled = computed(() => nodeInfo.value.enabled ?? props.node?.enabled ?? false)
const maxAssignmentBatch = 500
const canAssign = computed(
  () =>
    enabled.value &&
    selected.value.length > 0 &&
    selected.value.length <= Math.min(available.value, maxAssignmentBatch) &&
    !saving.value
)
const nameOfKind = (kind: string) => (kind === 'teacher' ? '教师' : kind === 'student' ? '学生' : kind)
function resetAssigned() {
  assignedPage.value = 1
  void loadAssigned()
}

function resetCandidates() {
  candidatePage.value = 1
  clearCandidateSelection()
  void loadCandidates()
}

// Selection is reserved across pages so one assignment can cover several pages; a new search
// or node starts from a clean selection.
function clearCandidateSelection() {
  selected.value = []
  candidatesRef.value?.clearSelection()
}

function setSelection(value: AccountCandidate[]) {
  selected.value = value
}

async function loadAssigned() {
  if (!props.node) return
  const version = ++assignedVersion,
    id = props.node.id
  loadingAssigned.value = true
  assignedError.value = ''
  try {
    const query = new URLSearchParams({
      search: assignedSearch.value,
      page: String(assignedPage.value),
      pageSize: '10'
    })
    const result = await api<NodeAccountsResponse>(`/nodes/${encodeURIComponent(id)}/accounts?${query}`)
    if (version !== assignedVersion || props.node?.id !== id) return
    assigned.value = result.items
    assignedTotal.value = result.total
    nodeInfo.value = {
      ...nodeInfo.value,
      accountQuota: result.accountQuota,
      accountCount: result.accountCount,
      enabled: result.enabled
    }
    if (!assigned.value.length && assignedPage.value > 1) {
      assignedPage.value--
      await loadAssigned()
    }
  } catch (error) {
    if (version === assignedVersion) {
      assignedError.value = messageOf(error)
      assigned.value = []
      assignedTotal.value = 0
    }
  } finally {
    if (version === assignedVersion) loadingAssigned.value = false
  }
}
async function loadCandidates() {
  if (!props.node) return
  const version = ++candidateVersion,
    id = props.node.id
  loadingCandidates.value = true
  candidateError.value = ''
  try {
    const query = new URLSearchParams({
      search: candidateSearch.value,
      page: String(candidatePage.value),
      pageSize: '10'
    })
    if (candidateKind.value) query.set('kind', candidateKind.value)
    const result = await api<Paged<AccountCandidate>>(`/nodes/${encodeURIComponent(id)}/account-candidates?${query}`)
    if (version !== candidateVersion || props.node?.id !== id) return
    candidates.value = result.items
    candidateTotal.value = result.total
    if (!candidates.value.length && candidatePage.value > 1) {
      candidatePage.value--
      await loadCandidates()
    }
  } catch (error) {
    if (version === candidateVersion) {
      candidateError.value = messageOf(error)
      candidates.value = []
      candidateTotal.value = 0
    }
  } finally {
    if (version === candidateVersion) loadingCandidates.value = false
  }
}
async function refresh() {
  await Promise.all([loadAssigned(), loadCandidates()])
}
async function assign() {
  if (!props.node || !canAssign.value) return
  saving.value = true
  actionError.value = ''
  try {
    const count = selected.value.length
    await post(`/nodes/${encodeURIComponent(props.node.id)}/accounts`, {
      userIds: selected.value.map(person => person.userId)
    })
    clearCandidateSelection()
    ElMessage.success(`已分配 ${count} 个账号`)
    emit('changed')
    await refresh()
  } catch (error) {
    actionError.value = messageOf(error)
    await loadAssigned()
  } finally {
    saving.value = false
  }
}
// Slot rows arrive as Element Plus DefaultRow; the collection itself stays typed.
async function unassign(person: Row) {
  if (!props.node) return
  const nodeId = props.node.id
  try {
    await ElMessageBox.confirm(
      `确认解除“${person.name}（${person.userName}）”与当前节点的关联？此操作不会删除师生档案或登录账号。`,
      '移除节点账号分配',
      { type: 'warning', confirmButtonText: '移除分配', cancelButtonText: '取消' }
    )
    if (props.node?.id !== nodeId) return
    saving.value = true
    actionError.value = ''
    await del(`/nodes/${encodeURIComponent(nodeId)}/accounts/${encodeURIComponent(person.userId)}`)
    ElMessage.success('已解除节点关联')
    emit('changed')
    await refresh()
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') {
      actionError.value = messageOf(error)
      await loadAssigned()
    }
  } finally {
    saving.value = false
  }
}
watch(
  () => props.node?.id,
  id => {
    assignedVersion++
    candidateVersion++
    assigned.value = []
    candidates.value = []
    clearCandidateSelection()
    assignedSearch.value = ''
    candidateSearch.value = ''
    candidateKind.value = ''
    assignedPage.value = 1
    candidatePage.value = 1
    activeTab.value = 'assigned'
    assignedError.value = ''
    candidateError.value = ''
    actionError.value = ''
    nodeInfo.value = props.node ? { ...props.node } : {}
    if (id) void refresh()
  },
  { immediate: true }
)
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="`${node?.name || '节点'} · 分配账号`"
    width="min(1100px, 96vw)"
    :close-on-click-modal="false"
    class="node-accounts-dialog"
  >
    <el-alert
      title="维护师生账号与节点的资源使用关联，不创建操作系统账号；移除分配不会删除师生档案。"
      type="info"
      :closable="false"
      show-icon
      class="section-notice"
    />
    <div class="node-account-summary">
      <div>
        <el-tag v-if="node?.isSimulated || node?.provider === 'simulated'" type="warning">模拟节点</el-tag
        ><el-tag :type="enabled ? 'success' : 'info'">{{ enabled ? '允许分配' : '已停用' }}</el-tag>
      </div>
      <div>
        <span
          >已分配 <strong>{{ accountCount }}</strong></span
        ><span
          >账号配额 <strong>{{ accountQuota }}</strong></span
        ><span
          >剩余名额 <strong>{{ available }}</strong></span
        >
      </div>
      <el-button :icon="Refresh" :disabled="saving" @click="refresh">刷新</el-button>
    </div>
    <el-alert
      v-if="!enabled"
      title="当前节点已停用，暂停新增分配；仍可移除已有分配。请先在节点列表启用后再添加账号。"
      type="warning"
      :closable="false"
      show-icon
      class="section-notice"
    />
    <el-alert
      v-else-if="available === 0"
      :title="
        accountQuota === 0
          ? '账号配额为 0，当前不接受新增分配。可在节点编辑中调整配额。'
          : '账号配额已用满。请调整节点配额或先移除不再使用的账号。'
      "
      type="warning"
      :closable="false"
      show-icon
      class="section-notice"
    />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon class="section-notice" />
    <el-tabs v-model="activeTab" class="node-account-tabs">
      <el-tab-pane :label="`已分配账号（${accountCount}）`" name="assigned">
        <section class="node-account-panel">
          <div class="node-account-panel-heading">
            <div>
              <h3>已分配账号</h3>
              <p>当前节点的师生使用名单</p>
            </div>
            <span class="record-count">匹配 {{ assignedTotal }} 项</span>
          </div>
          <div class="node-account-search">
            <el-input
              v-model="assignedSearch"
              aria-label="搜索已分配的师生账号"
              clearable
              :prefix-icon="Search"
              placeholder="搜索姓名、账号或学号 / 工号"
              @keyup.enter="resetAssigned"
              @clear="resetAssigned"
            /><el-button @click="resetAssigned">搜索</el-button>
          </div>
          <el-alert
            v-if="assignedError"
            :title="assignedError"
            type="error"
            :closable="false"
            show-icon
            class="node-account-error"
          />
          <el-table
            :data="assigned"
            row-key="userId"
            v-loading="loadingAssigned"
            max-height="340"
            :empty-text="assignedError ? '列表加载失败，请刷新重试' : '当前节点尚未分配账号'"
          >
            <el-table-column label="姓名 / 账号" min-width="160"
              ><template #default="{ row }"
                ><span class="table-name">{{ row.name }}</span>
                <div class="account-secondary">{{ row.userName }}</div></template
              ></el-table-column
            >
            <el-table-column label="身份 / 学号工号" min-width="140"
              ><template #default="{ row }"
                >{{ nameOfKind(row.kind) }}
                <div class="account-secondary">{{ row.number }}</div></template
              ></el-table-column
            >
            <el-table-column label="分配时间（北京时间）" min-width="185"
              ><template #default="{ row }">{{ dateTime(row.assignedAt) }}</template></el-table-column
            >
            <el-table-column label="操作" width="100" fixed="right"
              ><template #default="{ row }"
                ><el-button link type="danger" :disabled="saving" @click="unassign(row)">移除</el-button></template
              ></el-table-column
            >
          </el-table>
          <div class="node-account-pagination">
            <el-pagination
              v-model:current-page="assignedPage"
              :page-size="10"
              :total="assignedTotal"
              layout="prev,pager,next"
              @change="loadAssigned"
            />
          </div>
        </section>
      </el-tab-pane>
      <el-tab-pane label="添加师生账号" name="candidates">
        <section class="node-account-panel">
          <div class="node-account-panel-heading">
            <div>
              <h3>添加师生账号</h3>
              <p>仅显示尚未分配到本节点的师生</p>
            </div>
            <span class="record-count">匹配 {{ candidateTotal }} 项</span>
          </div>
          <div class="node-account-search candidate-search">
            <el-input
              v-model="candidateSearch"
              aria-label="搜索可分配的师生账号"
              clearable
              :prefix-icon="Search"
              placeholder="搜索姓名、账号或学号 / 工号"
              @keyup.enter="resetCandidates"
              @clear="resetCandidates"
            /><el-select v-model="candidateKind" aria-label="筛选师生类型" @change="resetCandidates"
              ><el-option label="全部师生" value="" /><el-option label="教师" value="teacher" /><el-option
                label="学生"
                value="student" /></el-select
            ><el-button @click="resetCandidates">搜索</el-button>
          </div>
          <el-alert
            v-if="candidateError"
            :title="candidateError"
            type="error"
            :closable="false"
            show-icon
            class="node-account-error"
          />
          <el-table
            ref="candidatesRef"
            :data="candidates"
            row-key="userId"
            v-loading="loadingCandidates"
            max-height="340"
            :empty-text="
              candidateError ? '候选账号加载失败，请刷新重试' : '暂无匹配的未分配师生，请调整搜索或先创建档案'
            "
            @selection-change="setSelection"
          >
            <el-table-column
              type="selection"
              width="45"
              reserve-selection
              :selectable="() => enabled && !saving && available > 0"
            />
            <el-table-column label="姓名 / 账号" min-width="170"
              ><template #default="{ row }"
                ><span class="table-name">{{ row.name }}</span>
                <div class="account-secondary">{{ row.userName }}</div></template
              ></el-table-column
            >
            <el-table-column label="身份" width="85"
              ><template #default="{ row }">{{ nameOfKind(row.kind) }}</template></el-table-column
            >
            <el-table-column prop="number" label="学号 / 工号" min-width="145" />
          </el-table>
          <div class="node-account-pagination">
            <span class="account-secondary">已选 {{ selected.length }} 项</span
            ><el-pagination
              v-model:current-page="candidatePage"
              :page-size="10"
              :total="candidateTotal"
              layout="prev,pager,next"
              @change="loadCandidates"
            />
          </div>
          <div class="node-account-add">
            <span v-if="selected.length > maxAssignmentBatch" class="selection-warning"
              >每批最多分配 {{ maxAssignmentBatch }} 个账号，请分批提交</span
            ><span v-else-if="selected.length > available" class="selection-warning"
              >已选数量超过剩余 {{ available }} 个名额</span
            ><el-button type="primary" :icon="Plus" :disabled="!canAssign" :loading="saving" @click="assign"
              >分配选中账号{{ selected.length ? `（${selected.length}）` : '' }}</el-button
            >
          </div>
        </section>
      </el-tab-pane>
    </el-tabs>
    <template #footer><el-button @click="visible = false">关闭</el-button></template>
  </el-dialog>
</template>

<style scoped>
:global(.node-accounts-dialog.el-dialog) {
  margin: 4vh auto !important;
  max-height: 92dvh;
  display: flex;
  flex-direction: column;
}
:global(.node-accounts-dialog .el-dialog__header),
:global(.node-accounts-dialog .el-dialog__footer) {
  flex: 0 0 auto;
}
:global(.node-accounts-dialog .el-dialog__body) {
  overflow: auto;
  min-height: 0;
}
.node-account-tabs :deep(.el-tabs__header) {
  margin-bottom: 18px;
}

.node-account-summary {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 20px;
  padding: 0 0 20px;
  flex-wrap: wrap;
}
.node-account-summary > div {
  display: flex;
  align-items: center;
  gap: 15px;
}
.node-account-summary > div:nth-child(2) {
  font-size: 14px;
  color: #7b8c9d;
}
.node-account-summary strong {
  font-size: 20px;
  color: #3f6177;
  margin-left: 5px;
}
.node-account-panels {
  display: grid;
  grid-template-columns: 1fr;
  gap: 22px;
}
.node-account-panel {
  border: 1px solid #e5eaf1;
  border-radius: 9px;
  overflow: hidden;
}
.node-account-panel-heading {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 15px;
  padding: 18px 20px 14px;
}
.node-account-panel-heading h3 {
  font-size: 16px;
  margin: 0 0 5px;
  color: #38566e;
}
.node-account-panel-heading p {
  font-size: 12px;
  margin: 0;
  color: #93a0b1;
}
.node-account-search {
  display: flex;
  gap: 10px;
  align-items: center;
  padding: 0 20px 17px;
}
.candidate-search > .el-select {
  width: 120px;
  flex-shrink: 0;
}
.node-account-error {
  width: auto;
  margin: 0 20px 15px;
}
.account-secondary {
  font-size: 12px;
  color: #8b9cad;
  line-height: 1.8;
}
.node-account-pagination {
  display: flex;
  justify-content: flex-end;
  align-items: center;
  gap: 20px;
  padding: 12px 18px;
}
.node-account-pagination > span {
  margin-right: auto;
}
.node-account-add {
  display: flex;
  justify-content: flex-end;
  align-items: center;
  gap: 15px;
  border-top: 1px solid #edf1f5;
  padding: 14px 20px;
}
.selection-warning {
  color: #b86553;
  font-size: 13px;
}
@media (max-width: 650px) {
  .node-account-summary {
    gap: 14px;
  }
  .node-account-summary > div:nth-child(2) {
    order: 3;
    width: 100%;
    justify-content: space-between;
    font-size: 12px;
  }
  .node-account-summary strong {
    font-size: 17px;
  }
  .node-account-search {
    padding-left: 14px;
    padding-right: 14px;
    flex-wrap: wrap;
  }
  .candidate-search > .el-input {
    width: 100%;
    flex: none;
  }
  .candidate-search > .el-select {
    flex: 1;
  }
  .node-account-panel-heading {
    padding-left: 14px;
    padding-right: 14px;
  }
  .node-account-panel-heading .record-count {
    display: none;
  }
  .node-account-pagination {
    padding-left: 10px;
    padding-right: 10px;
    gap: 5px;
  }
  .node-account-add {
    flex-wrap: wrap;
  }
  .node-account-add > .el-button {
    width: 100%;
  }
}
</style>

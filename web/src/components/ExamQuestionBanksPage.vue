<script setup lang="ts">
import { computed, defineAsyncComponent, onMounted, reactive, ref } from 'vue'
import { Delete, EditPen, Plus, Refresh, Search, View } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, dateTime, del, messageOf, post, put } from '../api'
import { readListState, useListPersist } from '../listState'
import { recoverPage } from '../paging'
import { examQuestionBankLabels } from '../questionBanks'
import { contentVisibilityOptions, visibilityLabel } from '../teaching'
import { focusFirstField } from '../focusField'
import type { ContentVisibility, ExamQuestionBank, ExamQuestionBankType, Paged } from '../types'
import { useDiscardGuard } from '../discardGuard'
import TableEmpty from './TableEmpty.vue'

const QuestionBankPage = defineAsyncComponent(() => import('./QuestionBankPage.vue'))
const props = defineProps<{ readOnly?: boolean; admin?: boolean }>()
const canWrite = computed(() => !props.readOnly)
const visibilityChoices = computed(() =>
  props.admin ? contentVisibilityOptions : contentVisibilityOptions.filter(item => item.value !== 'published')
)
const filtering = computed(() => !!search.value.trim() || !!type.value)
const banks = ref<ExamQuestionBank[]>([])
const total = ref(0)
const listKey = 'exam-question-banks'
const savedList = readListState(listKey)
const page = ref(savedList.page)
const size = ref(savedList.pageSize)
const search = ref(savedList.search)
const type = ref(savedList.filters.type || '')
const loading = ref(false)
const error = ref('')
const activeBank = ref<ExamQuestionBank | null>(null)
const editor = ref(false)
const bankNameInput = ref<{ focus?: () => void; $el?: HTMLElement }>()
function focusBankName() {
  const root = bankNameInput.value?.$el
  if (root) focusFirstField(root)
  else bankNameInput.value?.focus?.()
}
const editing = ref<ExamQuestionBank | null>(null)
const form = reactive({ name: '', type: 'theory' as ExamQuestionBankType, visibility: 'private' as ContentVisibility })
const guard = useDiscardGuard({
  visible: editor,
  snapshot: () => form,
  message: '题库信息还没有保存，关闭后不会保留。',
  busy: () => saving.value
})
const formError = ref('')
const saving = ref(false)
const deleting = ref('')
let loadVersion = 0

async function load() {
  loading.value = true
  error.value = ''
  const version = ++loadVersion
  const params = new URLSearchParams({
    page: String(page.value),
    pageSize: String(size.value),
    search: search.value.trim(),
    type: type.value
  })
  try {
    const result = await api<Paged<ExamQuestionBank>>(`/exam-question-banks?${params}`)
    if (version !== loadVersion) return
    banks.value = result.items
    total.value = result.total
    const recovered = recoverPage(page.value, size.value, total.value, banks.value.length)
    if (recovered !== null && recovered !== page.value) {
      page.value = recovered
      return load()
    }
  } catch (failure) {
    if (version === loadVersion) error.value = messageOf(failure)
  } finally {
    if (version === loadVersion) loading.value = false
  }
}

useListPersist(listKey, () => ({
  search: search.value,
  filters: { type: type.value },
  page: page.value,
  pageSize: size.value
}))

function resetBankFilters() {
  search.value = ''
  type.value = ''
  filter()
}

function filter() {
  page.value = 1
  void load()
}

function edit(bank: ExamQuestionBank | null = null) {
  editing.value = bank
  form.name = bank?.name ?? ''
  form.type = bank?.type ?? 'theory'
  form.visibility = bank?.visibility ?? (props.admin ? 'published' : 'private')
  formError.value = ''
  guard.capture()
  editor.value = true
}

async function save() {
  if (saving.value) return
  formError.value = ''
  const name = form.name.trim()
  if (!name) {
    formError.value = '请填写题库名称。'
    return
  }
  saving.value = true
  try {
    const payload = { name, type: form.type, visibility: form.visibility }
    if (editing.value) await put(`/exam-question-banks/${editing.value.id}`, payload)
    else {
      await post('/exam-question-banks', payload)
      page.value = 1
      search.value = ''
      type.value = ''
    }
    editor.value = false
    ElMessage.success('题库已保存')
    await load()
  } catch (failure) {
    formError.value = messageOf(failure)
  } finally {
    saving.value = false
  }
}

async function remove(bank: ExamQuestionBank) {
  if (deleting.value) return
  try {
    await ElMessageBox.confirm(`确定删除空题库“${bank.name}”吗？`, '删除题库', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消'
    })
  } catch {
    return
  }
  deleting.value = bank.id
  try {
    await del(`/exam-question-banks/${bank.id}`)
    if (banks.value.length === 1 && page.value > 1) page.value--
    ElMessage.success('题库已删除')
    await load()
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    deleting.value = ''
  }
}

function back() {
  activeBank.value = null
  void load()
}

onMounted(load)
</script>

<template>
  <QuestionBankPage v-if="activeBank" :key="activeBank.id" :bank="activeBank" :read-only="!activeBank.canEdit" @back="back" />
  <template v-else>
    <section class="page-heading">
      <div>
        <h1>{{ canWrite ? '题库管理' : '题库' }}</h1>
        <p v-if="canWrite">可以新建本人题库并录入题目。公共标准库由管理员维护，其中的题目可以直接用于组卷。</p>
        <p v-else>只读查看管理员已配置的题库和题目，不能改题或删除。</p>
      </div>
      <div class="heading-actions">
        <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
        <el-button v-if="canWrite" type="primary" :icon="Plus" @click="edit()">新建题库</el-button>
      </div>
    </section>
    <section class="surface">
      <div class="table-toolbar">
        <div class="table-controls">
          <el-input
            v-model="search"
            class="search-box"
            placeholder="搜索题库名称"
            :prefix-icon="Search"
            clearable
            @keyup.enter="filter"
            @clear="filter"
          />
          <el-select
            v-model="type"
            class="table-filter"
            placeholder="全部题库类型"
            clearable
            value-on-clear=""
            @change="filter"
          >
            <el-option v-for="(label, key) in examQuestionBankLabels" :key="key" :label="label" :value="key" />
          </el-select>
          <el-button type="primary" :icon="Search" @click="filter">查询</el-button>
          <el-button @click="resetBankFilters">重置</el-button>
        </div>
        <span class="record-count">共 {{ total }} 个题库</span>
      </div>
      <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="table-error" />
      <el-table v-loading="loading" :data="banks" row-key="id" empty-text="">
        <template #empty>
          <TableEmpty
            v-if="!loading"
            :title="error ? '题库加载失败' : filtering ? '没有符合条件的题库' : '暂无题库'"
            :hint="
              error
                ? '请检查服务状态后刷新重试'
                : filtering
                  ? '试试调整名称或类型筛选，也可清除后查看全部题库。'
                  : canWrite
                    ? '点击右上角「新建题库」开始维护题目。'
                    : '暂无题库。请联系管理员维护题库后再查看。'
            "
          >
            <template v-if="!error && filtering" #action>
              <el-button type="primary" plain @click="resetBankFilters">清除筛选</el-button>
            </template>
          </TableEmpty>
        </template>
        <el-table-column label="题库名称" min-width="190">
          <template #default="{ row }">
            <strong>{{ row.name }}</strong>
            <el-tag v-if="row.isDefault" size="small" effect="plain" class="default-tag">默认</el-tag>
            <el-tag size="small" effect="plain" class="default-tag">{{ visibilityLabel(row.visibility) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="题库类型" width="120">
          <template #default="{ row }">{{ examQuestionBankLabels[row.type as ExamQuestionBankType] }}</template>
        </el-table-column>
        <el-table-column prop="questionCount" label="题目数量" width="110" align="center" />
        <el-table-column label="创建时间" width="170">
          <template #default="{ row }">{{ dateTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="225" fixed="right">
          <template #default="{ row }">
            <el-button type="primary" link :icon="View" @click="activeBank = row as ExamQuestionBank">{{
              row.canEdit ? '管理题目' : '查看题目'
            }}</el-button>
            <el-button v-if="row.canEdit" link :icon="EditPen" @click="edit(row as ExamQuestionBank)">编辑</el-button>
            <el-tooltip
              v-if="row.canEdit && !row.isDefault"
              :disabled="row.questionCount === 0"
              content="请先清空题库或移出题目"
            >
              <span>
                <el-button
                  type="danger"
                  link
                  :icon="Delete"
                  :disabled="row.questionCount > 0"
                  :loading="deleting === row.id"
                  @click="remove(row as ExamQuestionBank)"
                  >删除</el-button
                >
              </span>
            </el-tooltip>
          </template>
        </el-table-column>
      </el-table>
      <div class="table-footer">
        <el-pagination
          v-model:current-page="page"
          v-model:page-size="size"
          :total="total"
          :page-sizes="[10, 20, 50]"
          layout="total, sizes, prev, pager, next"
          @current-change="load"
          @size-change="filter"
        />
      </div>
    </section>
  </template>
  <el-dialog
    v-model="editor"
    :title="editing ? '编辑题库' : '新建题库'"
    width="min(510px, 94vw)"
    :before-close="guard.beforeClose"
    @opened="focusBankName"
  >
    <el-alert v-if="formError" :title="formError" type="error" show-icon :closable="false" />
    <el-form scroll-to-error label-position="top" @submit.prevent="save">
      <el-form-item label="题库名称" required
        ><el-input ref="bankNameInput" v-model="form.name" maxlength="100"
      /></el-form-item>
      <el-form-item label="题库类型" required>
        <el-select v-model="form.type" :disabled="editing?.isDefault">
          <el-option v-for="(label, key) in examQuestionBankLabels" :key="key" :label="label" :value="key" />
        </el-select>
      </el-form-item>
      <el-form-item label="可见范围" required>
        <el-select v-model="form.visibility">
          <el-option v-for="option in visibilityChoices" :key="option.value" :label="option.label" :value="option.value" />
        </el-select>
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button @click="guard.cancel">取消</el-button>
      <el-button type="primary" :loading="saving" @click="save">{{ editing ? '保存' : '创建题库' }}</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.default-tag {
  margin-left: 8px;
}
</style>

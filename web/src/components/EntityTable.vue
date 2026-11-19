<script setup lang="ts">
import { computed } from 'vue'
import { Connection, Delete, Monitor, Refresh, Search } from '@element-plus/icons-vue'
import type { Row } from '../api'
import type { Entity } from '../entities'

const props = defineProps<{
  entity: Entity
  entityKey: string
  rows: Row[]
  total: number
  loading: boolean
  error: string
  isPeople: boolean
  selectedCount: number
  actionLoading: string
}>()
const search = defineModel<string>('search', { required: true })
const page = defineModel<number>('page', { required: true })
const pageSize = defineModel<number>('pageSize', { required: true })
const emit = defineEmits<{
  refresh: []
  'selection-change': [rows: Row[]]
  'batch-delete': []
  'test-cabinets': []
  terminal: [row: Row]
  action: [row: Row, key: string]
  reset: [row: Row]
  view: [row: Row]
  edit: [row: Row]
  remove: [row: Row]
  navigate: [key: string, params?: Row]
}>()

const actionWidth = computed(() =>
  props.entityKey === 'devices'
    ? 255
    : props.entityKey === 'nodes'
      ? 410
      : props.entityKey === 'cabinets'
        ? 270
        : props.isPeople
          ? 245
          : 165
)

function searchNow() {
  page.value = 1
  emit('refresh')
}

function cell(row: Row, key: string) {
  if (key === 'accountUsage') return `${row.accountCount ?? 0} / ${row.accountQuota ?? 0}`
  if (key === 'virtualMachineUsage') return `${row.virtualMachineCount ?? 0} / ${row.virtualMachineQuota ?? 0}`
  if (key === 'connectionType')
    return ({ simulated: '模拟设备', ssh: 'SSH', telnet: 'Telnet', tcp: 'TCP' } as Row)[row[key]] || row[key]
  if (key === 'provider')
    return ({ simulated: '模拟节点', hyperv: 'Hyper-V', proxmox: 'Proxmox VE' } as Row)[row[key]] || row[key]
  return row[key] === undefined || row[key] === null || row[key] === '' ? '—' : row[key]
}
</script>

<template>
  <section class="surface">
    <div class="table-toolbar">
      <div class="search-box">
        <el-input
          v-model="search"
          :aria-label="`搜索${entity.title}`"
          :prefix-icon="Search"
          clearable
          placeholder="输入关键词搜索"
          @keyup.enter="searchNow"
          @clear="searchNow"
        />
        <el-button @click="searchNow">搜索</el-button>
      </div>
      <div class="toolbar-right">
        <span class="record-count">共 {{ total }} 条记录</span>
        <el-button v-if="isPeople && selectedCount" type="danger" plain :icon="Delete" @click="emit('batch-delete')">
          删除 {{ selectedCount }} 项
        </el-button>
        <el-button
          v-if="entityKey === 'cabinets'"
          :icon="Connection"
          :loading="actionLoading === 'batch-test'"
          :disabled="!selectedCount"
          @click="emit('test-cabinets')"
        >
          测试选中机柜
        </el-button>
        <el-button :icon="Refresh" aria-label="刷新列表" @click="emit('refresh')" />
      </div>
    </div>
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="table-error" />
    <el-table
      v-loading="loading"
      :data="rows"
      row-key="id"
      class="resource-table"
      :empty-text="error ? '数据加载失败，请刷新重试' : '暂无记录，点击右上角新增'"
      @selection-change="emit('selection-change', $event)"
    >
      <el-table-column v-if="isPeople || entityKey === 'cabinets'" type="selection" width="48" />
      <el-table-column
        v-for="column in entity.columns"
        :key="column.key"
        :prop="column.key"
        :label="column.label"
        :width="column.width"
        :min-width="column.width || 130"
        show-overflow-tooltip
      >
        <template #default="{ row }">
          <span v-if="column.key === 'name'" class="table-name">
            {{ cell(row, column.key) }}
            <el-tag v-if="row.isSimulated" type="warning" size="small" effect="light">模拟</el-tag>
          </span>
          <el-tag v-else-if="column.key === 'enabled'" :type="row.enabled ? 'success' : 'info'" effect="light">
            {{ row.enabled ? '允许分配' : '已停用' }}
          </el-tag>
          <el-tag
            v-else-if="
              (column.key === 'provider' || column.key === 'connectionType') && row[column.key] === 'simulated'
            "
            type="warning"
            effect="light"
          >
            {{ cell(row, column.key) }}
          </el-tag>
          <span v-else>{{ cell(row, column.key) }}</span>
        </template>
      </el-table-column>
      <el-table-column label="操作" fixed="right" :width="actionWidth">
        <template #default="{ row }">
          <el-button v-if="entityKey === 'devices'" link type="primary" :icon="Monitor" @click="emit('terminal', row)">
            终端
          </el-button>
          <el-button
            v-if="entityKey === 'devices'"
            link
            type="primary"
            :loading="actionLoading === `${row.id}:test`"
            @click="emit('action', row, 'test')"
          >
            测试
          </el-button>
          <el-button
            v-if="entityKey === 'cabinets'"
            link
            type="primary"
            @click="emit('navigate', 'devices', { cabinetId: row.id })"
          >
            设备
          </el-button>
          <el-button v-if="entityKey === 'cabinets'" link type="warning" @click="emit('action', row, 'reclaim')"
            >回收</el-button
          >
          <el-button v-if="entityKey === 'nodes'" link type="primary" @click="emit('action', row, 'status')"
            >状态</el-button
          >
          <el-button
            v-if="entityKey === 'nodes'"
            link
            :type="row.enabled ? 'warning' : 'success'"
            @click="emit('action', row, 'enable')"
          >
            {{ row.enabled ? '停用' : '启用' }}
          </el-button>
          <el-button
            v-if="entityKey === 'nodes' && row.provider === 'simulated'"
            link
            type="primary"
            @click="emit('action', row, 'configuration')"
          >
            系统配置
          </el-button>
          <el-button v-if="entityKey === 'nodes'" link type="primary" @click="emit('action', row, 'accounts')"
            >分配账号</el-button
          >
          <el-button v-if="isPeople" link type="warning" @click="emit('reset', row)">重置密码</el-button>
          <el-button
            v-if="!['devices', 'cabinets', 'nodes'].includes(entityKey)"
            link
            type="primary"
            @click="emit('view', row)"
          >
            查看
          </el-button>
          <el-button link type="primary" @click="emit('edit', row)">编辑</el-button>
          <el-button link type="danger" @click="emit('remove', row)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>
    <div class="table-footer">
      <span>{{ selectedCount ? `已选择 ${selectedCount} 项` : '数据保存在当前部署服务器' }}</span>
      <el-pagination
        v-model:current-page="page"
        v-model:page-size="pageSize"
        :total="total"
        :page-sizes="[10, 20, 50, 100]"
        layout="sizes, prev, pager, next"
        @change="emit('refresh')"
      />
    </div>
  </section>
</template>

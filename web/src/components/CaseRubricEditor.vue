<script setup lang="ts">
import { ref, watch } from 'vue'
import { api, messageOf } from '../api'
import { caseFieldLabel, type CaseDeviceOption, type CaseField, type CaseRubricItem } from '../caseWork'

const rules = defineModel<CaseRubricItem[]>({ required: true })
const props = defineProps<{ nodeId: string | null }>()
const devices = ref<CaseDeviceOption[]>([])
const error = ref('')
const loading = ref(false)
const fields: CaseField[] = ['hostname', 'enabled', 'description']

watch(
  () => props.nodeId,
  async (id, _, onCleanup) => {
    let cancelled = false
    onCleanup(() => {
      cancelled = true
    })
    devices.value = []
    error.value = ''
    loading.value = !!id
    if (!id) return
    try {
      const options = await api<CaseDeviceOption[]>(`/nodes/${id}/case-devices`)
      if (!cancelled) devices.value = options
    } catch (failure) {
      if (!cancelled) error.value = messageOf(failure)
    } finally {
      if (!cancelled) loading.value = false
    }
  },
  { immediate: true }
)

function add() {
  rules.value.push({
    name: '',
    field: 'hostname',
    deviceId: devices.value[0]?.id || '',
    interfaceName: null,
    expected: '',
    score: 10,
    guidance: ''
  })
}

function resetTarget(rule: CaseRubricItem) {
  rule.interfaceName = null
  rule.expected = rule.field === 'enabled' ? 'true' : ''
}
</script>

<template>
  <section>
    <div class="rubric-heading">
      <h3>实操评分表</h3>
      <el-button :disabled="loading || !devices.length || rules.length >= 30" @click="add">添加评分项</el-button>
    </div>
    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" />
    <p v-if="!props.nodeId">选择启动节点后可添加评分项。</p>
    <p v-else-if="!loading && !devices.length && !error">该节点未配置模拟设备。</p>
    <p v-else-if="!rules.length">添加评分项后，学生可提交自动评分。</p>
    <div v-for="(rule, index) in rules" :key="index" class="rubric-rule">
      <el-form-item label="评分项名称" required>
        <el-input v-model="rule.name" maxlength="100" placeholder="例如：配置交换机主机名" />
      </el-form-item>
      <el-form-item label="模拟设备" required>
        <el-select v-model="rule.deviceId" filterable @change="rule.interfaceName = null">
          <el-option v-for="device in devices" :key="device.id" :label="device.name" :value="device.id" />
        </el-select>
      </el-form-item>
      <el-form-item label="检查字段">
        <el-select v-model="rule.field" @change="resetTarget(rule)">
          <el-option v-for="field in fields" :key="field" :label="caseFieldLabel[field]" :value="field" />
        </el-select>
      </el-form-item>
      <el-form-item v-if="rule.field !== 'hostname'" label="接口" required>
        <el-select v-model="rule.interfaceName">
          <el-option
            v-for="name in devices.find(device => device.id === rule.deviceId)?.interfaces || []"
            :key="name"
            :label="name"
            :value="name"
          />
        </el-select>
      </el-form-item>
      <el-form-item label="预期配置">
        <el-select v-if="rule.field === 'enabled'" v-model="rule.expected">
          <el-option label="启用" value="true" />
          <el-option label="停用" value="false" />
        </el-select>
        <el-input v-else v-model="rule.expected" :maxlength="rule.field === 'hostname' ? 63 : 100" />
      </el-form-item>
      <el-form-item label="分值" required>
        <el-input-number v-model="rule.score" :min="1" :max="100" :precision="0" />
      </el-form-item>
      <el-form-item label="未达标时的排错指导" class="guidance">
        <el-input v-model="rule.guidance" maxlength="500" />
      </el-form-item>
      <el-button link type="danger" class="remove" @click="rules.splice(index, 1)">删除评分项</el-button>
    </div>
  </section>
</template>

<style scoped>
.rubric-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}
.rubric-rule {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 190px), 1fr));
  padding: 12px;
  gap: 0 12px;
  margin-bottom: 12px;
  border: 1px solid #dce6ef;
  border-radius: 8px;
}
.guidance {
  grid-column: 1 / -1;
}
.remove {
  justify-self: start;
}
</style>

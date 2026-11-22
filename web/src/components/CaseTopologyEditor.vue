<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, del, messageOf, post } from '../api'
import type { CaseDeviceOption, CaseTopologyBlueprint } from '../caseWork'

interface Template {
  id: string
  name: string
  blueprint: CaseTopologyBlueprint
}
const blueprint = defineModel<CaseTopologyBlueprint | null>({ required: true })
const props = defineProps<{ nodeId: string; devices: CaseDeviceOption[] }>()
const templates = ref<Template[]>([])
const selected = ref('')
const issue = ref('')
const busy = ref(false)
let request = 0
const nodeDevices = computed(() => props.devices.filter(item => blueprint.value?.deviceIds.includes(item.id)))

async function load() {
  const generation = ++request
  issue.value = ''
  try {
    const rows = await api<Template[]>(`/case-topology-templates?nodeId=${encodeURIComponent(props.nodeId)}`)
    if (generation === request) templates.value = rows
  } catch (failure) {
    if (generation === request) issue.value = messageOf(failure)
  }
}
watch(
  () => props.nodeId,
  () => {
    templates.value = []
    selected.value = ''
    void load()
  },
  { immediate: true }
)

function choose() {
  const template = templates.value.find(item => item.id === selected.value)
  if (template) blueprint.value = JSON.parse(JSON.stringify(template.blueprint))
}

function pickDevices(ids: string[]) {
  if (!blueprint.value) return
  blueprint.value = {
    deviceIds: ids,
    links: blueprint.value.links.filter(link => ids.includes(link.fromDeviceId) && ids.includes(link.toDeviceId))
  }
}

function addLink() {
  blueprint.value?.links.push({ fromDeviceId: '', fromPort: '', toDeviceId: '', toPort: '', network: '' })
}

function clearBlueprint() {
  blueprint.value = null
  selected.value = ''
}

async function saveTemplate() {
  if (!blueprint.value) return
  try {
    const { value } = await ElMessageBox.prompt('填写可在其他案例中复用的模板名称。', '另存拓扑模板', {
      inputValidator: value => (!!value?.trim() && value.trim().length <= 100) || '模板名称需为 1–100 字',
      confirmButtonText: '保存模板'
    })
    busy.value = true
    const saved = await post<Template>('/case-topology-templates', {
      name: value,
      nodeId: props.nodeId,
      blueprint: blueprint.value
    })
    await load()
    selected.value = saved.id
    ElMessage.success('拓扑模板已保存')
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') issue.value = messageOf(failure)
  } finally {
    busy.value = false
  }
}

async function deleteTemplate() {
  const id = selected.value
  if (!id) return
  try {
    await ElMessageBox.confirm('删除后，已保存到课程的拓扑仍可使用。', '删除拓扑模板', { type: 'warning' })
    await del(`/case-topology-templates/${id}`)
    selected.value = ''
    await load()
  } catch (failure) {
    if (failure !== 'cancel' && failure !== 'close') issue.value = messageOf(failure)
  }
}
</script>

<template>
  <section class="topology-editor" aria-label="实操拓扑">
    <h3>实操拓扑</h3>
    <el-alert v-if="issue" type="error" :title="issue" :closable="false" />
    <div class="topology-tools">
      <el-select v-model="selected" clearable aria-label="拓扑模板" placeholder="选择拓扑模板" @change="choose">
        <el-option v-for="item in templates" :key="item.id" :label="item.name" :value="item.id" />
      </el-select>
      <el-button v-if="selected" link type="danger" @click="deleteTemplate">删除模板</el-button>
      <el-button v-if="!blueprint" @click="blueprint = { deviceIds: [], links: [] }">自定义拓扑</el-button>
      <el-button v-else link @click="clearBlueprint">取消使用拓扑</el-button>
    </div>
    <template v-if="blueprint">
      <el-form-item label="部署设备">
        <el-select
          :model-value="blueprint.deviceIds"
          multiple
          filterable
          placeholder="选择实操要部署的设备"
          @change="pickDevices"
        >
          <el-option v-for="item in devices" :key="item.id" :label="item.name" :value="item.id" />
        </el-select>
      </el-form-item>
      <fieldset v-for="(link, index) in blueprint.links" :key="index" class="topology-link">
        <legend>连线 {{ index + 1 }}</legend>
        <el-select
          v-model="link.fromDeviceId"
          placeholder="起点设备"
          aria-label="起点设备"
          @change="link.fromPort = ''"
        >
          <el-option v-for="item in nodeDevices" :key="item.id" :label="item.name" :value="item.id" />
        </el-select>
        <el-select v-model="link.fromPort" placeholder="起点接口" aria-label="起点接口">
          <el-option
            v-for="port in nodeDevices.find(item => item.id === link.fromDeviceId)?.interfaces || []"
            :key="port"
            :label="port"
            :value="port"
          />
        </el-select>
        <el-select v-model="link.toDeviceId" placeholder="终点设备" aria-label="终点设备" @change="link.toPort = ''">
          <el-option
            v-for="item in nodeDevices"
            :key="item.id"
            :label="item.name"
            :value="item.id"
            :disabled="item.id === link.fromDeviceId"
          />
        </el-select>
        <el-select v-model="link.toPort" placeholder="终点接口" aria-label="终点接口">
          <el-option
            v-for="port in nodeDevices.find(item => item.id === link.toDeviceId)?.interfaces || []"
            :key="port"
            :label="port"
            :value="port"
          />
        </el-select>
        <el-input v-model="link.network" placeholder="网络名称" aria-label="网络名称" maxlength="40" />
        <el-button link type="danger" @click="blueprint.links.splice(index, 1)">删除连线</el-button>
      </fieldset>
      <div class="topology-tools">
        <el-button :disabled="blueprint.deviceIds.length < 2 || blueprint.links.length >= 50" @click="addLink"
          >添加连线</el-button
        >
        <el-button :disabled="!blueprint.deviceIds.length" :loading="busy" @click="saveTemplate"
          >另存拓扑模板</el-button
        >
      </div>
    </template>
  </section>
</template>

<style scoped>
.topology-editor {
  margin: 14px 0;
}
.topology-tools {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin: 8px 0;
}
.topology-tools .el-select {
  width: min(290px, 100%);
}
.topology-link {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(125px, 1fr));
  gap: 8px;
  padding: 8px;
  margin: 6px 0;
  border: 1px solid var(--el-border-color);
}
</style>

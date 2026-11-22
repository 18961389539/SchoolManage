<script setup lang="ts">
import { computed, nextTick, reactive, ref, watch } from 'vue'
import type { FormInstance, FormItemRule, FormRules } from 'element-plus'
import type { Row } from '../api'
import { newEntity, type Entity, type Field } from '../entities'

const visible = defineModel<boolean>({ required: true })
const props = defineProps<{
  entity: Entity
  editing: boolean
  initialRow: Row | null
  options: Record<string, Row[]>
  saving: boolean
}>()
const emit = defineEmits<{ submit: [payload: Row] }>()

const form = reactive<Row>({})
const formRef = ref<FormInstance>()
function isAvailable(field: Field) {
  return !field.visibleWhen || form[field.visibleWhen.key] === field.visibleWhen.value
}
const fields = computed(() =>
  props.entity.fields.filter(field => (!props.editing || !field.createOnly) && isAvailable(field))
)

function isRequired(field: Field) {
  return !!field.required && (!field.requiredWhen || form[field.requiredWhen.key] === field.requiredWhen.value)
}

function fieldRules(field: Field): FormItemRule[] {
  return [
    ...(isRequired(field) ? [{ required: true, message: `请填写${field.label}`, trigger: ['blur', 'change'] }] : []),
    ...(field.type === 'password' && field.createOnly ? [{ min: 12, message: '密码至少 12 位', trigger: 'blur' }] : []),
    ...(field.type === 'email' ? [{ type: 'email', message: '请输入正确的电子邮箱', trigger: 'blur' }] : [])
  ] as FormItemRule[]
}

const rules = computed<FormRules>(() => Object.fromEntries(fields.value.map(field => [field.key, fieldRules(field)])))

watch(visible, open => {
  if (!open) return
  for (const key of Object.keys(form)) delete form[key]
  Object.assign(form, newEntity(props.entity), props.initialRow || {})
  for (const field of props.entity.fields) if (field.type === 'password') form[field.key] = ''
  void nextTick(() => formRef.value?.clearValidate())
})

function fieldOptions(
  field: Field
): { label: string; value: string | number | boolean | object; disabled?: boolean }[] {
  return (
    field.options ||
    (field.source ? (props.options[field.source] || []).map(row => ({ label: row.name, value: row.id })) : [])
  )
}

async function submit() {
  const valid = await formRef.value?.validate().catch(() => false)
  if (!valid) return
  const payload: Row = { ...form }
  if (props.entity.kind) payload.kind = props.entity.kind
  if (!payload.classId && props.entity.kind) payload.classId = null
  if (props.entity.endpoint === '/devices') {
    payload.nodeId = payload.connectionType === 'simulated' && payload.nodeId ? payload.nodeId : null
  }
  emit('submit', payload)
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="`${editing ? '编辑' : '新增'}${entity.singular}`"
    width="650px"
    :close-on-click-modal="false"
    destroy-on-close
  >
    <el-form ref="formRef" :model="form" :rules="rules" label-position="top" class="entity-form">
      <el-form-item
        v-for="field in fields"
        :key="field.key"
        :label="field.label"
        :prop="field.key"
        :class="{ 'full-field': field.type === 'textarea' || field.type === 'password' }"
      >
        <el-select
          v-if="field.type === 'select'"
          v-model="form[field.key]"
          filterable
          :clearable="!isRequired(field)"
          :placeholder="`请选择${field.label}`"
        >
          <el-option
            v-for="option in fieldOptions(field)"
            :key="String(option.value)"
            :label="option.label"
            :value="option.value"
            :disabled="option.disabled"
          />
        </el-select>
        <el-input-number
          v-else-if="field.type === 'number'"
          v-model="form[field.key]"
          :min="field.min"
          :max="field.max"
          controls-position="right"
        />
        <el-switch
          v-else-if="field.type === 'switch'"
          v-model="form[field.key]"
          :active-text="field.key === 'isSimulated' ? '模拟机柜' : '允许分配'"
          :inactive-text="field.key === 'isSimulated' ? '真实机柜' : '暂停分配'"
        />
        <el-input
          v-else
          v-model="form[field.key]"
          :type="field.type === 'password' ? 'password' : field.type === 'textarea' ? 'textarea' : 'text'"
          :show-password="field.type === 'password'"
          :rows="3"
          :placeholder="field.placeholder || `请输入${field.label}`"
          :maxlength="field.type === 'textarea' ? 1000 : 256"
          :autocomplete="field.type === 'password' ? 'new-password' : 'off'"
        />
        <div v-if="field.help" class="field-help">{{ field.help }}</div>
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button @click="visible = false">取消</el-button>
      <el-button type="primary" :loading="saving" @click="submit">保存{{ entity.singular }}</el-button>
    </template>
  </el-dialog>
</template>

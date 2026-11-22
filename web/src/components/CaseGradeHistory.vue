<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { dateTime } from '../api'
import { caseFieldLabel, type CaseField, type CaseGrade } from '../caseWork'

const props = defineProps<{ grades: CaseGrade[] }>()
const open = ref('')
const finals = computed(() => props.grades.filter(grade => grade.isFinal))
const average = computed(() => {
  const grades = finals.value
  return grades.length ? Math.round((grades.reduce((sum, grade) => sum + grade.score, 0) / grades.length) * 10) / 10 : 0
})
const best = computed(() => Math.max(0, ...finals.value.map(grade => grade.score)))

watch(
  () => props.grades[0]?.id,
  id => (open.value = id || ''),
  { immediate: true }
)

function configValue(value: string | null, field: CaseField) {
  if (value === null) return '不可用'
  if (field === 'enabled') return value === 'true' ? '启用' : '停用'
  return value || '（空）'
}
</script>

<template>
  <section v-if="grades.length" class="case-grade-history">
    <h3>评分记录</h3>
    <p v-if="finals.length">正式评分 {{ finals.length }} 次 · 平均分 {{ average }} 分 · 最高得分 {{ best }} 分</p>
    <el-collapse v-model="open" accordion>
      <el-collapse-item v-for="grade in grades" :key="grade.id" :name="grade.id">
        <template #title>
          <span class="case-grade-heading">
            {{ grade.isFinal ? '正式评分' : '尝试评分' }} · {{ grade.score }} / {{ grade.maxScore }} 分 ·
            {{ dateTime(grade.scoredAt) }}
            <el-tag size="small" :type="grade.passed ? 'success' : 'warning'">{{
              grade.passed ? '达标' : '未达标'
            }}</el-tag>
          </span>
        </template>
        <p>实操开始 {{ dateTime(grade.startedAt) }} · 评分结束 {{ dateTime(grade.scoredAt) }}</p>
        <el-table :data="grade.details">
          <el-table-column label="评分项" min-width="160">
            <template #default="{ row }">
              <strong>{{ row.name }}</strong>
              <div>{{ row.deviceName }} · {{ row.interfaceName }} {{ caseFieldLabel[row.field as CaseField] }}</div>
            </template>
          </el-table-column>
          <el-table-column label="预期 / 实际" min-width="150">
            <template #default="{ row }">
              {{ configValue(row.expected, row.field) }} / {{ configValue(row.actual, row.field) }}
            </template>
          </el-table-column>
          <el-table-column label="得分" width="86">
            <template #default="{ row }">{{ row.matched ? row.score : 0 }} / {{ row.score }}</template>
          </el-table-column>
          <el-table-column label="排错指导" min-width="150">
            <template #default="{ row }">{{ row.matched ? '—' : row.guidance || '请核对该项预期配置。' }}</template>
          </el-table-column>
        </el-table>
      </el-collapse-item>
    </el-collapse>
  </section>
</template>

<style scoped>
.case-grade-history {
  margin-top: 16px;
}
.case-grade-heading {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}
</style>

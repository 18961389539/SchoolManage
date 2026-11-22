<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Refresh } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import { campLevelType, campResultType } from '../camp'
import type { CampReport } from '../types'

const visible = defineModel<boolean>({ required: true })
const props = defineProps<{ attemptId: string }>()

const report = ref<CampReport | null>(null)
const loading = ref(false)
const error = ref('')

const CX = 170
const CY = 154
const RADIUS = 94

const axes = computed(() => report.value?.radar || [])
const canDrawRadar = computed(() => axes.value.length >= 3)
const numberedAxes = computed(() => axes.value.length > 8)

function point(index: number, ratio: number): [number, number] {
  const total = Math.max(1, axes.value.length)
  const angle = -Math.PI / 2 + (index * 2 * Math.PI) / total
  return [CX + Math.cos(angle) * RADIUS * ratio, CY + Math.sin(angle) * RADIUS * ratio]
}

function ringPoints(ratio: number) {
  return axes.value.map((_, index) => point(index, ratio).join(',')).join(' ')
}

const scorePoints = computed(() =>
  axes.value.map((axis, index) => point(index, Math.min(100, Math.max(0, axis.percent)) / 100).join(',')).join(' ')
)

function labelPosition(index: number) {
  const [x, y] = point(index, 1.26)
  return { x, y }
}

function axisName(name: string, index: number) {
  if (numberedAxes.value) return String(index + 1)
  const letters = Array.from(name)
  return letters.length > 8 ? `${letters.slice(0, 8).join('')}…` : name
}

async function load() {
  if (!props.attemptId) return
  loading.value = true
  error.value = ''
  try {
    report.value = await api<CampReport>(`/camp/attempts/${props.attemptId}/report`)
  } catch (failure) {
    report.value = null
    error.value = messageOf(failure)
  } finally {
    loading.value = false
  }
}

watch(
  () => [visible.value, props.attemptId],
  () => {
    if (visible.value) void load()
  },
  { immediate: true }
)
</script>

<template>
  <el-drawer
    v-model="visible"
    title="岗位能力分析报告"
    class="camp-report-drawer"
    size="min(860px, 100vw)"
    destroy-on-close
  >
    <div v-if="loading" class="report-loading"><el-skeleton :rows="8" animated /></div>
    <el-result v-else-if="error" icon="error" title="报告加载失败" :sub-title="error">
      <template #extra><el-button type="primary" :icon="Refresh" @click="load">重试</el-button></template>
    </el-result>
    <template v-else-if="report">
      <header class="report-head">
        <div>
          <h2>{{ report.position.name }}</h2>
          <p>
            {{ report.attempt.studentName }}（{{ report.attempt.studentNumber || report.attempt.studentUserName }}）
            <span v-if="report.attempt.className"> · {{ report.attempt.className }}</span>
          </p>
        </div>
        <div class="report-score">
          <strong>{{ report.attempt.totalScore }}</strong>
          <span>/ {{ report.attempt.maxScore }}</span>
          <el-tag :type="campLevelType(report.attempt.level)" effect="dark">{{ report.attempt.level }}</el-tag>
        </div>
      </header>
      <dl class="report-summary">
        <div>
          <dt>得分率</dt>
          <dd>{{ report.attempt.percent }}%</dd>
        </div>
        <div>
          <dt>客观题</dt>
          <dd>{{ report.attempt.objectiveScore }} 分</dd>
        </div>
        <div>
          <dt>实操题</dt>
          <dd>{{ report.attempt.practicalScore }} 分</dd>
        </div>
        <div>
          <dt>题目</dt>
          <dd>{{ report.questionCount }} 道题</dd>
        </div>
      </dl>
      <div class="report-meta">
        <span>提交 {{ dateTime(report.attempt.submittedAt || report.evaluatedAt) }}</span>
        <el-tag v-if="report.attempt.autoSubmitted" type="warning" size="small" effect="plain">超时自动提交</el-tag>
      </div>

      <div class="report-body">
        <section class="radar-panel">
          <h3>能力雷达</h3>
          <svg v-if="canDrawRadar" :viewBox="`0 0 ${CX * 2} ${CY * 2}`" role="img" aria-label="岗位能力雷达图">
            <g>
              <polygon
                v-for="ratio in [0.2, 0.4, 0.6, 0.8, 1]"
                :key="ratio"
                :points="ringPoints(ratio)"
                fill="none"
                stroke="#dce7eb"
                stroke-width="1"
              />
              <line
                v-for="(_, index) in axes"
                :key="`axis-${index}`"
                :x1="CX"
                :y1="CY"
                :x2="point(index, 1)[0]"
                :y2="point(index, 1)[1]"
                stroke="#e4eeea"
                stroke-width="1"
              />
              <polygon :points="scorePoints" fill="rgba(47, 109, 91, 0.22)" stroke="#2f6d5b" stroke-width="2" />
              <circle
                v-for="(axis, index) in axes"
                :key="`dot-${index}`"
                :cx="point(index, Math.min(100, Math.max(0, axis.percent)) / 100)[0]"
                :cy="point(index, Math.min(100, Math.max(0, axis.percent)) / 100)[1]"
                r="3.5"
                fill="#2f6d5b"
              />
              <text
                v-for="(axis, index) in axes"
                :key="`label-${index}`"
                :x="labelPosition(index).x"
                :y="labelPosition(index).y"
                text-anchor="middle"
                dominant-baseline="middle"
                fill="#51687f"
                font-size="11"
              >
                <title>{{ axis.name }}</title>
                {{ axisName(axis.name, index) }}
              </text>
            </g>
          </svg>
          <ol v-if="numberedAxes" class="radar-labels" aria-label="雷达图图例">
            <li v-for="axis in axes" :key="axis.name">
              <span>{{ axis.name }}</span>
              <strong>{{ axis.percent }}%</strong>
            </li>
          </ol>
          <div v-if="!canDrawRadar" class="radar-fallback">
            <p v-if="!axes.length">暂无技能达成数据</p>
            <div v-for="axis in axes" :key="axis.name" class="fallback-row">
              <span>{{ axis.name }}</span>
              <el-progress :percentage="axis.percent" :stroke-width="10" />
            </div>
          </div>
        </section>

        <section class="skill-panel">
          <h3>技能明细</h3>
          <ul v-if="report.skills.length" class="skill-list">
            <li v-for="(skill, index) in report.skills" :key="index" class="skill-card">
              <div class="skill-card-heading">
                <strong>{{ skill.skill }}</strong>
                <el-tag :type="campResultType(skill.result)" size="small">{{ skill.result }}</el-tag>
              </div>
              <p v-if="skill.subSkill" class="skill-sub">子技能 · {{ skill.subSkill }}</p>
              <p v-if="skill.ability" class="skill-sub">具体能力 · {{ skill.ability }}</p>
              <dl class="skill-measures">
                <div>
                  <dt>题目</dt>
                  <dd>{{ skill.questionCount }} 道</dd>
                </div>
                <div>
                  <dt>得分</dt>
                  <dd>{{ skill.score }} / {{ skill.maxScore }} 分</dd>
                </div>
              </dl>
              <div class="skill-progress">
                <span>达成度</span>
                <el-progress :percentage="skill.percent" :stroke-width="6" :aria-label="`${skill.skill}达成度`" />
              </div>
            </li>
          </ul>
          <p v-else class="skill-sub">暂无技能数据</p>
        </section>
      </div>
      <p v-if="report.position.description" class="report-description">
        <strong>岗位说明</strong> {{ report.position.description }}
      </p>
    </template>
  </el-drawer>
</template>

<style scoped>
:global(.camp-report-drawer .el-drawer__header) {
  flex: none;
  margin: 0;
  padding: 17px 22px;
  border-bottom: 1px solid #dce7eb;
}

:global(.camp-report-drawer .el-drawer__body) {
  min-height: 0;
  padding: 18px 22px 24px;
  overscroll-behavior: contain;
}

.report-loading {
  padding: 20px 0;
}

.report-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 12px 18px;
}

.report-head > div:first-child {
  min-width: 0;
  overflow-wrap: anywhere;
}

.report-head h2 {
  margin: 0 0 5px;
  color: #1c334e;
  font-size: 21px;
}

.report-head p {
  margin: 0;
  color: var(--text-muted, #586d84);
  font-size: 13px;
}

.report-score {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: 6px;
  color: var(--text-muted, #586d84);
  font-size: 13px;
  font-variant-numeric: tabular-nums;
}

.report-score strong {
  color: #1e674f;
  font-size: 30px;
  line-height: 1;
}

.report-score :deep(.el-tag) {
  align-self: center;
  margin-inline-start: 2px;
}

.report-summary {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 8px;
  margin: 14px 0 10px;
}

.report-summary > div {
  padding: 9px 11px;
  border: 1px solid #e4eeea;
  border-radius: 8px;
  background: #f3f8f6;
}

.report-summary dt,
.skill-measures dt {
  margin-bottom: 2px;
  color: var(--text-muted, #586d84);
  font-size: 12px;
}

.report-summary dd {
  margin: 0;
  color: #233f5b;
  font-size: 16px;
  font-weight: 650;
  font-variant-numeric: tabular-nums;
}

.report-meta {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 5px 14px;
  color: var(--text-muted, #586d84);
  font-size: 12px;
}

.report-body {
  display: grid;
  grid-template-columns: minmax(260px, 0.9fr) minmax(0, 1.3fr);
  align-items: start;
  gap: 15px;
  margin-top: 15px;
}

.radar-panel,
.skill-panel {
  min-width: 0;
  padding: 13px;
  border: 1px solid #e4eeea;
  border-radius: 9px;
  overflow-wrap: anywhere;
}

.radar-panel h3,
.skill-panel h3 {
  margin: 0 0 9px;
  color: #1c334e;
  font-size: 15px;
}

.radar-panel svg {
  display: block;
  width: 100%;
  max-width: 320px;
  height: auto;
  margin-inline: auto;
}

.radar-labels {
  display: grid;
  gap: 3px;
  margin: 0;
  padding-inline-start: 20px;
  color: #3d5770;
  font-size: 12px;
}

.radar-labels strong {
  margin-inline-start: 5px;
  font-variant-numeric: tabular-nums;
}

.radar-fallback p,
.skill-sub {
  margin: 0;
  color: var(--text-muted, #586d84);
  font-size: 12px;
  line-height: 1.55;
}

.fallback-row {
  display: grid;
  grid-template-columns: minmax(0, 105px) minmax(0, 1fr);
  align-items: center;
  gap: 8px;
  margin: 6px 0;
  color: #3b566e;
  font-size: 12px;
}

.skill-list {
  display: grid;
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.skill-card {
  display: grid;
  gap: 4px;
  min-width: 0;
  padding: 10px;
  border: 1px solid #e4eeea;
  border-radius: 7px;
  background: #f6faf8;
}

.skill-card-heading {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 7px;
  color: #25415c;
  font-size: 14px;
}

.skill-card-heading :deep(.el-tag) {
  flex-shrink: 0;
}

.skill-measures {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 15px;
  margin: 2px 0;
}

.skill-measures > div {
  display: flex;
  align-items: baseline;
  gap: 4px;
}

.skill-measures dt,
.skill-measures dd {
  margin: 0;
}

.skill-measures dd {
  font-size: 13px;
  font-variant-numeric: tabular-nums;
}

.skill-progress {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  align-items: center;
  gap: 8px;
  color: var(--text-muted, #586d84);
  font-size: 12px;
}

.report-description {
  margin: 12px 0 0;
  color: #425d77;
  font-size: 13px;
  line-height: 1.65;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

@media (max-width: 740px) {
  .report-body {
    grid-template-columns: minmax(0, 1fr);
    gap: 10px;
  }

  .radar-panel svg {
    max-width: 260px;
  }
}

@media (max-width: 540px) {
  :global(.camp-report-drawer .el-drawer__header),
  :global(.camp-report-drawer .el-drawer__body) {
    padding-inline: 13px;
  }

  .report-head h2 {
    font-size: 18px;
  }

  .report-summary {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .report-summary > div {
    padding: 7px 8px;
  }
}
</style>

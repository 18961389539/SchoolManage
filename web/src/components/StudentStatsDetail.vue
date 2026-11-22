<script setup lang="ts">
import { computed, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { View } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import { remainingText } from '../analytics'
import type {
  CaseRecordDetail,
  ClassroomStatsPracticeRow,
  ClassroomStatsTranscriptRow,
  PracticeAttemptDetail,
  StudentStatsDetail
} from '../analytics'
import { recordStatusLabels } from '../teaching'
import CaseGradeHistory from './CaseGradeHistory.vue'

const props = defineProps<{ detail: StudentStatsDetail }>()
const attemptVisible = ref(false)
const attemptLoading = ref(false)
const attempt = ref<PracticeAttemptDetail | null>(null)
const caseVisible = ref(false)
const caseLoading = ref(false)
const caseRecord = ref<CaseRecordDetail | null>(null)

const kindRows = computed(() => {
  const summary = props.detail.summary
  return [
    { label: '理论知识', stats: summary.theory },
    { label: '理论练习', stats: summary.theoryPractice },
    { label: '案例教学', stats: summary.case },
    { label: '案例实操', stats: summary.casePractice }
  ]
})

function statusLabel(row: { learned: boolean; passed: boolean }) {
  return !row.learned ? '未学习' : row.passed ? '已达标' : '学习中'
}

function statusType(row: { learned: boolean; passed: boolean }) {
  return !row.learned ? 'info' : row.passed ? 'success' : 'warning'
}

async function openAttempt(row: ClassroomStatsPracticeRow) {
  attemptVisible.value = true
  attempt.value = null
  attemptLoading.value = true
  try {
    attempt.value = await api<PracticeAttemptDetail>(
      `/classrooms/${props.detail.classroom.id}/statistics/practice-attempts/${row.attemptId}`
    )
  } catch (failure) {
    ElMessage.error(messageOf(failure))
    attemptVisible.value = false
  } finally {
    attemptLoading.value = false
  }
}

async function openCase(recordId: string) {
  caseVisible.value = true
  caseRecord.value = null
  caseLoading.value = true
  try {
    caseRecord.value = await api<CaseRecordDetail>(
      `/classrooms/${props.detail.classroom.id}/statistics/case-records/${recordId}`
    )
  } catch (failure) {
    ElMessage.error(messageOf(failure))
    caseVisible.value = false
  } finally {
    caseLoading.value = false
  }
}

function letters(indexes: number[]) {
  const alphabet = ['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J']
  return indexes.map(index => alphabet[index] || String(index + 1)).join('、') || '未作答'
}
</script>

<template>
  <div class="student-stats-detail">
    <div class="detail-summary">
      <article class="summary-main">
        <span>资源达标率</span>
        <strong>{{ detail.summary.overall.passRate }}<small>%</small></strong>
        <el-tag :type="detail.summary.isPassing ? 'success' : 'warning'" size="small">
          {{ detail.summary.isPassing ? '已达标' : '未达标' }}
        </el-tag>
        <p>
          已学习 {{ detail.summary.overall.learned }} / {{ detail.summary.overall.total }}，达标
          {{ detail.summary.overall.passed }}
        </p>
      </article>
      <article>
        <span>资源学习率</span><strong>{{ detail.summary.overall.learningRate }}%</strong>
        <p>{{ detail.student.className || '未分班' }} · {{ detail.student.number }}</p>
      </article>
      <article>
        <span>活跃天数</span><strong>{{ detail.summary.activeDays }}<small> 天</small></strong>
        <p>最近 {{ detail.summary.lastActivity ? dateTime(detail.summary.lastActivity) : '暂无记录' }}</p>
      </article>
      <article>
        <span>课堂剩余</span><strong class="remaining">{{ remainingText(detail.classroom.remainingSeconds) }}</strong>
        <p>理论练习单次达标线 60%</p>
      </article>
    </div>

    <div class="kind-strip">
      <div v-for="row in kindRows" :key="row.label" class="kind-item">
        <div>
          <span>{{ row.label }}</span><strong>{{ row.stats.learned }} / {{ row.stats.total }}</strong>
        </div>
        <el-progress :percentage="row.stats.passRate" :stroke-width="6" :show-text="false" />
        <p>达标 {{ row.stats.passed }} · {{ row.stats.passRate }}%</p>
      </div>
    </div>

    <el-tabs class="detail-tabs">
      <el-tab-pane label="成绩单">
        <el-table :data="detail.transcript" row-key="id" empty-text="暂无课程资源" max-height="480">
          <el-table-column label="章节 / 资源" min-width="250">
            <template #default="{ row }">
              <strong class="resource-name">{{ row.name }}</strong>
              <p class="resource-path">{{ row.chapterName }} / {{ row.sectionName }}</p>
            </template>
          </el-table-column>
          <el-table-column prop="kindLabel" label="类型" width="100" />
          <el-table-column label="状态" width="90">
            <template #default="{ row }">
              <el-tag size="small" :type="statusType(row as ClassroomStatsTranscriptRow)">{{
                statusLabel(row as ClassroomStatsTranscriptRow)
              }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column label="成绩" width="100">
            <template #default="{ row }">
              {{ row.maxScore > 0 ? `${row.bestScore} / ${row.maxScore}` : '—' }}
            </template>
          </el-table-column>
          <el-table-column prop="attemptCount" label="练习次数" width="90" />
          <el-table-column label="最近学习" min-width="165">
            <template #default="{ row }">{{ row.lastActivity ? dateTime(row.lastActivity) : '—' }}</template>
          </el-table-column>
        </el-table>
      </el-tab-pane>

      <el-tab-pane label="理论练习历史">
        <el-table :data="detail.practiceHistory" row-key="attemptId" empty-text="暂无练习提交记录" max-height="480">
          <el-table-column label="资源" min-width="200">
            <template #default="{ row }">
              <strong class="resource-name">{{ row.resourceName }}</strong>
              <p class="resource-path">{{ row.chapterName }} / {{ row.sectionName }}</p>
            </template>
          </el-table-column>
          <el-table-column label="第几次" width="80">
            <template #default="{ row }">第 {{ row.attemptNumber }} 次</template>
          </el-table-column>
          <el-table-column label="得分" width="100">
            <template #default="{ row }">{{ row.score }} / {{ row.maxScore }}</template>
          </el-table-column>
          <el-table-column label="结果" width="90">
            <template #default="{ row }">
              <el-tag size="small" :type="row.passed ? 'success' : 'danger'">{{
                row.passed ? '达标' : '未达标'
              }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column label="提交时间" min-width="165">
            <template #default="{ row }">{{ dateTime(row.submittedAt) }}</template>
          </el-table-column>
          <el-table-column label="操作" width="95" fixed="right">
            <template #default="{ row }">
              <el-button link type="primary" :icon="View" @click="openAttempt(row as ClassroomStatsPracticeRow)"
                >答卷</el-button
              >
            </template>
          </el-table-column>
        </el-table>
      </el-tab-pane>

      <el-tab-pane label="案例实操历史">
        <el-table :data="detail.caseHistory" row-key="recordId" empty-text="暂无案例实操记录" max-height="480">
          <el-table-column label="资源" min-width="200">
            <template #default="{ row }">
              <strong class="resource-name">{{ row.resourceName }}</strong>
              <p class="resource-path">{{ row.chapterName }} / {{ row.sectionName }}</p>
            </template>
          </el-table-column>
          <el-table-column label="状态" width="100">
            <template #default="{ row }">
              <el-tag size="small" :type="row.status === 'completed' ? 'success' : 'warning'">
                {{ recordStatusLabels[row.status] || row.status }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column label="开始时间" min-width="165">
            <template #default="{ row }">{{ dateTime(row.startedAt) }}</template>
          </el-table-column>
          <el-table-column label="完成时间" min-width="165">
            <template #default="{ row }">{{ row.completedAt ? dateTime(row.completedAt) : '—' }}</template>
          </el-table-column>
          <el-table-column label="操作" width="95" fixed="right">
            <template #default="{ row }">
              <el-button link type="primary" :icon="View" @click="openCase(row.recordId)">记录</el-button>
            </template>
          </el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>

    <el-dialog v-model="attemptVisible" title="理论练习答卷" width="min(860px, 96vw)" append-to-body>
      <div v-if="attemptLoading" class="dialog-loading"><el-skeleton :rows="6" animated /></div>
      <template v-else-if="attempt">
        <div class="attempt-head">
          <div>
            <h3>{{ attempt.resource.name }}</h3>
            <p>
              {{ attempt.resource.chapterName }} / {{ attempt.resource.sectionName }} · 第
              {{ attempt.attemptNumber }} 次提交
            </p>
          </div>
          <div class="attempt-score">
            <strong>{{ attempt.score }} / {{ attempt.maxScore }}</strong>
            <el-tag :type="attempt.passed ? 'success' : 'danger'" size="small">{{
              attempt.passed ? '达标' : '未达标'
            }}</el-tag>
          </div>
        </div>
        <el-descriptions :column="2" size="small" border>
          <el-descriptions-item label="开始时间">{{ dateTime(attempt.startedAt) }}</el-descriptions-item>
          <el-descriptions-item label="结束时间">{{ dateTime(attempt.submittedAt) }}</el-descriptions-item>
          <el-descriptions-item label="练习次数">{{ attempt.attemptCount }}</el-descriptions-item>
          <el-descriptions-item label="平均分">{{ attempt.averageScore }}</el-descriptions-item>
          <el-descriptions-item label="最高得分">{{ attempt.bestScore }}</el-descriptions-item>
        </el-descriptions>
        <div class="attempt-questions">
          <article v-for="(question, index) in attempt.questions" :key="question.questionId" class="attempt-question">
            <div class="question-title">
              <span :class="question.correct ? 'correct' : 'wrong'">{{ question.correct ? '答对' : '答错' }}</span>
              <strong>{{ index + 1 }}. {{ question.stem }}</strong>
              <em>{{ question.score }} 分</em>
            </div>
            <p class="answer-line">
              <span>我的答案：{{ letters(question.selected) }}</span>
              <span>正确答案：{{ letters(question.correctAnswers) }}</span>
            </p>
            <p v-if="question.analysis" class="analysis">{{ question.analysis }}</p>
          </article>
        </div>
      </template>
    </el-dialog>

    <el-dialog v-model="caseVisible" title="案例实操记录" width="min(760px, 96vw)" append-to-body>
      <div v-if="caseLoading" class="dialog-loading"><el-skeleton :rows="6" animated /></div>
      <template v-else-if="caseRecord">
        <div class="case-head">
          <div>
            <h3>{{ caseRecord.resource.name }}</h3>
            <p>{{ caseRecord.resource.chapterName }} / {{ caseRecord.resource.sectionName }}</p>
          </div>
          <el-tag :type="caseRecord.status === 'completed' ? 'success' : 'warning'">
            {{ recordStatusLabels[caseRecord.status] || caseRecord.status }}
          </el-tag>
        </div>
        <el-descriptions :column="2" border size="small">
          <el-descriptions-item label="开始时间">{{ dateTime(caseRecord.startedAt) }}</el-descriptions-item>
          <el-descriptions-item label="完成时间">{{
            caseRecord.completedAt ? dateTime(caseRecord.completedAt) : '—'
          }}</el-descriptions-item>
          <el-descriptions-item label="启动节点">{{
            caseRecord.resource.startupNodeName || '未设置'
          }}</el-descriptions-item>
          <el-descriptions-item label="尝试次数">{{ caseRecord.attemptCount }}</el-descriptions-item>
        </el-descriptions>
        <div v-if="caseRecord.resource.topology" class="case-block">
          <h4>环境拓扑</h4>
          <pre>{{ caseRecord.resource.topology }}</pre>
        </div>
        <div v-if="caseRecord.resource.content" class="case-block">
          <h4>任务说明</h4>
          <p class="case-content">{{ caseRecord.resource.content }}</p>
        </div>
        <CaseGradeHistory :grades="caseRecord.grades" />
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.detail-summary {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 10px;
  margin-bottom: 12px;
}

.detail-summary article {
  padding: 14px;
  background: #f6faf8;
  border: 1px solid #e4eeea;
  border-radius: 10px;
}

.detail-summary span {
  display: block;
  margin-bottom: 4px;
  color: #56697c;
  font-size: 11px;
}

.detail-summary strong {
  color: #21425f;
  font-size: 24px;
}

.detail-summary strong small {
  font-size: 12px;
}

.detail-summary strong.remaining {
  font-size: 15px;
}

.detail-summary p {
  margin: 5px 0 0;
  color: #56697c;
  font-size: 10px;
}

.summary-main .el-tag {
  margin-left: 7px;
}

.kind-strip {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 9px;
  margin-bottom: 15px;
}

.kind-item {
  padding: 10px 12px;
  border: 1px solid #e4eeea;
  border-radius: 8px;
}

.kind-item > div {
  display: flex;
  justify-content: space-between;
  margin-bottom: 7px;
  color: #60768a;
  font-size: 11px;
}

.kind-item strong {
  color: var(--workspace-accent);
}

.kind-item p {
  margin: 6px 0 0;
  color: #56697c;
  font-size: 9px;
}

.resource-name {
  color: #2f4b66;
  font-size: 12px;
}

.resource-path {
  margin: 3px 0 0;
  color: #56697c;
  font-size: 10px;
}

.dialog-loading {
  padding: 15px 0;
}

.attempt-head,
.case-head {
  display: flex;
  justify-content: space-between;
  gap: 15px;
  align-items: flex-start;
  padding-bottom: 13px;
  border-bottom: 1px solid #e4eeea;
}

.attempt-head h3,
.case-head h3 {
  margin: 0 0 4px;
  color: #203f5d;
  font-size: 17px;
}

.attempt-head p,
.case-head p {
  margin: 0;
  color: #56697c;
  font-size: 11px;
}

.attempt-score {
  display: flex;
  align-items: center;
  gap: 8px;
}

.attempt-score strong {
  color: #1a514c;
  font-size: 18px;
}

.attempt-questions {
  display: grid;
  gap: 12px;
  margin-top: 14px;
  max-height: 62vh;
  overflow: auto;
}

.attempt-question {
  padding: 13px;
  background: #f5f9f8;
  border: 1px solid #e4eeea;
  border-radius: 9px;
}

.question-title {
  display: flex;
  gap: 8px;
  align-items: flex-start;
  color: #314e69;
  font-size: 12px;
  line-height: 1.6;
}

.question-title span {
  flex: none;
  padding: 1px 6px;
  font-size: 10px;
  border-radius: 4px;
}

.question-title .correct {
  color: #177c5e;
  background: #e6f7f0;
}

.question-title .wrong {
  color: #b43f32;
  background: #fceceb;
}

.question-title em {
  margin-left: auto;
  flex: none;
  color: #56697c;
  font-size: 10px;
  font-style: normal;
}

.answer-line {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 20px;
  margin: 8px 0 0;
  color: #56697c;
  font-size: 11px;
}

.analysis {
  margin: 8px 0 0;
  padding-top: 8px;
  color: #56697c;
  font-size: 11px;
  line-height: 1.6;
  border-top: 1px dashed #dce7eb;
}

.case-block {
  margin-top: 14px;
}

.case-block h4 {
  margin: 0 0 6px;
  color: #375773;
  font-size: 12px;
}

.case-block pre {
  margin: 0;
  padding: 10px;
  overflow: auto;
  color: #4e687f;
  font-size: 11px;
  background: #f6faf8;
  border: 1px solid #e4eeea;
  border-radius: 7px;
  white-space: pre-wrap;
}

.case-content {
  margin: 0;
  color: #5f768b;
  font-size: 12px;
  line-height: 1.7;
  white-space: pre-wrap;
}

@media (max-width: 900px) {
  .detail-summary,
  .kind-strip {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 560px) {
  .detail-summary,
  .kind-strip {
    grid-template-columns: 1fr;
  }
}
</style>

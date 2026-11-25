<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { DataAnalysis, Download, Refresh, Search, User } from '@element-plus/icons-vue'
import { api, dateTime, downloadFile, messageOf } from '../api'
import { remainingText, resourceProgressText } from '../analytics'
import type {
  ClassroomStatisticsResponse,
  ClassroomStatsStudentRow,
  ResourceStudentRow,
  ResourceStudentsResponse,
  StudentStatsDetail
} from '../analytics'
import { classroomStatusOptions, labelOf } from '../teaching'
import StudentStatsDetailView from './StudentStatsDetail.vue'
import TableEmpty from './TableEmpty.vue'

const props = defineProps<{ visible: boolean; classroomId: string }>()
const emit = defineEmits<{ 'update:visible': [value: boolean] }>()

const loading = ref(false)
const error = ref('')
const data = ref<ClassroomStatisticsResponse | null>(null)
const activeTab = ref<'overview' | 'resources' | 'students'>('overview')
const studentSearch = ref('')
const chapterFilter = ref('')
const studentChapterId = ref('')
const chapterStudents = ref<ClassroomStatsStudentRow[] | null>(null)
const chapterLoading = ref(false)
const chapterError = ref('')
let chapterRequest = 0
const expandedChapters = ref<string[]>([])

const detailVisible = ref(false)
const detailLoading = ref(false)
const detail = ref<StudentStatsDetail | null>(null)

const resourceVisible = ref(false)
const resourceLoading = ref(false)
const resourceDetail = ref<ResourceStudentsResponse | null>(null)
const exporting = ref('')

const kindRows = computed(() => {
  if (!data.value) return []
  const summary = data.value.summary
  return [
    { key: 'theory', label: '理论知识', stats: summary.theory },
    { key: 'theoryPractice', label: '理论练习', stats: summary.theoryPractice },
    { key: 'case', label: '案例教学', stats: summary.case },
    { key: 'casePractice', label: '案例实操', stats: summary.casePractice }
  ]
})

const filteredStudents = computed(() => {
  const keyword = studentSearch.value.trim().toLowerCase()
  if (!data.value) return []
  const rows = studentChapterId.value ? chapterStudents.value || [] : data.value.students
  if (!keyword) return rows
  return rows.filter(student =>
    [student.name, student.number, student.className, student.userName]
      .filter(Boolean)
      .some(value => String(value).toLowerCase().includes(keyword))
  )
})

const studentKinds = [
  { key: 'theory', label: '理论讲解' },
  { key: 'theoryPractice', label: '理论练习' },
  { key: 'case', label: '案例讲解' },
  { key: 'casePractice', label: '案例实操' }
] as const

const attentionStudents = computed(() => {
  if (!data.value) return []
  return [...data.value.students]
    .filter(student => student.overall.total > 0 && !isOverallPassing(student))
    .sort((a, b) => a.overall.passRate - b.overall.passRate)
    .slice(0, 5)
})

const attentionEmptyTitle = computed(() => {
  if (!data.value?.students.length) return '课堂暂无学生'
  if (!data.value.students.some(student => student.overall.total > 0)) return '学生尚未开始学习'
  return '暂无需要重点关注的学生'
})

const attentionEmptyHint = computed(() => {
  if (!data.value?.students.length) return '加入学生后，达标率靠后的学生会显示在这里。'
  if (!data.value.students.some(student => student.overall.total > 0)) return '有学习记录且未达标的学生会出现在这里。'
  return '当前学生均已达标，可在“学生维度”查看全部数据。'
})

const studentFiltering = computed(() => !!(studentSearch.value.trim() || studentChapterId.value))

function clearStudentFilters() {
  studentSearch.value = ''
  if (studentChapterId.value) {
    studentChapterId.value = ''
    chapterStudents.value = null
    chapterError.value = ''
  }
}

const visibleChapters = computed(() => {
  if (!data.value) return []
  if (!chapterFilter.value) return data.value.chapters
  return data.value.chapters.filter(chapter => chapter.id === chapterFilter.value)
})

const chapterRows = computed(() => {
  if (!data.value) return []
  return data.value.chapters.map(chapter => {
    const resources = chapter.sections.flatMap(section => section.resources)
    const slots = resources.length * data.value!.classroom.studentCount
    const learned = resources.reduce((sum, item) => sum + item.learnedCount, 0)
    const passed = resources.reduce((sum, item) => sum + item.passedCount, 0)
    return {
      id: chapter.id,
      name: chapter.name,
      resourceCount: resources.length,
      learned,
      passed,
      learningRate: slots ? Math.round((learned * 100) / slots) : 0,
      passRate: slots ? Math.round((passed * 100) / slots) : 0
    }
  })
})

function isOverallPassing(student: ClassroomStatsStudentRow) {
  return student.overall.total > 0 && student.overall.passed * 100 >= student.overall.total * 60
}

function studentState(student: ClassroomStatsStudentRow) {
  return isOverallPassing(student)
    ? { text: '已达标', type: 'success' as const }
    : { text: '未达标', type: 'warning' as const }
}

async function load() {
  if (!props.classroomId) return
  loading.value = true
  error.value = ''
  try {
    data.value = await api<ClassroomStatisticsResponse>(`/classrooms/${props.classroomId}/statistics`)
    expandedChapters.value = data.value.chapters.map(chapter => chapter.id)
    if (studentChapterId.value) await loadStudentChapter()
  } catch (failure) {
    error.value = messageOf(failure)
    data.value = null
  } finally {
    loading.value = false
  }
}

async function loadStudentChapter() {
  const request = ++chapterRequest
  chapterStudents.value = null
  chapterError.value = ''
  chapterLoading.value = !!studentChapterId.value
  if (!studentChapterId.value) return
  try {
    const params = new URLSearchParams({ chapterId: studentChapterId.value })
    const stats = await api<ClassroomStatisticsResponse>(`/classrooms/${props.classroomId}/statistics?${params}`)
    if (request === chapterRequest) chapterStudents.value = stats.students
  } catch (failure) {
    if (request === chapterRequest) chapterError.value = messageOf(failure)
  } finally {
    if (request === chapterRequest) chapterLoading.value = false
  }
}

async function openStudent(userId: string) {
  if (!props.classroomId) return
  detailVisible.value = true
  detailLoading.value = true
  detail.value = null
  try {
    detail.value = await api<StudentStatsDetail>(`/classrooms/${props.classroomId}/students/${userId}/statistics`)
  } catch (failure) {
    ElMessage.error(messageOf(failure))
    detailVisible.value = false
  } finally {
    detailLoading.value = false
  }
}

async function openResource(resourceId: string) {
  if (!props.classroomId) return
  resourceVisible.value = true
  resourceLoading.value = true
  resourceDetail.value = null
  try {
    resourceDetail.value = await api<ResourceStudentsResponse>(
      `/classrooms/${props.classroomId}/statistics/resources/${resourceId}/students`
    )
  } catch (failure) {
    ElMessage.error(messageOf(failure))
    resourceVisible.value = false
  } finally {
    resourceLoading.value = false
  }
}

function resourceStudentType(row: ResourceStudentRow) {
  return !row.learned ? 'info' : row.passed ? 'success' : 'warning'
}

function resourceStudentLabel(row: ResourceStudentRow) {
  return !row.learned ? '未学习' : row.passed ? '已达标' : '学习中'
}

async function exportCsv(kind: 'resources' | 'students') {
  if (!props.classroomId) return
  exporting.value = kind
  try {
    const params = new URLSearchParams()
    const chapter = kind === 'students' ? studentChapterId.value : chapterFilter.value
    if (chapter) params.set('chapterId', chapter)
    if (kind === 'students' && studentSearch.value.trim()) params.set('search', studentSearch.value.trim())
    const report = kind === 'resources' ? '课堂资源学习记录' : '课堂学生维度统计'
    await downloadFile(
      `/classrooms/${props.classroomId}/statistics/exports/${kind}?${params}`,
      `${report}-${data.value?.classroom.name || ''}.csv`
    )
  } catch (failure) {
    ElMessage.error(messageOf(failure))
  } finally {
    exporting.value = ''
  }
}

watch(
  () => [props.visible, props.classroomId] as const,
  ([visible, id]) => {
    ++chapterRequest
    chapterStudents.value = null
    chapterError.value = ''
    chapterLoading.value = false
    if (visible && id) {
      activeTab.value = 'overview'
      studentSearch.value = ''
      chapterFilter.value = ''
      studentChapterId.value = ''
      void load()
    }
  },
  { immediate: true }
)
</script>

<template>
  <el-drawer
    :model-value="visible"
    size="min(1180px, 100vw)"
    :title="data ? `${data.classroom.name} · 学情` : '学情'"
    :with-header="false"
    append-to-body
    body-class="classroom-stats-drawer-body"
    @update:model-value="emit('update:visible', $event)"
  >
    <p class="empty-hint">
      理论练习达标与技能进度按提交时保存的得分率计算，不随当前题目满分变化。缺少可靠历史满分的旧记录保留原始成绩，但暂不计为已达标；得分显示“—”，请联系管理员核实。
    </p>
    <div v-loading="loading" class="stats-body">
      <header class="stats-head">
        <div>
          <div class="eyebrow">学情</div>
          <h2>{{ data?.classroom.name || '学情' }}</h2>
          <p v-if="data">
            {{ data.classroom.courseName }} · {{ data.classroom.teacherName }} ·
            <el-tag size="small" :type="data.classroom.status === 'open' ? 'success' : 'info'">
              {{ labelOf(classroomStatusOptions, data.classroom.status) }}
            </el-tag>
            <span class="remaining">剩余 {{ remainingText(data.remainingSeconds) }}</span>
          </p>
        </div>
        <div class="stats-actions">
          <el-button @click="emit('update:visible', false)">关闭</el-button>
          <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
          <el-button
            v-if="data && activeTab === 'overview'"
            type="primary"
            :icon="Download"
            :loading="exporting === 'students'"
            class="overview-export"
            @click="exportCsv('students')"
          >
            导出学生维度
          </el-button>
        </div>
      </header>

      <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="stats-notice" />

      <template v-if="data">
        <el-tabs v-model="activeTab" class="stats-tabs">
          <el-tab-pane label="总览" name="overview">
            <div class="summary-grid">
              <article class="summary-card summary-main">
                <span>整体达标率</span>
                <strong>{{ data.summary.overall.passRate }}<small>%</small></strong>
                <el-progress :percentage="data.summary.overall.passRate" :stroke-width="7" :show-text="false" />
                <p>
                  {{ data.summary.overall.passedStudents }} / {{ data.summary.overall.totalStudents }} 名学生达标 ·
                  已学习 {{ data.summary.overall.learnedResources }} / {{ data.summary.overall.totalResources }} 项
                </p>
              </article>
              <article v-for="row in kindRows" :key="row.key" class="summary-card">
                <span>{{ row.label }}</span>
                <strong>{{ row.stats.passRate }}<small>%</small></strong>
                <el-progress
                  :percentage="row.stats.passRate"
                  :stroke-width="7"
                  :show-text="false"
                  :color="row.stats.passRate >= 60 ? '#2f9d7a' : '#d6a347'"
                />
                <p>
                  达标 {{ row.stats.passedStudents }} 人 · 学习率 {{ row.stats.learningRate }}% ·
                  {{ row.stats.passedResources }} / {{ row.stats.totalResources }} 项达标
                </p>
              </article>
            </div>
            <div class="overview-grid">
              <section class="surface overview-chapters">
                <div class="panel-head">
                  <div>
                    <h3>章节达标进度</h3>
                    <p>按课堂全部学生与资源统计</p>
                  </div>
                </div>
                <div class="chapter-progress-list">
                  <div v-for="chapter in chapterRows" :key="chapter.id" class="chapter-progress">
                    <div class="chapter-progress-head">
                      <strong>{{ chapter.name }}</strong>
                      <span
                        >{{ chapter.passed }} / {{ chapter.resourceCount * data.classroom.studentCount }} 人次达标</span
                      >
                    </div>
                    <el-progress
                      :percentage="chapter.passRate"
                      :stroke-width="8"
                      :color="chapter.passRate >= 60 ? '#2f9d7a' : '#d6a347'"
                    />
                    <p>学习率 {{ chapter.learningRate }}% · 达标率 {{ chapter.passRate }}%</p>
                  </div>
                  <el-empty v-if="!chapterRows.length" description="课程暂无章节资源" :image-size="70">
                    <p class="empty-hint">请先在课堂内容中配置课程章节和资源，再查看达标进度。</p>
                  </el-empty>
                </div>
              </section>

              <section class="surface overview-attention">
                <div class="panel-head">
                  <div>
                    <h3>重点关注</h3>
                    <p>达标率靠后的学生</p>
                  </div>
                  <el-icon :size="22"><DataAnalysis /></el-icon>
                </div>
                <button
                  v-for="student in attentionStudents"
                  :key="student.userId"
                  type="button"
                  class="attention-row"
                  @click="openStudent(student.userId)"
                >
                  <span class="attention-avatar"
                    ><el-icon><User /></el-icon
                  ></span>
                  <span class="attention-name"
                    >{{ student.name }}<small>{{ student.number }}</small></span
                  >
                  <span class="attention-rate">{{ student.overall.passRate }}%</span>
                  <el-tag size="small" type="warning">未达标</el-tag>
                </button>
                <el-empty v-if="!attentionStudents.length" :description="attentionEmptyTitle" :image-size="70">
                  <p class="empty-hint">{{ attentionEmptyHint }}</p>
                </el-empty>
                <el-button text type="primary" @click="activeTab = 'students'">查看全部学生数据</el-button>
              </section>
            </div>
          </el-tab-pane>

          <el-tab-pane name="resources">
            <template #label>资源维度</template>
            <div class="tab-toolbar">
              <el-select
                v-model="chapterFilter"
                aria-label="资源统计章节"
                placeholder="全部章节"
                clearable
                class="chapter-filter"
              >
                <el-option
                  v-for="chapter in data.chapters"
                  :key="chapter.id"
                  :label="chapter.name"
                  :value="chapter.id"
                />
              </el-select>
              <el-button
                class="tab-export"
                :icon="Download"
                :loading="exporting === 'resources'"
                @click="exportCsv('resources')"
              >
                导出资源维度
              </el-button>
            </div>
            <el-empty
              v-if="!visibleChapters.length"
              :description="chapterFilter ? '当前筛选下没有章节' : '暂无章节资源'"
              :image-size="80"
            >
              <p v-if="!chapterFilter" class="empty-hint">请先在课堂内容中配置课程章节和资源。</p>
              <el-button v-else @click="chapterFilter = ''">清除筛选</el-button>
            </el-empty>
            <el-collapse v-else v-model="expandedChapters" class="resource-collapse">
              <el-collapse-item v-for="chapter in visibleChapters" :key="chapter.id" :name="chapter.id">
                <template #title>
                  <strong class="collapse-title">{{ chapter.name }}</strong>
                  <span class="collapse-count">
                    {{ chapter.sections.reduce((sum, section) => sum + section.resources.length, 0) }} 项资源
                  </span>
                </template>
                <div v-for="section in chapter.sections" :key="section.id" class="resource-section">
                  <h4>{{ section.name }}</h4>
                  <section class="table-region resource-table-region" :aria-label="section.name">
                    <p v-if="section.resources.length" class="table-scroll-hint">横向滚动查看更多指标</p>
                    <el-table
                      :data="section.resources"
                      row-key="id"
                      native-scrollbar
                      :scrollbar-tabindex="0"
                      size="small"
                      empty-text="本节暂无资源"
                    >
                      <el-table-column label="资源" min-width="164" fixed>
                        <template #default="{ row }">
                          <strong class="resource-name">{{ row.name }}</strong>
                          <p class="resource-meta">{{ row.kindLabel }} · {{ row.durationMinutes }} 分钟</p>
                          <el-button link type="primary" @click="openResource(row.id)">名单</el-button>
                        </template>
                      </el-table-column>
                      <el-table-column label="学习人数 / 学习率" min-width="146">
                        <template #default="{ row }">
                          <div class="resource-rate">
                            <span>{{ row.learnedCount }} / {{ data?.classroom.studentCount }} 人</span>
                            <strong>{{ row.learningRate }}%</strong>
                          </div>
                          <el-progress :percentage="row.learningRate" :stroke-width="5" :show-text="false" />
                        </template>
                      </el-table-column>
                      <el-table-column label="达标人数 / 达标率" min-width="146">
                        <template #default="{ row }">
                          <div class="resource-rate">
                            <span>{{ row.passedCount }} / {{ data?.classroom.studentCount }} 人</span>
                            <strong>{{ row.passRate }}%</strong>
                          </div>
                          <el-progress
                            :percentage="row.passRate"
                            :stroke-width="5"
                            :show-text="false"
                            :color="row.passRate >= 60 ? '#2f9d7a' : '#d6a347'"
                          />
                        </template>
                      </el-table-column>
                    </el-table>
                  </section>
                </div>
              </el-collapse-item>
            </el-collapse>
          </el-tab-pane>

          <el-tab-pane label="学生维度" name="students">
            <div class="tab-toolbar student-toolbar">
              <el-select
                v-model="studentChapterId"
                aria-label="学生统计大章"
                placeholder="全部大章"
                clearable
                class="chapter-filter"
                @change="loadStudentChapter"
              >
                <el-option
                  v-for="chapter in data.chapters"
                  :key="chapter.id"
                  :label="chapter.name"
                  :value="chapter.id"
                />
              </el-select>
              <el-input
                v-model="studentSearch"
                class="student-search"
                aria-label="搜索姓名、学号或班级"
                placeholder="搜索姓名、学号或班级"
                clearable
                :prefix-icon="Search"
              />
              <el-button
                class="tab-export"
                :icon="Download"
                :loading="exporting === 'students'"
                :disabled="chapterLoading"
                @click="exportCsv('students')"
              >
                导出学生维度
              </el-button>
            </div>
            <div class="student-table-caption">
              <span>共 {{ filteredStudents.length }} 名学生</span>
              <span>资源达标线为完成度 60%；理论练习另按单次得分 60% 计</span>
            </div>
            <el-alert
              v-if="chapterError"
              :title="chapterError"
              type="error"
              show-icon
              :closable="false"
              class="tab-error"
            />
            <section class="table-region student-table-region" aria-label="学生维度表格">
              <p v-if="filteredStudents.length" class="table-scroll-hint">横向滚动查看更多指标</p>
              <el-table
                v-loading="chapterLoading"
                :data="filteredStudents"
                row-key="userId"
                size="small"
                max-height="min(64vh, 40rem)"
                native-scrollbar
                :scrollbar-tabindex="0"
                empty-text=""
                @row-click="row => openStudent(row.userId)"
              >
                <template #empty>
                  <TableEmpty
                    v-if="!chapterLoading"
                    :title="studentFiltering ? '暂无符合条件的学生' : '课堂暂无学生'"
                    :hint="
                      studentFiltering
                        ? '试试调整关键词或章节，或清除筛选查看全部学生。'
                        : '加入学生后，这里会显示学习率、达标情况与最近学习记录。'
                    "
                  >
                    <template v-if="studentFiltering" #action>
                      <el-button @click="clearStudentFilters">清除筛选</el-button>
                    </template>
                  </TableEmpty>
                </template>
                <el-table-column label="学生" min-width="156" fixed>
                  <template #default="{ row }">
                    <strong class="student-name">{{ row.name }}</strong>
                    <p class="student-meta">{{ row.number }} · {{ row.className || '未分班' }}</p>
                    <el-button link type="primary" @click.stop="openStudent(row.userId)">详情</el-button>
                  </template>
                </el-table-column>
                <el-table-column label="整体" width="132" sortable :sort-by="row => row.overall.passRate">
                  <template #default="{ row }">
                    <div class="overall-cell">
                      <el-tag size="small" :type="studentState(row as ClassroomStatsStudentRow).type">{{
                        studentState(row as ClassroomStatsStudentRow).text
                      }}</el-tag>
                      <span>学习 {{ row.overall.learningRate }}%</span>
                      <span>达标 {{ row.overall.passRate }}%</span>
                    </div>
                  </template>
                </el-table-column>
                <el-table-column
                  v-for="kind in studentKinds"
                  :key="kind.key"
                  :label="kind.label"
                  width="110"
                  sortable
                  :sort-by="row => row[kind.key].passRate"
                >
                  <template #default="{ row }">
                    <div>学习 {{ row[kind.key].learningRate }}%</div>
                    <div>达标 {{ row[kind.key].passRate }}%</div>
                  </template>
                </el-table-column>
                <el-table-column prop="activeDays" label="活跃天数" width="95" sortable />
                <el-table-column label="最近学习" min-width="165" sortable :sort-by="row => row.lastActivity || ''">
                  <template #default="{ row }">{{
                    row.lastActivity ? dateTime(row.lastActivity) : '暂无记录'
                  }}</template>
                </el-table-column>
              </el-table>
            </section>
          </el-tab-pane>
        </el-tabs>
      </template>
      <el-empty v-else-if="!loading && !error" description="暂无课堂学情数据">
        <p class="empty-hint">课堂数据加载后，将在这里展示章节、资源与学生的学情。</p>
      </el-empty>
    </div>

    <el-dialog
      v-model="detailVisible"
      :title="detail ? `${detail.student.name} · 学情` : '学生学情'"
      width="min(1080px, 96vw)"
      append-to-body
    >
      <div v-if="detailLoading"><el-skeleton :rows="8" animated /></div>
      <StudentStatsDetailView v-else-if="detail" :detail="detail" />
    </el-dialog>

    <el-dialog
      v-model="resourceVisible"
      :title="resourceDetail ? `${resourceDetail.resource.name} · 学生名单` : '资源学生名单'"
      width="min(880px, 96vw)"
      append-to-body
    >
      <div v-if="resourceLoading"><el-skeleton :rows="6" animated /></div>
      <template v-else-if="resourceDetail">
        <div class="resource-detail-head">
          <div>
            <h3>{{ resourceDetail.resource.name }}</h3>
            <p>{{ resourceDetail.resource.chapterName }} / {{ resourceDetail.resource.sectionName }}</p>
          </div>
          <div class="resource-detail-metrics">
            <span
              >学习率 <strong>{{ resourceDetail.learningRate }}%</strong></span
            >
            <span
              >达标率 <strong>{{ resourceDetail.passRate }}%</strong></span
            >
          </div>
        </div>
        <section class="table-region roster-table-region" aria-label="资源学生名单表格">
          <p v-if="resourceDetail.items.length" class="table-scroll-hint">横向滚动查看更多指标</p>
          <el-table
            :data="resourceDetail.items"
            size="small"
            row-key="userId"
            max-height="55vh"
            native-scrollbar
            :scrollbar-tabindex="0"
          >
            <el-table-column label="学生" min-width="150" fixed>
              <template #default="{ row }">
                <strong class="student-name">{{ row.name }}</strong>
                <p class="student-meta">{{ row.number }} · {{ row.className || '未分班' }}</p>
              </template>
            </el-table-column>
            <el-table-column label="状态" width="90">
              <template #default="{ row }">
                <el-tag size="small" :type="resourceStudentType(row as ResourceStudentRow)">{{
                  resourceStudentLabel(row as ResourceStudentRow)
                }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column label="成绩" width="90">
              <template #default="{ row }">{{
                resourceProgressText(row.bestScore, row.maxScore, resourceDetail.resource.kind, row.attemptCount)
              }}</template>
            </el-table-column>
            <el-table-column prop="attemptCount" label="练习次数" width="90" />
            <el-table-column label="最近学习" min-width="165">
              <template #default="{ row }">{{ row.lastActivity ? dateTime(row.lastActivity) : '暂无记录' }}</template>
            </el-table-column>
          </el-table>
        </section>
      </template>
    </el-dialog>
  </el-drawer>
</template>

<style scoped>
:global(.el-drawer__body.classroom-stats-drawer-body) {
  padding: 0;
}

.stats-body {
  min-height: 100%;
  padding: 22px 26px 30px;
}

.stats-head {
  display: flex;
  justify-content: space-between;
  gap: 20px;
  align-items: flex-start;
  padding-bottom: 16px;
  border-bottom: 1px solid #e4eeea;
}

.stats-head h2 {
  margin: 5px 0 6px;
  color: #183a56;
  font-size: 22px;
  overflow-wrap: anywhere;
}

.stats-head p {
  display: flex;
  flex-wrap: wrap;
  gap: 7px;
  align-items: center;
  margin: 0;
  color: #56697c;
  font-size: 12px;
}

.stats-head .remaining {
  color: #4b6e8c;
}

.stats-actions {
  display: flex;
  flex: none;
  flex-wrap: wrap;
  gap: 8px;
}

.stats-actions > .el-button + .el-button {
  margin-inline-start: 0;
}

.stats-notice {
  margin-top: 15px;
}

.summary-grid {
  display: grid;
  grid-template-columns: 1.25fr repeat(4, minmax(0, 1fr));
  gap: 10px;
  margin: 0 0 16px;
}

.summary-card {
  padding: 15px;
  background: #fff;
  border: 1px solid #dce7eb;
  border-radius: 11px;
  box-shadow: 0 2px 8px rgba(29, 62, 91, 0.035);
}

.summary-card > span {
  color: var(--el-text-color-regular);
  font-size: 13px;
}

.summary-card > strong {
  display: block;
  margin: 3px 0 8px;
  color: #214561;
  font-size: 28px;
  font-variant-numeric: tabular-nums;
  line-height: 1;
}

.summary-card strong small {
  font-size: 12px;
}

.summary-card p {
  margin: 8px 0 0;
  color: var(--el-text-color-regular);
  font-size: 12px;
  line-height: 1.55;
}

.summary-main {
  border-color: var(--el-color-primary-light-7);
  background: linear-gradient(140deg, var(--el-color-primary-light-9), #fff);
}

.summary-main > strong {
  color: var(--el-color-primary-dark-2);
  font-size: 34px;
}

.stats-tabs {
  margin-top: 10px;
}

.stats-tabs :deep(.el-tabs__content) {
  min-inline-size: 0;
}

.overview-grid {
  display: grid;
  grid-template-columns: minmax(0, 1.5fr) minmax(280px, 1fr);
  gap: 14px;
}

.overview-chapters,
.overview-attention {
  padding: 17px;
}

.panel-head {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: flex-start;
  margin-bottom: 13px;
  color: #56697c;
}

.panel-head h3 {
  margin: 0 0 3px;
  color: #294d69;
  font-size: 14px;
}

.panel-head p {
  margin: 0;
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.chapter-progress-list {
  display: grid;
  gap: 15px;
}

.chapter-progress-head {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 6px;
  color: #355774;
  font-size: 13px;
}

.chapter-progress-head span,
.chapter-progress p {
  color: var(--el-text-color-regular);
  font-size: 11px;
}

.chapter-progress p {
  margin: 4px 0 0;
}

.attention-row {
  display: flex;
  align-items: center;
  gap: 9px;
  width: 100%;
  padding: 10px 8px;
  margin-bottom: 6px;
  background: #f6faf8;
  border: 1px solid #e4eeea;
  border-radius: 8px;
  cursor: pointer;
  text-align: left;
  transition: border-color 0.15s ease;
}

.attention-row:hover {
  border-color: #bdd9d3;
}

.attention-avatar {
  display: grid;
  width: 27px;
  height: 27px;
  place-items: center;
  color: #0f5f58;
  background: #d8efe9;
  border-radius: 50%;
}

.attention-name {
  display: flex;
  flex-direction: column;
  color: #2d4d68;
  font-size: 12px;
}

.attention-name small {
  color: #56697c;
  font-size: 9px;
}

.attention-rate {
  margin-left: auto;
  color: #94610f;
  font-size: 13px;
  font-weight: 600;
}

.tab-toolbar {
  display: grid;
  grid-template-columns: minmax(140px, 220px) minmax(0, 1fr) auto;
  align-items: center;
  gap: 10px;
  margin-bottom: 12px;
}

.chapter-filter,
.student-search {
  inline-size: 100%;
}

.student-search {
  max-inline-size: 280px;
}

.tab-export {
  grid-column: -2;
  justify-self: end;
}

.student-table-caption {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 12px;
  margin-bottom: 9px;
  color: var(--el-text-color-regular);
  font-size: 13px;
}

.tab-error {
  margin-bottom: 10px;
}

.table-region {
  container-type: inline-size;
  min-inline-size: 0;
}

.student-table-region {
  container-name: student-stat-table;
}

.resource-table-region {
  container-name: resource-stat-table;
}

.roster-table-region {
  container-name: resource-roster-table;
}

.table-scroll-hint {
  display: none;
  margin: 0 0 6px;
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.empty-hint {
  max-width: 36ch;
  margin: 8px auto 0;
  color: var(--el-text-color-regular);
  font-size: 12px;
  line-height: 1.5;
  text-align: center;
}

.table-region :deep(.el-scrollbar__wrap:focus-visible) {
  outline: 2px solid var(--el-color-primary);
  outline-offset: -2px;
}

.table-region :deep(.el-table .cell) {
  padding-inline: 10px;
  overflow-wrap: anywhere;
}

.table-region :deep(.el-button.is-link) {
  margin-block-start: 1px;
  min-block-size: 32px;
}

@container student-stat-table (max-width: 1005px) {
  .table-scroll-hint {
    display: block;
  }
}

@container resource-stat-table (max-width: 455px) {
  .table-scroll-hint {
    display: block;
  }
}

@container resource-roster-table (max-width: 600px) {
  .table-scroll-hint {
    display: block;
  }
}

.resource-collapse {
  background: #fff;
  border: 1px solid #e4eeea;
  border-radius: 10px;
  overflow: hidden;
}

.resource-collapse :deep(.el-collapse-item__header) {
  min-block-size: 48px;
  block-size: auto;
  padding: 8px 12px;
  line-height: 1.5;
  text-align: left;
}

.collapse-title {
  min-inline-size: 0;
  margin-right: 10px;
  overflow-wrap: anywhere;
  color: #2d4f6b;
  font-size: 13px;
}

.collapse-count {
  flex: none;
  color: var(--el-text-color-regular);
  font-size: 11px;
}

.resource-section {
  padding: 4px 14px 14px;
}

.resource-section h4 {
  margin: 8px 0 7px;
  color: #5b748a;
  font-size: 11px;
}

.resource-name,
.student-name {
  color: #2c4b66;
  font-size: 13px;
}

.resource-meta,
.student-meta {
  margin: 3px 0 0;
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.resource-rate {
  display: flex;
  justify-content: space-between;
  gap: 6px;
  margin-bottom: 5px;
  font-variant-numeric: tabular-nums;
}

.overall-cell {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  align-items: start;
  gap: 4px;
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.overall-cell :deep(.el-tag) {
  grid-column: 1 / -1;
  justify-self: start;
}

.resource-detail-head {
  display: flex;
  justify-content: space-between;
  gap: 15px;
  align-items: flex-start;
  padding-bottom: 13px;
  margin-bottom: 12px;
  border-bottom: 1px solid #e4eeea;
}

.resource-detail-head h3 {
  margin: 0 0 4px;
  color: #23425e;
  font-size: 17px;
}

.resource-detail-head p {
  margin: 0;
  color: #56697c;
  font-size: 11px;
}

.resource-detail-metrics {
  display: flex;
  gap: 15px;
  color: #56697c;
  font-size: 11px;
}

.resource-detail-metrics strong {
  color: var(--workspace-accent);
  font-size: 16px;
}

@media (max-width: 1050px) {
  .summary-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .summary-main {
    grid-column: 1 / -1;
  }

  .overview-grid {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 640px) {
  .stats-body {
    padding: 16px 13px 24px;
  }

  .stats-head {
    flex-direction: column;
    gap: 12px;
  }

  .stats-actions {
    inline-size: 100%;
  }

  .stats-actions > .el-button.overview-export {
    margin-inline-start: auto;
  }

  .summary-card {
    padding: 12px;
  }

  .summary-card > strong {
    font-size: 24px;
  }

  .summary-main > strong {
    font-size: 32px;
  }

  .tab-toolbar {
    grid-template-columns: minmax(0, 1fr) auto;
    gap: 8px;
  }

  .student-toolbar .chapter-filter {
    grid-column: 1 / -1;
  }

  .student-search {
    max-inline-size: none;
  }

  .resource-section {
    padding-inline: 9px;
  }

  .resource-detail-head {
    flex-wrap: wrap;
    gap: 8px;
  }
}
</style>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { DataAnalysis, Refresh, School, User } from '@element-plus/icons-vue'
import { api, dateTime, messageOf } from '../api'
import { flattenSkills, resourceProgressText, skillStateLabels, skillStateType } from '../analytics'
import type {
  SkillGraphResource,
  SkillGraphResponse,
  SkillGraphSkill,
  SkillGraphStudentOption,
  SkillGraphStudentsResponse
} from '../analytics'

const props = defineProps<{ role?: string; initialStudentId?: string }>()
const emit = defineEmits<{ navigate: [key: string, params?: Record<string, string>] }>()

const isStaff = computed(() => props.role === 'Admin' || props.role === 'Teacher')
const loading = ref(false)
const error = ref('')
const data = ref<SkillGraphResponse | null>(null)

const students = ref<SkillGraphStudentOption[]>([])
const studentLoading = ref(false)
const selectedUserId = ref(props.initialStudentId || '')
const classId = ref('')
const classes = ref<SkillGraphStudentsResponse['classes']>([])
let studentRequest = 0
let graphRequest = 0

const view = ref<'map' | 'radar' | 'bars' | 'graph'>('map')
const categoryFilter = ref('')
const selectedSkillId = ref('')
const zoom = ref(1)
const panX = ref(0)
const panY = ref(0)
const panning = ref(false)
let drag = { x: 0, y: 0, panX: 0, panY: 0, scale: 1 }

const palette = ['#327fb2', '#2f9d7a', '#be882f', '#8068ad', '#bf5f6e', '#4d8f91', '#708f40', '#a86d97']

const categoryIndex = computed(
  () => new Map((data.value?.categories || []).map((category, index) => [category.id, index]))
)

function colorOf(categoryId: string) {
  return palette[(categoryIndex.value.get(categoryId) ?? 0) % palette.length]
}

const filteredCategories = computed(() => {
  if (!data.value) return []
  if (!categoryFilter.value) return data.value.categories
  return data.value.categories.filter(category => category.id === categoryFilter.value)
})

const allSkills = computed(() => flattenSkills(filteredCategories.value))

const selectedSkill = computed(() => allSkills.value.find(skill => skill.id === selectedSkillId.value) || null)

const scopeText = computed(() => {
  if (!data.value) return ''
  const count = data.value.scope.classroomCount
  return data.value.scope.teacherScoped ? `教师管理范围内的 ${count} 个课堂` : `已加入的 ${count} 个课堂`
})

const summaryRows = computed(() => {
  const summary = data.value?.summary
  if (!summary) return []
  return [
    { label: '完整目标', value: `${summary.fullScore} 分`, tone: 'plain' },
    { label: '当前目标', value: `${summary.currentTarget} 分`, tone: 'plain' },
    { label: '实际掌握', value: `${summary.score} 分`, tone: 'success' },
    { label: '提升空间', value: `${Math.max(0, summary.fullScore - summary.score)} 分`, tone: 'warning' },
    { label: '已掌握技能', value: summary.masteredSkills, tone: 'success' },
    { label: '学习中技能', value: summary.learningSkills, tone: 'warning' },
    { label: '未开始技能', value: summary.notStartedSkills, tone: 'info' },
    { label: '已达标资源', value: `${summary.passedResources} / ${summary.totalResources}`, tone: 'plain' }
  ]
})

const scoreRing = computed(() => {
  const score = Math.max(0, Math.min(100, data.value?.summary.score || 0))
  return { background: `conic-gradient(#0f7375 ${score * 3.6}deg, #e3f3ee 0deg)` }
})

function shorten(value: string, max: number) {
  return value.length > max ? `${value.slice(0, max)}…` : value
}

interface MapNode {
  id: string
  kind: 'category' | 'chapter' | 'skill'
  label: string
  x: number
  y: number
  w: number
  h: number
  color: string
  categoryId: string
  chapterId?: string
  skill?: SkillGraphSkill
  score?: number
  skillCount?: number
  masteredCount?: number
}

interface MapLink {
  id: string
  d: string
  color: string
}

const MAP_WIDTH = 800

const mapLayout = computed(() => {
  const categories = filteredCategories.value
  const nodes: MapNode[] = []
  const links: MapLink[] = []
  const bands: { id: string; y: number; height: number; color: string }[] = []
  let cursor = 22
  categories.forEach(category => {
    const color = colorOf(category.id)
    const start = cursor
    let chapterCursor = start + 14
    const placements = category.chapters.map(chapter => {
      const height = Math.max(52, chapter.skills.length * 42)
      const placement = { chapter, top: chapterCursor, height, center: chapterCursor + height / 2 }
      chapterCursor += height + 12
      return placement
    })
    const bandHeight = Math.max(88, chapterCursor - start)
    const categoryCenter = start + bandHeight / 2
    nodes.push({
      id: `category-${category.id}`,
      kind: 'category',
      label: category.name,
      x: 12,
      y: categoryCenter - 27,
      w: 176,
      h: 54,
      color,
      categoryId: category.id,
      score: category.score,
      skillCount: category.skillCount,
      masteredCount: category.masteredCount
    })
    placements.forEach(placement => {
      const chapter = placement.chapter
      nodes.push({
        id: `chapter-${chapter.id}`,
        kind: 'chapter',
        label: chapter.name,
        x: 238,
        y: placement.center - 21,
        w: 188,
        h: 42,
        color,
        categoryId: category.id,
        chapterId: chapter.id,
        skillCount: chapter.skills.length
      })
      links.push({
        id: `link-c-${chapter.id}`,
        d: `M188 ${categoryCenter} C210 ${categoryCenter}, 216 ${placement.center}, 238 ${placement.center}`,
        color
      })
      chapter.skills.forEach((skill, index) => {
        const center = placement.top + 21 + index * 42
        nodes.push({
          id: `skill-${skill.id}`,
          kind: 'skill',
          label: skill.name,
          x: 476,
          y: center - 19,
          w: 306,
          h: 38,
          color,
          categoryId: category.id,
          chapterId: chapter.id,
          skill,
          score: skill.score
        })
        links.push({
          id: `link-s-${skill.id}`,
          d: `M426 ${placement.center} C450 ${placement.center}, 454 ${center}, 476 ${center}`,
          color
        })
      })
    })
    bands.push({ id: category.id, y: start, height: bandHeight, color })
    cursor = start + bandHeight + 20
  })
  return { nodes, links, bands, height: Math.max(400, cursor) }
})

interface GraphNode {
  id: string
  kind: 'root' | 'category' | 'chapter' | 'skill'
  label: string
  subtitle: string
  x: number
  y: number
  r: number
  color: string
  categoryId: string
  chapterId?: string
  skillId?: string
}

interface GraphLink {
  id: string
  x1: number
  y1: number
  x2: number
  y2: number
  color: string
}

const GRAPH_CENTER = { x: 450, y: 288 }

const graphChapterId = ref('')
const graphChapter = computed(() =>
  filteredCategories.value
    .find(category => category.id === categoryFilter.value)
    ?.chapters.find(chapter => chapter.id === graphChapterId.value)
)

const graphLayout = computed(() => {
  const category = filteredCategories.value.find(item => item.id === categoryFilter.value)
  const chapter = graphChapter.value
  const center: GraphNode = {
    id: 'graph-root',
    kind: 'root',
    label: shorten(chapter?.name || category?.name || '全部课程', 10),
    subtitle: chapter ? `${chapter.skills.length} 项技能` : category ? `${category.chapters.length} 章` : '',
    ...GRAPH_CENTER,
    r: 56,
    color: category ? colorOf(category.id) : '#327fb2',
    categoryId: category?.id || ''
  }
  const topics =
    chapter && category
      ? chapter.skills.map(skill => ({
          id: skill.id,
          kind: 'skill' as const,
          label: skill.name,
          subtitle: `${skill.score} 分`,
          color: skill.state === 'mastered' ? '#2f9d7a' : skill.state === 'learning' ? '#be882f' : '#8296a8',
          categoryId: category.id,
          skillId: skill.id
        }))
      : category
        ? category.chapters.map(item => ({
            id: item.id,
            kind: 'chapter' as const,
            label: item.name,
            subtitle: `${item.skills.length} 项技能`,
            color: colorOf(category.id),
            categoryId: category.id,
            chapterId: item.id
          }))
        : filteredCategories.value.map(item => ({
            id: item.id,
            kind: 'category' as const,
            label: item.name,
            subtitle: `${item.score} 分`,
            color: colorOf(item.id),
            categoryId: item.id
          }))
  const nodes = topics.map((item, index): GraphNode => {
    const angle = -Math.PI / 2 + (index * Math.PI * 2) / topics.length
    return {
      ...item,
      x: GRAPH_CENTER.x + Math.cos(angle) * 335,
      y: GRAPH_CENTER.y + Math.sin(angle) * 216,
      r: topics.length > 14 ? 28 : 40
    }
  })
  const links: GraphLink[] = nodes.map(node => ({
    id: node.id,
    x1: center.x,
    y1: center.y,
    x2: node.x,
    y2: node.y,
    color: node.color
  }))
  return { nodes: [center, ...nodes], links }
})

const radarAxes = computed(() => {
  const categories = data.value?.categories || []
  const rows = categories.flatMap(category =>
    category.chapters.map(chapter => ({
      key: `${category.id}-${chapter.id}`,
      label: chapter.name,
      caption: category.name,
      score: chapter.skills.length
        ? Math.round(chapter.skills.reduce((sum, skill) => sum + skill.score, 0) / chapter.skills.length)
        : 0
    }))
  )
  return rows.sort((a, b) => b.score - a.score).slice(0, 8)
})

const radarReady = computed(() => radarAxes.value.length >= 3)
const radarCenter = { x: 280, y: 280 }
const radarRadius = 188

function radarAngle(index: number) {
  return -Math.PI / 2 + (index * Math.PI * 2) / radarAxes.value.length
}

function radarPoint(index: number, ratio: number) {
  const angle = radarAngle(index)
  return {
    x: radarCenter.x + Math.cos(angle) * radarRadius * ratio,
    y: radarCenter.y + Math.sin(angle) * radarRadius * ratio
  }
}

function radarPolygon(ratio: number) {
  return radarAxes.value
    .map((_, index) => {
      const point = radarPoint(index, ratio)
      return `${point.x.toFixed(1)},${point.y.toFixed(1)}`
    })
    .join(' ')
}

function radarLabelAnchor(index: number) {
  const x = Math.cos(radarAngle(index))
  if (x > 0.3) return 'start'
  if (x < -0.3) return 'end'
  return 'middle'
}

function radarLabelPoint(index: number) {
  const angle = radarAngle(index)
  return {
    x: radarCenter.x + Math.cos(angle) * (radarRadius + 22),
    y: radarCenter.y + Math.sin(angle) * (radarRadius + 22)
  }
}

const radarInsights = computed(() => {
  const axes = radarAxes.value
  if (!axes.length) return []
  return [
    { label: '优势章节', value: axes[0].label, detail: `${axes[0].score} 分` },
    { label: '待提升章节', value: axes[axes.length - 1].label, detail: `${axes[axes.length - 1].score} 分` },
    {
      label: '覆盖技能',
      value: `${data.value?.summary.totalSkills || 0} 个技能`,
      detail: `${radarAxes.value.length} 个章节进入雷达`
    }
  ]
})

function skillStateOf(resource: SkillGraphResource) {
  return !resource.learned ? 'info' : resource.passed ? 'success' : 'warning'
}

function skillStateText(resource: SkillGraphResource) {
  return !resource.learned ? '未学习' : resource.passed ? '已达标' : '学习中'
}

function ensureSelection() {
  const skills = allSkills.value
  if (!skills.some(skill => skill.id === selectedSkillId.value)) selectedSkillId.value = skills[0]?.id || ''
}

function onNodeClick(node: MapNode) {
  if (node.kind === 'category') {
    categoryFilter.value = categoryFilter.value === node.categoryId ? '' : node.categoryId
    return
  }
  if (node.kind === 'chapter') {
    categoryFilter.value = node.categoryId
    const chapter = data.value?.categories
      .find(category => category.id === node.categoryId)
      ?.chapters.find(item => item.id === node.chapterId)
    if (chapter?.skills[0]) selectedSkillId.value = chapter.skills[0].id
    return
  }
  if (node.skill) selectedSkillId.value = node.skill.id
}

function onGraphClick(node: GraphNode) {
  if (node.kind === 'root') {
    if (graphChapterId.value) graphChapterId.value = ''
    else categoryFilter.value = ''
    return
  }
  if (node.kind === 'category') {
    categoryFilter.value = node.categoryId
    return
  }
  if (node.kind === 'chapter') graphChapterId.value = node.chapterId || ''
  if (node.skillId) selectedSkillId.value = node.skillId
}

function chooseCategory(id: string) {
  categoryFilter.value = categoryFilter.value === id ? '' : id
}

function resourceType(resource: SkillGraphResource) {
  return skillStateOf(resource)
}

function openResource(resource: SkillGraphResource) {
  emit('navigate', 'classroom-content', {
    classroomId: resource.classroomId,
    resourceId: resource.resourceId,
    backKey: 'skill-graph'
  })
}

function zoomIn() {
  zoom.value = Math.min(1.8, Math.round((zoom.value + 0.1) * 10) / 10)
}

function zoomOut() {
  zoom.value = Math.max(0.55, Math.round((zoom.value - 0.1) * 10) / 10)
}

function resetView() {
  zoom.value = 1
  panX.value = 0
  panY.value = 0
}

function onWheel(event: WheelEvent) {
  zoom.value = Math.max(0.55, Math.min(1.8, Math.round((zoom.value + (event.deltaY > 0 ? -0.1 : 0.1)) * 10) / 10))
}

function startPan(event: PointerEvent) {
  const target = event.target as Element
  if (!event.isPrimary || event.button !== 0 || target.closest('[data-map-node]')) return
  const svg = event.currentTarget as SVGSVGElement
  const rect = svg.getBoundingClientRect()
  const viewBox = svg.viewBox.baseVal
  const scale = Math.max(viewBox.width / rect.width, viewBox.height / rect.height)
  panning.value = true
  drag = { x: event.clientX, y: event.clientY, panX: panX.value, panY: panY.value, scale }
  svg.setPointerCapture(event.pointerId)
}

function movePan(event: PointerEvent) {
  if (!panning.value) return
  panX.value = drag.panX + (event.clientX - drag.x) * drag.scale
  panY.value = drag.panY + (event.clientY - drag.y) * drag.scale
}

function endPan() {
  panning.value = false
}

async function loadStudents(query = '', initial = false) {
  const request = ++studentRequest
  studentLoading.value = true
  try {
    const params = new URLSearchParams({ page: '1', pageSize: '100' })
    if (query.trim()) params.set('search', query.trim())
    if (classId.value) params.set('classId', classId.value)
    const result = await api<SkillGraphStudentsResponse>(`/skill-graph/students?${params.toString()}`)
    if (request !== studentRequest) return
    students.value = result.items
    classes.value = result.classes
    if (initial) {
      selectedUserId.value ||= result.items[0]?.userId || ''
      if (selectedUserId.value) void loadGraph()
      else {
        data.value = null
        error.value = '暂无可查看技能图谱的学生'
      }
    }
  } catch (failure) {
    if (request === studentRequest) error.value = messageOf(failure)
  } finally {
    if (request === studentRequest) studentLoading.value = false
  }
}

async function loadGraph() {
  if (isStaff.value && !selectedUserId.value) return
  const request = ++graphRequest
  loading.value = true
  error.value = ''
  try {
    const path = isStaff.value ? `/skill-graph/students/${selectedUserId.value}` : '/skill-graph'
    const graph = await api<SkillGraphResponse>(path)
    if (request !== graphRequest) return
    data.value = graph
    categoryFilter.value = ''
    selectedSkillId.value = ''
    ensureSelection()
    resetView()
  } catch (failure) {
    if (request === graphRequest) {
      error.value = messageOf(failure)
      data.value = null
    }
  } finally {
    if (request === graphRequest) loading.value = false
  }
}

function changeStudent() {
  if (!selectedUserId.value) return
  void loadGraph()
}

function changeClass() {
  ++graphRequest
  selectedUserId.value = ''
  data.value = null
  error.value = ''
  loading.value = false
  void loadStudents('', true)
}

function onRemoteSearch(query: string) {
  void loadStudents(query)
}

watch(categoryFilter, () => {
  graphChapterId.value = ''
  resetView()
  ensureSelection()
})
watch([view, graphChapterId], resetView)

onMounted(() => {
  if (isStaff.value) void loadStudents('', true)
  else void loadGraph()
})
</script>

<template>
  <section class="page-heading">
    <div>
      <h1>技能图谱</h1>
      <p>以课程章节为技能单元，串联资源学习与达标情况，定位优势与待补强方向。</p>
    </div>
    <div class="heading-actions">
      <el-select
        v-if="isStaff"
        v-model="classId"
        aria-label="所属班级"
        placeholder="全部班级"
        clearable
        class="skill-class-picker"
        @change="changeClass"
      >
        <el-option v-for="item in classes" :key="item.id" :label="item.name" :value="item.id" />
      </el-select>
      <el-select
        v-if="isStaff"
        v-model="selectedUserId"
        class="student-picker"
        filterable
        remote
        :remote-method="onRemoteSearch"
        :loading="studentLoading"
        placeholder="搜索并选择学生"
        @change="changeStudent"
      >
        <el-option
          v-for="student in students"
          :key="student.userId"
          :label="`${student.name} · ${student.className || '未分班'} · ${student.number}`"
          :value="student.userId"
        />
      </el-select>
      <el-button :icon="Refresh" :loading="loading" @click="loadGraph">刷新图谱</el-button>
    </div>
  </section>

  <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="section-notice" />

  <div v-if="data" class="student-banner">
    <div class="student-identity">
      <span class="student-avatar"
        ><el-icon><User /></el-icon
      ></span>
      <div>
        <strong>{{ data.student.name }}</strong>
        <p>
          {{ data.student.number }} · {{ data.student.className || '未分班' }} ·
          {{ data.student.professionName || '未设置专业' }}
        </p>
      </div>
    </div>
    <div class="scope-line">
      <el-icon><School /></el-icon>{{ scopeText }}
      <span>更新于 {{ dateTime(data.updatedAt) }}</span>
    </div>
  </div>

  <div v-if="data" v-loading="loading" class="skill-shell">
    <aside class="skill-side">
      <section class="surface score-panel">
        <div class="score-ring" :style="scoreRing">
          <div>
            <strong>{{ data.summary.score }}</strong>
            <span>综合能力分</span>
          </div>
        </div>
        <div class="score-meta">
          <span>{{ data.summary.masteredSkills }} 项已掌握</span>
          <span>{{ data.summary.totalSkills }} 项技能</span>
        </div>
      </section>

      <section class="surface side-block">
        <div v-for="row in summaryRows" :key="row.label" class="side-row">
          <span>{{ row.label }}</span>
          <strong :class="row.tone">{{ row.value }}</strong>
        </div>
        <div class="side-row">
          <span>活跃天数</span><strong>{{ data.summary.activeDays }} 天</strong>
        </div>
        <div class="side-row">
          <span>最近学习</span
          ><strong class="small">{{
            data.summary.lastActivity ? dateTime(data.summary.lastActivity) : '暂无记录'
          }}</strong>
        </div>
      </section>

      <section class="surface side-block categories">
        <div class="side-title">课程分类</div>
        <button :class="{ active: !categoryFilter }" @click="categoryFilter = ''">
          <span>全部课程</span><em>{{ data.summary.score }} 分</em>
        </button>
        <button
          v-for="category in data.categories"
          :key="category.id"
          :class="{ active: categoryFilter === category.id }"
          @click="chooseCategory(category.id)"
        >
          <i :style="{ background: colorOf(category.id) }"></i>
          <span>{{ category.name }}</span>
          <em>{{ category.score }} 分</em>
        </button>
      </section>
    </aside>

    <main class="surface skill-main">
      <div class="skill-toolbar">
        <el-radio-group v-model="view" size="small">
          <el-radio-button value="map">技能地图</el-radio-button>
          <el-radio-button value="radar">能力雷达</el-radio-button>
          <el-radio-button value="bars">技能详细对比</el-radio-button>
          <el-radio-button value="graph">知识关联</el-radio-button>
        </el-radio-group>
        <div class="legend">
          <span><i class="dot mastered"></i>已掌握</span>
          <span><i class="dot learning"></i>学习中</span>
          <span><i class="dot not-started"></i>未开始</span>
        </div>
      </div>

      <div v-if="(view === 'map' || view === 'graph') && mapLayout.nodes.length" class="map-tools">
        <el-button size="small" circle aria-label="缩小图谱" @click="zoomOut">−</el-button>
        <span>{{ Math.round(zoom * 100) }}%</span>
        <el-button size="small" circle aria-label="放大图谱" @click="zoomIn">＋</el-button>
        <el-button size="small" @click="resetView">复位</el-button>
        <span class="map-hint">拖动平移 · 滚轮缩放 · 点击节点查看资源</span>
      </div>
      <div v-if="view === 'map'" class="map-wrap">
        <div v-if="!mapLayout.nodes.length" class="map-empty">
          <el-icon :size="30" aria-hidden="true"><DataAnalysis /></el-icon>
          <strong>暂无技能数据</strong>
          <span>{{
            isStaff
              ? '该学生尚未加入课堂或学习资源，技能地图暂未生成。'
              : '加入课堂并开始学习资源后，这里会按课程章节生成技能地图。'
          }}</span>
          <el-button v-if="!isStaff" type="primary" plain @click="emit('navigate', 'my-classrooms')"
            >去我的课堂</el-button
          >
        </div>
        <div v-else class="map-viewport">
          <svg
            class="map-svg"
            :width="MAP_WIDTH"
            :height="mapLayout.height"
            :viewBox="`0 0 ${MAP_WIDTH} ${mapLayout.height}`"
            @wheel.prevent="onWheel"
            @pointerdown="startPan"
            @pointermove="movePan"
            @pointerup="endPan"
            @pointerleave="endPan"
            @pointercancel="endPan"
          >
            <g :transform="`translate(${panX} ${panY}) scale(${zoom})`">
              <rect
                v-for="band in mapLayout.bands"
                :key="`band-${band.id}`"
                x="0"
                :y="band.y - 8"
                :width="MAP_WIDTH"
                :height="band.height + 4"
                rx="14"
                fill="#f6faf8"
                stroke="#e4eeea"
              />
              <path
                v-for="link in mapLayout.links"
                :key="link.id"
                :d="link.d"
                fill="none"
                :stroke="link.color"
                stroke-opacity="0.28"
                stroke-width="1.4"
              />
              <g
                v-for="node in mapLayout.nodes"
                :key="node.id"
                data-map-node
                class="map-node"
                :class="[`node-${node.kind}`, { selected: node.skill && node.skill.id === selectedSkillId }]"
                @click.stop="onNodeClick(node)"
              >
                <rect
                  :x="node.x"
                  :y="node.y"
                  :width="node.w"
                  :height="node.h"
                  rx="9"
                  :fill="node.kind === 'skill' && node.skill && node.skill.state === 'mastered' ? '#f1faf6' : '#fff'"
                  :stroke="node.skill && node.skill.id === selectedSkillId ? '#1d6da4' : node.color"
                  :stroke-opacity="node.kind === 'skill' ? 0.45 : 0.75"
                  :stroke-width="node.skill && node.skill.id === selectedSkillId ? 2 : 1.2"
                />
                <rect
                  v-if="node.kind !== 'skill'"
                  :x="node.x"
                  :y="node.y"
                  width="4"
                  :height="node.h"
                  :fill="node.color"
                  rx="2"
                />
                <text
                  :x="node.x + 14"
                  :y="node.y + (node.kind === 'skill' ? 16 : node.kind === 'category' ? 22 : 17)"
                  class="node-label"
                  :fill="node.kind === 'skill' ? '#294660' : '#24445f'"
                >
                  {{ shorten(node.label, node.kind === 'skill' ? 20 : node.kind === 'category' ? 11 : 12) }}
                </text>
                <text v-if="node.kind === 'skill'" :x="node.x + 14" :y="node.y + 31" class="node-sub">
                  {{ node.skill ? skillStateLabels[node.skill.state] : '' }} · {{ node.skill?.passedCount || 0 }}/{{
                    node.skill?.totalResources || 0
                  }}
                  资源达标
                </text>
                <text
                  v-if="node.kind === 'skill'"
                  :x="node.x + node.w - 12"
                  :y="node.y + 23"
                  text-anchor="end"
                  class="node-score"
                  :fill="node.color"
                >
                  {{ node.score }}%
                </text>
                <text
                  v-if="node.kind === 'chapter'"
                  :x="node.x + node.w - 12"
                  :y="node.y + 27"
                  text-anchor="end"
                  class="node-sub"
                >
                  {{ node.skillCount }} 技能
                </text>
                <text v-if="node.kind === 'category'" :x="node.x + 14" :y="node.y + 40" class="node-sub">
                  {{ node.score }} 分 · 掌握 {{ node.masteredCount }}/{{ node.skillCount }}
                </text>
              </g>
            </g>
          </svg>
        </div>
      </div>

      <div v-else-if="view === 'radar'" class="radar-wrap">
        <template v-if="radarReady">
          <svg class="radar-svg" viewBox="0 0 560 560">
            <g>
              <polygon
                v-for="ratio in [0.25, 0.5, 0.75, 1]"
                :key="`radar-grid-${ratio}`"
                :points="radarPolygon(ratio)"
                fill="none"
                stroke="#dce7eb"
                stroke-width="1"
              />
              <line
                v-for="(axis, index) in radarAxes"
                :key="`radar-axis-${axis.key}`"
                :x1="radarCenter.x"
                :y1="radarCenter.y"
                :x2="radarPoint(index, 1).x"
                :y2="radarPoint(index, 1).y"
                stroke="#e3f3ee"
                stroke-width="1"
              />
              <polygon :points="radarPolygon(1)" fill="none" stroke="#d5e0e6" stroke-width="1" />
              <polygon
                :points="
                  radarAxes
                    .map(
                      (axis, index) =>
                        `${radarPoint(index, axis.score / 100).x},${radarPoint(index, axis.score / 100).y}`
                    )
                    .join(' ')
                "
                fill="rgba(44, 127, 178, 0.16)"
                stroke="#2c7fb2"
                stroke-width="2"
              />
              <circle
                v-for="(axis, index) in radarAxes"
                :key="`radar-point-${axis.key}`"
                :cx="radarPoint(index, axis.score / 100).x"
                :cy="radarPoint(index, axis.score / 100).y"
                r="4"
                fill="#fff"
                stroke="#2c7fb2"
                stroke-width="2"
              />
              <text
                v-for="(axis, index) in radarAxes"
                :key="`radar-label-${axis.key}`"
                :x="radarLabelPoint(index).x"
                :y="radarLabelPoint(index).y"
                :text-anchor="radarLabelAnchor(index)"
                class="radar-label"
              >
                {{ shorten(axis.label, 9) }}
              </text>
              <text
                v-for="(axis, index) in radarAxes"
                :key="`radar-score-${axis.key}`"
                :x="radarLabelPoint(index).x"
                :y="radarLabelPoint(index).y + 13"
                :text-anchor="radarLabelAnchor(index)"
                class="radar-score"
              >
                {{ axis.score }} 分
              </text>
            </g>
          </svg>
          <div class="radar-insights">
            <div class="insight-title">
              <el-icon><DataAnalysis /></el-icon>章节能力解读
            </div>
            <div v-for="item in radarInsights" :key="item.label" class="insight-row">
              <span>{{ item.label }}</span>
              <strong>{{ item.value }}</strong>
              <em>{{ item.detail }}</em>
            </div>
            <p>雷达按章节技能平均分排序，最多展示 8 个章节；分数来自资源学习与练习达标情况。</p>
          </div>
        </template>
        <div v-else class="radar-fallback">
          <div v-for="axis in radarAxes" :key="axis.key" class="fallback-row">
            <div>
              <strong>{{ axis.label }}</strong><span>{{ axis.caption }}</span>
            </div>
            <el-progress :percentage="axis.score" :stroke-width="8" />
          </div>
          <el-empty v-if="!radarAxes.length" description="暂无章节技能数据" :image-size="80" />
          <p v-if="radarAxes.length" class="fallback-hint">章节数量不足 3 个，暂以进度条展示章节能力。</p>
        </div>
      </div>

      <div v-else-if="view === 'bars'" class="comparison-wrap">
        <div v-for="skill in allSkills" :key="skill.id" class="comparison-row">
          <button class="comparison-skill" type="button" @click="selectedSkillId = skill.id">
            {{ skill.name }} <small>{{ skill.chapterName }}</small>
          </button>
          <el-progress
            :percentage="skill.score"
            :stroke-width="12"
            :show-text="false"
            :aria-label="`${skill.name}实际掌握`"
          />
          <strong>{{ skill.score }} / {{ data.summary.fullScore }} 分</strong>
        </div>
        <el-empty v-if="!allSkills.length" description="暂无技能数据" :image-size="80" />
      </div>

      <div v-else class="graph-wrap">
        <div class="graph-caption">
          <el-button v-if="categoryFilter" link @click="categoryFilter = ''">全部课程</el-button>
          <el-button v-if="graphChapter" link @click="graphChapterId = ''">
            {{ filteredCategories[0]?.name }}
          </el-button>
          <span>{{ graphChapter?.name || (categoryFilter ? filteredCategories[0]?.name : '全部课程') }}</span>
        </div>
        <svg
          class="graph-svg"
          viewBox="0 0 900 576"
          aria-label="技能关系图"
          @wheel.prevent="onWheel"
          @pointerdown="startPan"
          @pointermove="movePan"
          @pointerup="endPan"
          @pointerleave="endPan"
          @pointercancel="endPan"
        >
          <g :transform="`translate(${panX} ${panY}) translate(450 288) scale(${zoom}) translate(-450 -288)`">
            <line
              v-for="link in graphLayout.links"
              :key="link.id"
              :x1="link.x1"
              :y1="link.y1"
              :x2="link.x2"
              :y2="link.y2"
              :stroke="link.color"
              stroke-opacity="0.24"
              stroke-width="1.4"
            />
            <g
              v-for="node in graphLayout.nodes"
              :key="node.id"
              class="graph-node"
              data-map-node
              tabindex="0"
              role="button"
              :aria-label="node.label"
              @click="onGraphClick(node)"
              @keydown.enter.prevent="onGraphClick(node)"
              @keydown.space.prevent="onGraphClick(node)"
            >
              <title>{{ node.label }}</title>
              <circle
                :cx="node.x"
                :cy="node.y"
                :r="node.r"
                :fill="node.kind === 'chapter' ? '#fff' : node.color"
                :stroke="node.color"
                :stroke-width="node.skillId && node.skillId === selectedSkillId ? 3 : 1.5"
                :fill-opacity="node.kind === 'chapter' ? 1 : 0.14"
              />
              <text
                :x="node.x"
                :y="node.y - 1"
                text-anchor="middle"
                class="graph-label"
                :fill="node.kind === 'category' ? '#1f4b6d' : '#3b5b75'"
              >
                {{ shorten(node.label, node.r < 35 ? 5 : 7) }}
              </text>
              <text :x="node.x" :y="node.y + 14" text-anchor="middle" class="graph-sub">{{ node.subtitle }}</text>
            </g>
          </g>
        </svg>
      </div>
    </main>

    <aside class="surface skill-detail">
      <template v-if="selectedSkill">
        <div class="detail-head">
          <div class="eyebrow">SKILL DETAIL</div>
          <h2>{{ selectedSkill.name }}</h2>
          <p>{{ selectedSkill.categoryName }} / {{ selectedSkill.chapterName }}</p>
        </div>
        <div class="detail-score">
          <strong>{{ selectedSkill.score }}<small>%</small></strong>
          <el-tag :type="skillStateType(selectedSkill.state)" size="small">{{
            skillStateLabels[selectedSkill.state]
          }}</el-tag>
        </div>
        <el-progress
          :percentage="selectedSkill.score"
          :stroke-width="7"
          :color="selectedSkill.score >= 60 ? '#2f9d7a' : '#be882f'"
        />
        <div class="detail-metrics">
          <span
            >技能资源<strong>{{ selectedSkill.passedCount }} / {{ selectedSkill.totalResources }} 达标</strong></span
          >
          <span
            >已学习<strong>{{ selectedSkill.learnedCount }} 项</strong></span
          >
        </div>
        <div class="detail-resources">
          <h3>关联资源</h3>
          <button
            v-for="resource in selectedSkill.resources"
            :key="resource.resourceId"
            class="resource-item"
            @click="openResource(resource)"
          >
            <div class="resource-item-head">
              <span>{{ resource.kindLabel }}</span>
              <el-tag size="small" :type="resourceType(resource)">{{ skillStateText(resource) }}</el-tag>
            </div>
            <strong>{{ resource.name }}</strong>
            <p>{{ resource.classroomName }} · {{ resourceProgressText(resource.bestScore, resource.maxScore) }}</p>
            <el-progress :percentage="resource.progress" :stroke-width="4" :show-text="false" />
          </button>
          <el-empty v-if="!selectedSkill.resources.length" description="暂无关联资源" :image-size="60" />
        </div>
        <p class="detail-tip">点击资源可进入课堂学习；未达标技能按资源学习与练习成绩综合计算。</p>
      </template>
      <el-empty v-else description="点击技能节点查看详情" :image-size="80" />
    </aside>
  </div>

  <div v-else-if="!error && !loading" class="skill-empty">
    <el-empty description="暂无可展示的技能图谱">
      <p class="empty-hint">技能图谱来自课堂课程与学习记录，请先加入课堂并开始学习。</p>
    </el-empty>
  </div>
</template>

<style scoped>
.heading-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
}

.student-picker {
  width: 290px;
}

.skill-class-picker {
  width: 145px;
}

.student-banner {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 14px;
  align-items: center;
  padding: 13px 17px;
  margin-bottom: 14px;
  background: linear-gradient(120deg, #f3f9f6, #fff);
  border: 1px solid #d6e6e3;
  border-radius: 11px;
}

.student-identity {
  display: flex;
  gap: 11px;
  align-items: center;
}

.student-avatar {
  display: grid;
  width: 38px;
  height: 38px;
  place-items: center;
  color: #0f5f58;
  background: #d8efe9;
  border-radius: 50%;
}

.student-identity strong {
  color: #1f405c;
  font-size: 15px;
}

.student-identity p {
  margin: 3px 0 0;
  color: #56697c;
  font-size: 11px;
}

.scope-line {
  display: flex;
  flex-wrap: wrap;
  gap: 7px;
  align-items: center;
  color: #56697c;
  font-size: 11px;
}

.scope-line span {
  color: #5d6f80;
}

.skill-shell {
  display: grid;
  grid-template-columns: 232px minmax(0, 1fr) 300px;
  gap: 13px;
  align-items: start;
}

.skill-side {
  display: grid;
  gap: 11px;
}

.score-panel {
  padding: 19px;
  text-align: center;
}

.score-ring {
  display: grid;
  width: 132px;
  height: 132px;
  margin: 0 auto;
  padding: 9px;
  border-radius: 50%;
  place-items: center;
}

.score-ring > div {
  display: grid;
  width: 100%;
  height: 100%;
  background: #fff;
  border-radius: 50%;
  place-content: center;
}

.score-ring strong {
  color: #1a514c;
  font-size: 31px;
  line-height: 1;
}

.score-ring span {
  margin-top: 4px;
  color: #56697c;
  font-size: 9px;
}

.score-meta {
  display: flex;
  justify-content: center;
  gap: 10px;
  color: #56697c;
  font-size: 9px;
  margin-top: 10px;
}

.side-block {
  padding: 13px 15px;
}

.side-row {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  padding: 7px 0;
  color: #56697c;
  font-size: 11px;
  border-bottom: 1px dashed #e4eeea;
}

.side-row:last-child {
  border-bottom: 0;
}

.side-row strong {
  color: #2d506c;
  font-size: 12px;
}

.side-row strong.success {
  color: #20815f;
}

.side-row strong.warning {
  color: #94610f;
}

.side-row strong.info {
  color: #56697c;
}

.side-row strong.plain {
  font-size: 11px;
}

.side-row strong.small {
  font-size: 9px;
  font-weight: 500;
}

.side-title {
  margin-bottom: 7px;
  color: #56697c;
  font-size: 10px;
  letter-spacing: 0.05em;
}

.categories button {
  display: flex;
  align-items: center;
  gap: 7px;
  width: 100%;
  padding: 8px 7px;
  color: #526b80;
  font-size: 11px;
  text-align: left;
  background: transparent;
  border: 0;
  border-radius: 7px;
  cursor: pointer;
}

.categories button:hover {
  background: #eef6f4;
}

.categories button.active {
  color: #145a50;
  background: #e3f3ee;
}

.categories button i {
  flex: none;
  width: 7px;
  height: 7px;
  border-radius: 50%;
}

.categories button span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.categories button em {
  margin-left: auto;
  color: #56697c;
  font-size: 9px;
  font-style: normal;
}

.skill-main {
  min-width: 0;
  overflow: hidden;
}

.skill-toolbar {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 10px;
  align-items: center;
  padding: 13px 16px;
  border-bottom: 1px solid #dce7eb;
}

.legend {
  display: flex;
  gap: 13px;
  color: #56697c;
  font-size: 10px;
}

.legend .dot {
  display: inline-block;
  width: 7px;
  height: 7px;
  margin-right: 4px;
  border-radius: 50%;
}

.dot.mastered {
  background: #2f9d7a;
}

.dot.learning {
  background: #be882f;
}

.dot.not-started {
  background: #b8c7d4;
}

.map-tools {
  display: flex;
  align-items: center;
  gap: 7px;
  padding: 10px 16px 0;
  color: #56697c;
  font-size: 10px;
}

.map-hint {
  margin-left: 8px;
  color: #5d6f80;
}

.map-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  min-height: 360px;
  padding: 24px;
  text-align: center;
}

.map-empty .el-icon {
  display: grid;
  place-items: center;
  width: 56px;
  height: 56px;
  border-radius: 16px;
  background: #eef5f4;
  color: #5e8f88;
}

.map-empty strong {
  color: #3d5568;
  font-size: 15px;
}

.map-empty span {
  max-width: 380px;
  color: var(--text-muted);
  font-size: 12px;
  line-height: 1.7;
}

.map-empty .el-button {
  margin-top: 4px;
}

.map-viewport {
  max-height: calc(100vh - 335px);
  min-height: 430px;
  overflow: auto;
  padding: 10px 14px 16px;
}

.map-svg {
  display: block;
  width: 100%;
  max-width: 800px;
  height: auto;
  margin-inline: auto;
  cursor: grab;
  user-select: none;
  touch-action: none;
}

.map-svg:active {
  cursor: grabbing;
}

.map-node {
  cursor: pointer;
}

.map-node .node-label {
  font-size: 11px;
  font-weight: 600;
  pointer-events: none;
}

.map-node .node-sub {
  font-size: 8.5px;
  fill: #8da0b1;
  pointer-events: none;
}

.map-node .node-score {
  font-size: 11px;
  font-weight: 700;
  pointer-events: none;
}

.map-node.node-category .node-label {
  font-size: 12.5px;
}

.map-node.selected rect:first-child {
  filter: drop-shadow(0 3px 7px rgba(36, 107, 156, 0.13));
}

.radar-wrap {
  display: grid;
  grid-template-columns: minmax(0, 1.4fr) minmax(200px, 0.8fr);
  gap: 8px;
  padding: 16px 18px 20px;
  align-items: center;
}

.radar-svg {
  width: 100%;
  max-height: calc(100vh - 355px);
}

.radar-label {
  fill: #49677f;
  font-size: 9.5px;
}

.radar-score {
  fill: #8fa2b2;
  font-size: 8.5px;
}

.radar-insights {
  padding: 15px;
  background: #f6faf8;
  border: 1px solid #e4eeea;
  border-radius: 10px;
}

.insight-title {
  display: flex;
  gap: 5px;
  align-items: center;
  margin-bottom: 11px;
  color: #42637e;
  font-size: 11px;
  font-weight: 600;
}

.insight-row {
  display: flex;
  flex-wrap: wrap;
  gap: 5px 8px;
  align-items: baseline;
  padding: 8px 0;
  border-bottom: 1px dashed #e4eeea;
}

.insight-row span {
  color: #56697c;
  font-size: 9px;
}

.insight-row strong {
  color: #30516d;
  font-size: 11px;
}

.insight-row em {
  margin-left: auto;
  color: #56697c;
  font-size: 9px;
  font-style: normal;
}

.radar-insights p {
  margin: 10px 0 0;
  color: #5d6f80;
  font-size: 9px;
  line-height: 1.6;
}

.radar-fallback {
  padding: 20px;
  grid-column: 1 / -1;
}

.fallback-row {
  margin-bottom: 15px;
}

.fallback-row > div {
  display: flex;
  justify-content: space-between;
  margin-bottom: 5px;
  color: #385872;
  font-size: 11px;
}

.fallback-row span {
  color: #5d6f80;
  font-size: 9px;
}

.fallback-hint {
  color: #5d6f80;
  font-size: 10px;
}

.graph-wrap {
  padding: 12px 16px 16px;
}

.graph-caption {
  margin-bottom: 4px;
  color: #56697c;
  font-size: 10px;
}

.graph-svg {
  display: block;
  width: 100%;
  max-height: calc(100vh - 350px);
  cursor: grab;
  touch-action: none;
  user-select: none;
}

.graph-svg:active {
  cursor: grabbing;
}

.graph-node {
  cursor: pointer;
}

.graph-label {
  font-size: 10px;
  font-weight: 600;
  pointer-events: none;
}

.graph-sub {
  font-size: 7.5px;
  fill: #8ea1b2;
  pointer-events: none;
}

.comparison-wrap {
  display: grid;
  gap: 10px;
  max-height: 62vh;
  overflow: auto;
  padding: 16px;
}

.comparison-row {
  display: grid;
  grid-template-columns: minmax(90px, 1fr) minmax(45px, 2fr) auto;
  align-items: center;
  gap: 10px;
  font-size: 12px;
}

.comparison-skill {
  padding: 4px 0;
  color: var(--el-color-primary);
  text-align: left;
  background: transparent;
  border: 0;
  cursor: pointer;
}

.comparison-skill small {
  display: block;
  color: var(--el-text-color-secondary);
}

.skill-detail {
  display: flex;
  flex-direction: column;
  padding: 17px;
  max-height: calc(100vh - 200px);
  overflow: auto;
}

.detail-head h2 {
  margin: 5px 0 4px;
  color: #1d405d;
  font-size: 17px;
  line-height: 1.45;
}

.detail-head p {
  margin: 0;
  color: #56697c;
  font-size: 10px;
}

.detail-score {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin: 14px 0 7px;
}

.detail-score strong {
  color: #1a514c;
  font-size: 30px;
}

.detail-score small {
  font-size: 12px;
}

.detail-metrics {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
  margin: 12px 0 4px;
}

.detail-metrics span {
  display: flex;
  flex-direction: column;
  gap: 3px;
  padding: 8px;
  color: #56697c;
  font-size: 9px;
  background: #f6faf8;
  border: 1px solid #e4eeea;
  border-radius: 7px;
}

.detail-metrics strong {
  color: #3b5f7b;
  font-size: 10px;
}

.detail-resources {
  margin-top: 12px;
}

.detail-resources h3 {
  margin: 0 0 7px;
  color: #4c6a82;
  font-size: 11px;
}

.resource-item {
  display: block;
  width: 100%;
  padding: 9px;
  margin-bottom: 7px;
  text-align: left;
  background: #f6faf8;
  border: 1px solid #e4eeea;
  border-radius: 8px;
  cursor: pointer;
  transition: border-color 0.15s ease;
}

.resource-item:hover {
  border-color: #bdd9d3;
}

.resource-item-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 4px;
}

.resource-item-head > span {
  color: #56697c;
  font-size: 9px;
}

.resource-item strong {
  display: block;
  overflow: hidden;
  color: #2d4d68;
  font-size: 11px;
  line-height: 1.5;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.resource-item p {
  margin: 3px 0 6px;
  color: #5d6f80;
  font-size: 8.5px;
}

.detail-tip {
  margin: 9px 0 0;
  color: #5d6f80;
  font-size: 9px;
  line-height: 1.55;
}

.skill-empty {
  padding: 30px;
  background: #fff;
  border: 1px solid #e4eeea;
  border-radius: 11px;
}

.empty-hint {
  color: #56697c;
  font-size: 11px;
}

@media (max-width: 1680px) {
  .skill-shell {
    grid-template-columns: minmax(0, 1fr) 300px;
  }

  .skill-main {
    grid-column: 1;
    grid-row: 1;
  }

  .skill-detail {
    grid-column: 2;
    grid-row: 1;
  }

  .skill-side {
    grid-column: 1 / -1;
    grid-row: 2;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    align-items: start;
  }
}

@media (max-width: 1280px) {
  .skill-shell {
    grid-template-columns: 212px minmax(0, 1fr);
  }

  .skill-main {
    grid-column: auto;
    grid-row: auto;
  }

  .skill-side {
    grid-column: auto;
    grid-row: auto;
    grid-template-columns: 1fr;
  }

  .skill-detail {
    grid-column: 1 / -1;
    grid-row: auto;
    max-height: none;
  }

  .detail-resources {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(230px, 1fr));
    gap: 8px;
  }

  .detail-resources h3 {
    grid-column: 1 / -1;
  }

  .resource-item {
    margin-bottom: 0;
  }
}

@media (max-width: 1200px) {
  .student-picker {
    width: 100%;
  }

  .skill-shell {
    grid-template-columns: 1fr;
  }

  .skill-main {
    order: 1;
  }

  .skill-side {
    order: 2;
    grid-template-columns: 1fr 1fr;
  }

  .skill-detail {
    order: 3;
  }

  .categories {
    grid-column: 1 / -1;
  }

  .radar-wrap {
    grid-template-columns: 1fr;
  }

  .map-viewport {
    max-height: 60vh;
  }
}

@media (max-width: 620px) {
  .skill-side {
    grid-template-columns: 1fr;
  }

  .categories {
    grid-column: auto;
  }

  .legend {
    display: none;
  }
}
</style>

<script setup lang="ts">
import { computed } from 'vue'
import { ArrowRight, Clock, School } from '@element-plus/icons-vue'
import type { HomeClassroomCard } from '../analytics'
import { dateTime, parseApiDate } from '../time'
import { classroomStatusOptions, classroomStatusType, labelOf } from '../teaching'
import TableEmpty from './TableEmpty.vue'

const props = defineProps<{ classrooms: HomeClassroomCard[]; teacher?: boolean }>()
const emit = defineEmits<{ all: []; enter: [id: string]; statistics: [id: string] }>()

const statusOrder = { open: 0, scheduled: 1, closed: 2 }
const studentAction = { open: '进入课堂', scheduled: '查看内容', closed: '查看回顾' }
const openCount = computed(() => props.classrooms.filter(item => item.status === 'open').length)
const preview = computed(() => {
  const time = (item: HomeClassroomCard) =>
    parseApiDate(item.status === 'scheduled' ? item.openAt : item.closeAt).getTime()
  return [...props.classrooms]
    .sort(
      (a, b) =>
        statusOrder[a.status] - statusOrder[b.status] || (a.status === 'closed' ? time(b) - time(a) : time(a) - time(b))
    )
    .slice(0, 3)
})
</script>

<template>
  <section class="surface home-classrooms" aria-labelledby="home-classrooms-title">
    <header class="card-heading">
      <div>
        <h2 id="home-classrooms-title">
          <el-icon aria-hidden="true"><School /></el-icon>{{ teacher ? '我管理的课堂' : '我的课堂' }}
        </h2>
        <p v-if="classrooms.length">{{ openCount }} 个进行中 · 共 {{ classrooms.length }} 个课堂</p>
      </div>
      <el-button v-if="classrooms.length" text type="primary" @click="emit('all')">
        全部课堂<el-icon aria-hidden="true"><ArrowRight /></el-icon>
      </el-button>
    </header>

    <ul v-if="preview.length" class="home-classroom-list" role="list">
      <li
        v-for="item in preview"
        :key="item.id"
        class="home-classroom"
        :class="{ 'is-ongoing': item.status === 'open' }"
      >
        <div class="home-classroom-title">
          <div>
            <h3>{{ item.name }}</h3>
            <p>
              {{ item.courseName }}<template v-if="teacher"> · {{ item.studentCount }} 名学生</template
              ><template v-else-if="item.teacherName"> · {{ item.teacherName }}</template>
            </p>
          </div>
          <el-tag :type="classroomStatusType(item.status)" size="small">{{
            labelOf(classroomStatusOptions, item.status)
          }}</el-tag>
        </div>
        <div class="home-classroom-progress">
          <span>{{ teacher ? '班级达标率' : `资源达标 ${item.passed} / ${item.totalResources}` }}</span>
          <strong>{{ teacher ? item.classPassRate : item.passRate }}%</strong>
        </div>
        <el-progress
          :percentage="teacher ? item.classPassRate : item.passRate"
          :stroke-width="5"
          :show-text="false"
          :aria-label="teacher ? '班级达标率' : '资源达标率'"
        />
        <div class="home-classroom-footer">
          <p>
            <el-icon aria-hidden="true"><Clock /></el-icon>
            {{ item.status === 'scheduled' ? '计划开课' : '计划结课' }}
            <time :datetime="item.status === 'scheduled' ? item.openAt : item.closeAt">{{
              dateTime(item.status === 'scheduled' ? item.openAt : item.closeAt)
            }}</time>
          </p>
          <div class="home-classroom-actions">
            <el-button text type="primary" @click="emit('statistics', item.id)">查看统计</el-button>
            <el-button type="primary" :plain="item.status !== 'open'" @click="emit('enter', item.id)">
              {{ teacher ? '进入课堂' : studentAction[item.status] }}
            </el-button>
          </div>
        </div>
      </li>
    </ul>
    <div v-else class="home-classrooms-empty">
      <TableEmpty
        :title="teacher ? '暂无课堂' : '暂未加入课堂'"
        :hint="teacher ? undefined : '请联系授课教师，确认已加入课堂'"
      />
      <el-button v-if="teacher" type="primary" plain @click="emit('all')">前往我的课堂</el-button>
    </div>
  </section>
</template>

<style scoped>
.home-classrooms {
  min-width: 0;
}

.home-classrooms h2,
.home-classroom-footer p {
  display: flex;
  align-items: center;
  gap: 7px;
}

.home-classrooms h2 .el-icon {
  color: var(--workspace-accent);
}

.home-classrooms .card-heading > .el-button {
  flex: none;
  padding-inline: 8px;
}

.home-classrooms .card-heading > .el-button .el-icon {
  margin-left: 4px;
}

.home-classroom-list {
  display: grid;
  gap: 10px;
  margin: 0;
  padding: 0 20px 20px;
  list-style: none;
}

.home-classroom {
  min-width: 0;
  padding: 14px;
  border: 1px solid var(--surface-border);
  border-radius: 10px;
  background: #f6faf8;
  transition:
    border-color 0.16s ease,
    box-shadow 0.16s ease,
    background 0.16s ease;
}

.home-classroom:hover {
  border-color: #cfe6df;
  box-shadow: 0 6px 16px #1732460c;
}

.home-classroom.is-ongoing {
  border-color: #d5eae2;
  background: #f2faf6;
}

.home-classroom-title,
.home-classroom-progress,
.home-classroom-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.home-classroom-title {
  align-items: start;
}

.home-classroom-title h3 {
  margin: 0 0 4px;
  color: #234055;
  overflow-wrap: anywhere;
}

.home-classroom-title p,
.home-classroom-footer p {
  margin: 0;
  font-size: 12px;
  overflow-wrap: anywhere;
}

.home-classroom-title .el-tag {
  flex: none;
}

.home-classroom-progress {
  margin: 11px 0 5px;
  color: var(--text-muted);
  font-size: 12px;
}

.home-classroom-progress strong {
  color: var(--workspace-accent);
  font-variant-numeric: tabular-nums;
}

.home-classroom-footer {
  margin-top: 10px;
  flex-wrap: wrap;
}

.home-classroom-footer p {
  flex-wrap: wrap;
  gap: 2px 5px;
}

.home-classroom-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-left: auto;
}

.home-classroom-actions .el-button + .el-button {
  margin-left: 0;
}

.home-classrooms-empty {
  display: grid;
  justify-items: center;
  padding: 0 16px 21px;
  text-align: center;
}

@media (max-width: 550px) {
  .home-classroom-list {
    padding-inline: 12px;
    padding-bottom: 12px;
  }
  .home-classroom-actions .el-button {
    padding-inline: 12px;
  }
}
</style>

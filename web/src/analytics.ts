import type { ClassroomStatus, CourseType, QuestionType, ResourceKind } from './types'
import type { CaseGrade } from './caseWork'

export interface KindStats {
  total: number
  learned: number
  passed: number
  learningRate: number
  passRate: number
}

export interface StudentOverviewStats {
  totalResources: number
  practices: number
  learned: number
  passed: number
  learningRate: number
  passRate: number
  isPassing: boolean
  activeDays: number
  lastActivity: string | null
}

export interface HomeClassroomCard {
  id: string
  name: string
  courseId: string
  courseName: string
  teacherName: string
  status: ClassroomStatus
  openAt: string
  closeAt: string
  studentCount: number
  learned: number
  passed: number
  totalResources: number
  learningRate: number
  passRate: number
  classPassRate: number
}

export interface HomeLeaderboardRow {
  userId?: string
  name: string
  number?: string
  className?: string
  isMe: boolean
  learned: number
  passed: number
  totalResources: number
  learningRate: number
  passRate: number
  activeDays?: number
  lastActivity?: string | null
}

export interface HomeTodoAction {
  route: string
  params?: Record<string, string>
  label: string
}

export interface HomeTodo {
  key: string
  kind: string
  title: string
  hint: string
  action?: HomeTodoAction | null
}

export interface StudentHomeResponse {
  student: {
    userId: string
    name: string
    userName: string
    number: string
    className: string
    professionName: string
  }
  today: { learned: number; practices: number }
  overview: StudentOverviewStats
  kinds: {
    theory: KindStats
    theoryPractice: KindStats
    case: KindStats
    casePractice: KindStats
  }
  classrooms: HomeClassroomCard[]
  classLeaderboard: HomeLeaderboardRow[]
  todos: HomeTodo[]
  serverNow: string
}

export interface TeacherHomeResponse {
  teacher: { id: string; displayName: string; userName: string }
  today: { activeStudents: number; passedStudents: number }
  overview: {
    passedStudents: number
    managedClasses: number
    managedClassrooms: number
    managedStudents: number
    learningRate: number
    resourcePassRate: number
  }
  classrooms: HomeClassroomCard[]
  leaderboard: HomeLeaderboardRow[]
  todos: HomeTodo[]
  canCreateClassroom: boolean
  serverNow: string
}

export interface AdminHomeResponse {
  today: { activeClasses: number; activeStudents: number; passedStudents: number }
  overview: {
    passedStudents: number
    managedClasses: number
    managedStudents: number
    totalClassrooms: number
    openClassrooms: number
    learningRate: number
    resourcePassRate: number
  }
  classRanking: {
    classId: string
    className: string
    professionName: string
    studentCount: number
    passedStudents: number
    passRate: number
  }[]
  teacherActivity: {
    id: string
    displayName: string
    userName: string
    role: string
    managedClasses: number
    auditCount: number
    lastActiveAt: string | null
  }[]
  todos: HomeTodo[]
  serverNow: string
}

export interface StatsBucket {
  totalStudents: number
  passedStudents: number
  totalResources: number
  learnedResources: number
  passedResources: number
  learningRate: number
  passRate: number
}

export interface ClassroomStatsSummary {
  overall: StatsBucket
  theory: StatsBucket
  theoryPractice: StatsBucket
  case: StatsBucket
  casePractice: StatsBucket
}

export interface ClassroomStatsStudentRow {
  userId: string
  personId: string
  name: string
  userName: string
  number: string
  classId: string | null
  className: string
  overall: KindStats
  theory: KindStats
  theoryPractice: KindStats
  case: KindStats
  casePractice: KindStats
  activeDays: number
  lastActivity: string | null
}

export interface ClassroomStatsResource {
  id: string
  name: string
  kind: ResourceKind
  kindLabel: string
  durationMinutes: number
  maxScore: number
  learnedCount: number
  passedCount: number
  learningRate: number
  passRate: number
}

export interface ClassroomStatsSection {
  id: string
  name: string
  resources: ClassroomStatsResource[]
}

export interface ClassroomStatsChapter {
  id: string
  name: string
  sections: ClassroomStatsSection[]
}

export interface ClassroomStatisticsResponse {
  classroom: {
    id: string
    name: string
    courseId: string
    courseName: string
    teacherName: string
    status: ClassroomStatus
    openAt: string
    closeAt: string
    studentCount: number
  }
  serverNow: string
  remainingSeconds: number
  chapterId: string
  summary: ClassroomStatsSummary
  students: ClassroomStatsStudentRow[]
  chapters: ClassroomStatsChapter[]
}

export interface ClassroomStatsTranscriptRow {
  id: string
  name: string
  kind: ResourceKind
  kindLabel: string
  chapterId: string
  chapterName: string
  sectionId: string
  sectionName: string
  learned: boolean
  passed: boolean
  status: string
  bestScore: number
  maxScore: number
  attemptCount: number
  lastActivity: string | null
}

export interface ClassroomStatsPracticeRow {
  attemptId: string
  resourceId: string
  resourceName: string
  chapterName: string
  sectionName: string
  attemptNumber: number
  score: number
  maxScore: number
  passed: boolean
  submittedAt: string
}

export interface ClassroomStatsCaseRow {
  recordId: string
  resourceId: string
  resourceName: string
  chapterName: string
  sectionName: string
  status: string
  startedAt: string
  completedAt: string | null
  attemptCount: number
  lastActivity: string
}

export interface StudentStatsDetail {
  classroom: { id: string; name: string; courseName: string; remainingSeconds: number }
  student: {
    userId: string
    personId: string
    name: string
    userName: string
    number: string
    classId: string | null
    className: string
    professionName: string
  }
  summary: {
    overall: KindStats
    theory: KindStats
    theoryPractice: KindStats
    case: KindStats
    casePractice: KindStats
    isPassing: boolean
    activeDays: number
    lastActivity: string | null
  }
  transcript: ClassroomStatsTranscriptRow[]
  practiceHistory: ClassroomStatsPracticeRow[]
  caseHistory: ClassroomStatsCaseRow[]
}

export interface ResourceStudentRow {
  userId: string
  personId: string
  name: string
  userName: string
  number: string
  classId: string | null
  className: string
  learned: boolean
  passed: boolean
  status: string
  bestScore: number
  maxScore: number
  attemptCount: number
  lastActivity: string | null
}

export interface ResourceStudentsResponse {
  classroom: { id: string; name: string }
  resource: {
    id: string
    name: string
    kind: ResourceKind
    chapterName: string
    sectionName: string
    maxScore: number
  }
  learnedCount: number
  passedCount: number
  learningRate: number
  passRate: number
  items: ResourceStudentRow[]
}

export interface PracticeAttemptDetail {
  attemptId: string
  attemptNumber: number
  attemptCount: number
  averagePercent?: number | null
  bestPercent?: number | null
  startedAt: string | null
  score: number
  maxScore: number
  passed: boolean
  submittedAt: string
  snapshotUnavailable?: boolean
  snapshotMessage?: string | null
  student: { name: string; number: string }
  resource: { id: string; name: string; sectionName: string; chapterName: string }
  questions: {
    questionId: string
    type: QuestionType
    stem: string
    options: string[]
    selected: number[]
    correctAnswers: number[]
    correct: boolean
    score: number
    analysis: string
  }[]
}

export interface CaseRecordDetail {
  recordId: string
  status: string
  startedAt: string
  completedAt: string | null
  attemptCount: number
  grades: CaseGrade[]
  student: { name: string; number: string }
  resource: {
    id: string
    name: string
    kind: ResourceKind
    content: string
    topology: string
    startupNodeId: string | null
    startupNodeName: string
    sectionName: string
    chapterName: string
  }
}

export type SkillNodeState = 'mastered' | 'learning' | 'notStarted'

export interface SkillGraphResource {
  resourceId: string
  name: string
  kind: ResourceKind
  kindLabel: string
  durationMinutes: number
  classroomId: string
  classroomName: string
  learned: boolean
  passed: boolean
  status: string
  bestScore: number
  maxScore: number
  attemptCount: number
  progress: number
  lastActivity: string | null
}

export interface SkillGraphSkill {
  id: string
  name: string
  chapterId: string
  chapterName: string
  categoryId: string
  categoryName: string
  score: number
  state: SkillNodeState
  totalResources: number
  learnedCount: number
  passedCount: number
  resources: SkillGraphResource[]
}

export interface SkillGraphChapter {
  id: string
  name: string
  skills: SkillGraphSkill[]
}

export interface SkillGraphCategory {
  id: string
  name: string
  type: CourseType
  score: number
  skillCount: number
  masteredCount: number
  chapters: SkillGraphChapter[]
}

export interface SkillGraphResponse {
  student: {
    userId: string
    personId: string
    name: string
    userName: string
    number: string
    className: string
    professionName: string
  }
  scope: { classroomCount: number; teacherScoped: boolean }
  summary: {
    score: number
    currentTarget: number
    fullScore: number
    masteredSkills: number
    learnedSkills: number
    learningSkills: number
    notStartedSkills: number
    totalSkills: number
    totalResources: number
    learnedResources: number
    passedResources: number
    activeDays: number
    lastActivity: string | null
  }
  categories: SkillGraphCategory[]
  updatedAt: string
}

export interface SkillGraphStudentOption {
  userId: string
  personId: string
  name: string
  userName: string
  number: string
  classId: string | null
  className: string
  professionName: string
  classroomCount: number
}

export interface SkillGraphStudentsResponse {
  total: number
  items: SkillGraphStudentOption[]
  classes: { id: string; name: string; studentCount: number }[]
}

export const skillStateLabels: Record<SkillNodeState, string> = {
  mastered: '已掌握',
  learning: '学习中',
  notStarted: '未开始'
}

export function flattenSkills(categories: SkillGraphCategory[]): SkillGraphSkill[] {
  return categories.flatMap(category => category.chapters.flatMap(chapter => chapter.skills))
}

export function skillStateType(state: SkillNodeState): 'success' | 'warning' | 'info' {
  if (state === 'mastered') return 'success'
  if (state === 'learning') return 'warning'
  return 'info'
}

export function resourceProgressText(score: number, maxScore: number, kind?: string, attemptCount?: number): string {
  if (maxScore > 0) return `${score} / ${maxScore}`
  if (kind === 'theoryPractice' && (attemptCount ?? 0) > 0) return '历史满分未知，暂不判定达标'
  return '—'
}

export function rateText(rate: number | null | undefined): string {
  return rate == null ? '—' : `${rate}%`
}

export function attemptPassLabel(attempt: { maxScore: number; passed: boolean }): string {
  return attempt.maxScore > 0 ? (attempt.passed ? '达标' : '未达标') : '暂不判定'
}

export function remainingText(seconds: number): string {
  if (seconds <= 0) return '已结束'
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  if (days > 0) return `${days} 天 ${hours} 小时`
  const minutes = Math.floor((seconds % 3600) / 60)
  return hours > 0 ? `${hours} 小时 ${minutes} 分钟` : `${minutes} 分钟`
}

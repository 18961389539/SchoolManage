import type { ClassroomStatus, CourseStatus, CourseType, QuestionType, ResourceKind } from './types'

export interface LabelOption<T extends string> {
  value: T
  label: string
}

export const courseTypeOptions: LabelOption<CourseType>[] = [
  { value: 'general', label: '通识课程' },
  { value: 'core', label: '专业核心' },
  { value: 'open', label: '开放课程' }
]

export const courseStatusOptions: LabelOption<CourseStatus>[] = [
  { value: 'draft', label: '草稿' },
  { value: 'published', label: '已发布' }
]

export const resourceKindOptions: LabelOption<ResourceKind>[] = [
  { value: 'theory', label: '理论知识' },
  { value: 'case', label: '案例教学' },
  { value: 'theoryPractice', label: '理论练习' },
  { value: 'casePractice', label: '案例实操' }
]

export const questionTypeOptions: LabelOption<QuestionType>[] = [
  { value: 'single', label: '单选题' },
  { value: 'multiple', label: '多选题' },
  { value: 'judge', label: '判断题' }
]

export const classroomStatusOptions: LabelOption<ClassroomStatus>[] = [
  { value: 'scheduled', label: '待开课' },
  { value: 'open', label: '进行中' },
  { value: 'closed', label: '已结课' }
]

export const recordStatusLabels: Record<string, string> = {
  notStarted: '未开始',
  inProgress: '学习中',
  completed: '已完成'
}

export function labelOf<T extends string>(options: LabelOption<T>[], value: string | null | undefined): string {
  return options.find(option => option.value === value)?.label || value || '—'
}

export function classroomStatusType(status: ClassroomStatus): 'info' | 'success' | 'warning' {
  if (status === 'open') return 'success'
  if (status === 'closed') return 'info'
  return 'warning'
}

export function recordStatusType(status: string | undefined): 'info' | 'success' | 'warning' {
  if (status === 'completed') return 'success'
  if (status === 'inProgress') return 'warning'
  return 'info'
}

export function durationText(minutes: number): string {
  if (minutes <= 0) return '—'
  if (minutes < 60) return `${minutes} 分钟`
  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  return rest ? `${hours} 小时 ${rest} 分钟` : `${hours} 小时`
}

export function classroomResourceCounts(counts: {
  theoryCount: number
  theoryPracticeCount: number
  caseCount: number
  casePracticeCount: number
}): string {
  return `理论讲解 ${counts.theoryCount} · 理论练习 ${counts.theoryPracticeCount} · 案例讲解 ${counts.caseCount} · 案例实操 ${counts.casePracticeCount}`
}

export function questionAnswerText(question: { options: string[]; answers?: number[] }): string {
  const letters = ['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J']
  return (question.answers || [])
    .map(index => `${letters[index] || index + 1}. ${question.options[index] ?? ''}`)
    .join('、')
}

import type { ExamQuestionType, ExamStatus } from './types'
import type { LabelOption } from './teaching'

export const examQuestionTypeOptions: LabelOption<ExamQuestionType>[] = [
  { value: 'single', label: '单选题' },
  { value: 'multiple', label: '多选题' },
  { value: 'judge', label: '判断题' },
  { value: 'practical', label: '实操题' }
]

export const examStatusOptions: LabelOption<ExamStatus>[] = [
  { value: 'scheduled', label: '待开考' },
  { value: 'open', label: '进行中' },
  { value: 'closed', label: '已结束' }
]

export const attemptStatusLabels: Record<string, string> = {
  notStarted: '未参加',
  inProgress: '作答中',
  submitted: '待批阅',
  graded: '已批阅'
}

export const optionLetters = ['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J']

export function examQuestionTypeLabel(type: string | null | undefined): string {
  return examQuestionTypeOptions.find(option => option.value === type)?.label || type || '—'
}

export function examStatusLabel(status: string | null | undefined): string {
  return examStatusOptions.find(option => option.value === status)?.label || status || '—'
}

export function examStatusType(status: string): 'info' | 'success' | 'warning' {
  if (status === 'open') return 'success'
  if (status === 'closed') return 'info'
  return 'warning'
}

export function attemptStatusLabel(status: string | null | undefined): string {
  return attemptStatusLabels[status || ''] || status || '—'
}

export function attemptStatusType(status: string | undefined): 'info' | 'success' | 'warning' | 'danger' {
  if (status === 'graded') return 'success'
  if (status === 'submitted') return 'warning'
  if (status === 'inProgress') return 'danger'
  return 'info'
}

export function deploymentLabel(status: string | undefined): string {
  if (status === 'deployed') return '环境已就绪'
  if (status === 'failed') return '部署失败'
  return '待部署'
}

export function deploymentType(status: string | undefined): 'success' | 'info' | 'danger' {
  return status === 'deployed' ? 'success' : status === 'failed' ? 'danger' : 'info'
}

/** Formats an absolute remaining time as hh:mm:ss, keeping hours visible for long exams. */
export function countdownText(seconds: number): string {
  const total = Math.max(0, Math.floor(seconds))
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const rest = total % 60
  const parts = [minutes, rest].map(value => String(value).padStart(2, '0'))
  return hours > 0 ? `${hours}:${parts.join(':')}` : parts.join(':')
}

export function selectedText(question: { options: string[]; selected?: number[]; answers?: number[] }): string {
  const selected = question.selected ?? question.answers ?? []
  if (!selected.length) return '未作答'
  return selected
    .slice()
    .sort((left, right) => left - right)
    .map(index => `${optionLetters[index] || index + 1}. ${question.options[index] ?? ''}`)
    .join('、')
}

export function typeCountText(counts: Partial<Record<ExamQuestionType, number>> | undefined): string {
  if (!counts) return '—'
  const parts = examQuestionTypeOptions
    .filter(option => (counts[option.value] || 0) > 0)
    .map(option => `${option.label} ${counts[option.value]}`)
  return parts.length ? parts.join(' · ') : '暂无题目'
}

export function unansweredCount(
  questions: { type: ExamQuestionType; selected: number[]; textAnswer: string }[]
): number {
  return questions.filter(question =>
    question.type === 'practical' ? !question.textAnswer.trim() : question.selected.length === 0
  ).length
}

/** Downloads an authenticated API file and saves it under a readable name. */
export async function downloadFile(path: string, fileName: string): Promise<void> {
  const response = await fetch(`/api${path}`, { credentials: 'same-origin' })
  if (!response.ok) {
    let message = `下载失败（${response.status}）`
    try {
      const body = await response.json()
      if (body?.message) message = body.message
    } catch {
      // Non-JSON failure bodies keep the generic message.
    }
    throw new Error(message)
  }
  const blob = await response.blob()
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  document.body.append(anchor)
  anchor.click()
  anchor.remove()
  URL.revokeObjectURL(url)
}

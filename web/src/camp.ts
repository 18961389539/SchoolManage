import type { CampAttemptStatus, CampSkillRow } from './types'

export const campAttemptStatusLabels: Record<string, string> = {
  inProgress: '测评中',
  graded: '已完成'
}

export function campAttemptStatusLabel(status: string | null | undefined): string {
  return campAttemptStatusLabels[status || ''] || status || '—'
}

export function campAttemptStatusType(status: string | undefined): 'info' | 'warning' | 'success' {
  if (status === 'graded') return 'success'
  if (status === 'inProgress') return 'warning'
  return 'info'
}

export function campResultType(result: string): 'success' | 'warning' | 'danger' | 'info' {
  if (result === '掌握' || result === '优秀') return 'success'
  if (result === '基本掌握' || result === '良好' || result === '合格') return 'warning'
  if (result === '待提升') return 'danger'
  return 'info'
}

export function campLevelType(level: string): 'success' | 'warning' | 'danger' | 'info' {
  if (level === '优秀') return 'success'
  if (level === '良好' || level === '合格') return 'warning'
  if (level === '待提升') return 'danger'
  return 'info'
}

export function skillRowKey(row: CampSkillRow, index: number): string {
  return `${row.skill}-${row.subSkill}-${index}`
}

export function virtualMachineStatusText(status: string): string {
  if (status === 'running') return '运行中'
  if (status === 'stopped') return '已停止'
  return status || '状态未知'
}

export function campAttemptLabel(status: CampAttemptStatus | undefined): string {
  return status ? campAttemptStatusLabel(status) : '未开始'
}

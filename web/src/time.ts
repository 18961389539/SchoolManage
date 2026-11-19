export const schoolTimeZone = 'Asia/Shanghai'

/** The API stores UTC. Older SQLite responses may omit the UTC designator. */
export function parseApiDate(value: unknown): Date {
  if (value instanceof Date) return value
  let text = String(value ?? '').trim()
  if (/^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}/.test(text) && !/(?:Z|[+-]\d{2}:?\d{2})$/i.test(text)) {
    text = text.replace(' ', 'T') + 'Z'
  }
  return new Date(text)
}

export function dateTime(value: unknown): string {
  if (!value) return '—'
  const date = parseApiDate(value)
  if (Number.isNaN(date.getTime())) return String(value)
  return new Intl.DateTimeFormat('zh-CN', {
    timeZone: schoolTimeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hourCycle: 'h23'
  }).format(date)
}

/** datetime-local controls carry wall-clock text, so format and parse explicitly. */
export function shanghaiInputTime(value: unknown): string {
  const date = parseApiDate(value)
  if (Number.isNaN(date.getTime())) return ''
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: schoolTimeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hourCycle: 'h23'
  }).formatToParts(date)
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find(item => item.type === type)?.value || ''
  return `${part('year')}-${part('month')}-${part('day')}T${part('hour')}:${part('minute')}:${part('second')}`
}

export function shanghaiInputToIso(value: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,3})?)?$/.test(value)) return null
  const date = new Date(`${value}+08:00`)
  return Number.isNaN(date.getTime()) ? null : date.toISOString()
}

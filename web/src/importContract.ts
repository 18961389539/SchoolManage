import type { ImportPreviewResponse, ImportRowsPage, ImportStatusResponse } from './types'

const previewKeys = ['batchId', 'rows', 'validCount', 'errorCount'] as const
const statusKeys = [
  'batchId',
  'status',
  'processedCount',
  'totalCount',
  'importedCount',
  'removedCount',
  'failureMessage',
  'createdAt',
  'queuedAt',
  'startedAt',
  'completedAt',
  'message'
] as const
const rowPageKeys = ['rows', 'totalCount', 'page', 'pageSize'] as const
const rowKeys = ['rowNumber', 'data', 'errors'] as const
const dataKeys = ['name', 'userName', 'number', 'className', 'gender', 'phone', 'email'] as const
const issueKeys = ['column', 'message'] as const

function record(value: unknown, label: string): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error(`${label}响应格式不正确`)
  return value as Record<string, unknown>
}

function expectKeys(value: Record<string, unknown>, keys: readonly string[], label: string): void {
  const missing = keys.filter(key => !(key in value))
  if (missing.length) throw new Error(`${label}响应缺少字段：${missing.join('、')}（前后端契约不一致）`)
}

function expectArray(value: unknown, label: string): unknown[] {
  if (!Array.isArray(value)) throw new Error(`${label}响应格式不正确`)
  return value
}

/**
 * The backend records pin these fields, but a rename would otherwise arrive as `undefined` and
 * render as blanks. The guard turns that into one readable error at the call site.
 */
export function assertImportPreview(value: unknown): ImportPreviewResponse {
  const payload = record(value, '导入预览')
  expectKeys(payload, previewKeys, '导入预览')
  for (const raw of expectArray(payload.rows, '导入预览 rows')) {
    const row = record(raw, '导入预览行')
    expectKeys(row, rowKeys, '导入预览行')
    expectKeys(record(row.data, '导入预览行数据'), dataKeys, '导入预览行数据')
    for (const rawIssue of expectArray(row.errors, '导入预览错误'))
      expectKeys(record(rawIssue, '导入预览错误'), issueKeys, '导入预览错误')
  }
  return payload as unknown as ImportPreviewResponse
}

export function assertImportRows(value: unknown): ImportRowsPage {
  const payload = record(value, '导入预览分页')
  expectKeys(payload, rowPageKeys, '导入预览分页')
  for (const raw of expectArray(payload.rows, '导入预览分页 rows')) {
    const row = record(raw, '导入预览行')
    expectKeys(row, rowKeys, '导入预览行')
    expectKeys(record(row.data, '导入预览行数据'), dataKeys, '导入预览行数据')
    for (const rawIssue of expectArray(row.errors, '导入预览错误'))
      expectKeys(record(rawIssue, '导入预览错误'), issueKeys, '导入预览错误')
  }
  return payload as unknown as ImportRowsPage
}

export function assertImportStatus(value: unknown): ImportStatusResponse {
  const payload = record(value, '导入进度')
  expectKeys(payload, statusKeys, '导入进度')
  return payload as unknown as ImportStatusResponse
}

import { describe, expect, it } from 'vitest'
import { assertImportPreview, assertImportStatus } from './importContract'

const preview = {
  batchId: 'batch-1',
  validCount: 1,
  errorCount: 0,
  rows: [
    {
      rowNumber: 2,
      data: {
        name: '张三',
        userName: 'zhangsan',
        number: 'S001',
        className: '网络一班',
        gender: '男',
        phone: '13800000000',
        email: 'a@b.cn'
      },
      errors: []
    }
  ]
}

const status = {
  batchId: 'batch-1',
  status: 'queued',
  processedCount: 0,
  totalCount: 1,
  importedCount: 0,
  removedCount: 0,
  failureMessage: '',
  createdAt: '2026-01-01T00:00:00Z',
  queuedAt: null,
  startedAt: null,
  completedAt: null,
  message: '导入任务已进入队列'
}

function without(value: Record<string, unknown>, key: string): Record<string, unknown> {
  const copy = { ...value }
  delete copy[key]
  return copy
}

describe('import contract guards', () => {
  it('accepts complete preview and status payloads', () => {
    expect(assertImportPreview(preview)).toBe(preview)
    expect(assertImportStatus(status)).toBe(status)
  })

  it('names the missing top-level field', () => {
    expect(() => assertImportPreview(without(preview, 'rows'))).toThrow('导入预览响应缺少字段：rows')
    expect(() => assertImportStatus(without(status, 'message'))).toThrow('导入进度响应缺少字段：message')
  })

  it('checks every preview row, its data and its issues', () => {
    const missingData = { ...preview, rows: [{ ...preview.rows[0], data: without(preview.rows[0].data, 'email') }] }
    expect(() => assertImportPreview(missingData)).toThrow('导入预览行数据响应缺少字段：email')

    const badIssue = { ...preview, rows: [{ ...preview.rows[0], errors: [{ column: '账号' }] }] }
    expect(() => assertImportPreview(badIssue)).toThrow('导入预览错误响应缺少字段：message')
  })

  it('rejects payloads that are not objects or arrays', () => {
    expect(() => assertImportPreview(null)).toThrow('导入预览响应格式不正确')
    expect(() => assertImportPreview({ ...preview, rows: 'nope' })).toThrow('导入预览 rows响应格式不正确')
    expect(() => assertImportStatus([])).toThrow('导入进度响应格式不正确')
  })
})

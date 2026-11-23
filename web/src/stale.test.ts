import { describe, expect, it } from 'vitest'
import { ApiError } from './api'
import { isStale } from './stale'

describe('stale conflict detection', () => {
  it('recognises the server stale marker', () => {
    expect(isStale(new ApiError('已被其他人修改', 409, { message: '已被其他人修改', code: 'stale' }))).toBe(true)
  })

  it('leaves business conflicts alone', () => {
    expect(isStale(new ApiError('课程名称已存在', 409, { message: '课程名称已存在' }))).toBe(false)
    expect(isStale(new ApiError('已有学习记录的资源不能更改类型。', 409))).toBe(false)
  })

  it('ignores other statuses and plain errors', () => {
    expect(isStale(new ApiError('缺少版本号', 400, { code: 'stale' }))).toBe(false)
    expect(isStale(new Error('stale'))).toBe(false)
    expect(isStale(undefined)).toBe(false)
  })
})

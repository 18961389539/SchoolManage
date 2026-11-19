import { describe, expect, it } from 'vitest'
import { ApiError, bytes, listItems, messageOf, passwordIssue } from './api'

const policyMessage = '密码须为 12–128 位，且同时包含字母和数字'

describe('passwordIssue', () => {
  it('accepts 12–128 characters containing letters and digits', () => {
    expect(passwordIssue('abcd12345678')).toBe('')
    expect(passwordIssue('A1b2C3d4E5f6G7h8')).toBe('')
  })

  it('rejects values outside the policy', () => {
    expect(passwordIssue('')).toBe(policyMessage)
    expect(passwordIssue('abcd1234567')).toBe(policyMessage)
    expect(passwordIssue('abcdefghijkl')).toBe(policyMessage)
    expect(passwordIssue('123456789012')).toBe(policyMessage)
    expect(passwordIssue('a'.repeat(128) + '1')).toBe(policyMessage)
  })
})

describe('listItems', () => {
  it('unwraps paged responses and plain arrays', () => {
    expect(listItems([{ id: 1 }])).toEqual([{ id: 1 }])
    expect(listItems({ total: 2, items: [{ id: 1 }, { id: 2 }] })).toHaveLength(2)
  })

  it('returns an empty array for unexpected payloads', () => {
    expect(listItems(null)).toEqual([])
    expect(listItems({ total: 0 })).toEqual([])
    expect(listItems('nope')).toEqual([])
  })
})

describe('bytes', () => {
  it('formats byte sizes with binary units', () => {
    expect(bytes(0)).toBe('0 B')
    expect(bytes(1023)).toBe('1023 B')
    expect(bytes(1024)).toBe('1.0 KB')
    expect(bytes(1536)).toBe('1.5 KB')
    expect(bytes(1024 ** 3)).toBe('1.0 GB')
    expect(bytes('1024')).toBe('1.0 KB')
  })

  it('returns a dash for non-numeric values', () => {
    expect(bytes('abc')).toBe('—')
    expect(bytes(undefined)).toBe('—')
  })
})

describe('messageOf', () => {
  it('uses Error messages and falls back for unknown values', () => {
    expect(messageOf(new Error('boom'))).toBe('boom')
    expect(messageOf(new ApiError('服务不可用', 503))).toBe('服务不可用')
    expect(messageOf('nope')).toBe('操作失败，请稍后重试')
  })
})

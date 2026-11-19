import { describe, expect, it } from 'vitest'
import { ApiError } from './api'
import { classifySessionError, probeSession } from './session'
import type { SessionResponse } from './types'

const session: SessionResponse = {
  user: { id: 'u1', userName: 'admin', displayName: '平台管理员', role: 'Admin', mustChangePassword: false },
  csrfToken: 'token'
}

describe('classifySessionError', () => {
  it('treats 401 as signed out', () => {
    expect(classifySessionError(new ApiError('未登录', 401))).toBe('anonymous')
  })

  it('treats every other failure as unavailable', () => {
    expect(classifySessionError(new ApiError('服务异常', 500))).toBe('unavailable')
    expect(classifySessionError(new ApiError('无法连接本地服务', 0))).toBe('unavailable')
    expect(classifySessionError(new Error('boom'))).toBe('unavailable')
  })
})

describe('probeSession', () => {
  it('returns the authenticated session on success', async () => {
    await expect(probeSession(async () => session)).resolves.toEqual({ kind: 'authenticated', session })
  })

  it('returns anonymous for 401 without surfacing an error', async () => {
    await expect(probeSession(async () => Promise.reject(new ApiError('未登录', 401)))).resolves.toEqual({
      kind: 'anonymous'
    })
  })

  it('returns an unavailable state with the original message', async () => {
    await expect(probeSession(async () => Promise.reject(new ApiError('数据库忙', 503)))).resolves.toEqual({
      kind: 'unavailable',
      message: '数据库忙'
    })
  })
})

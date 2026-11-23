import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { appState, applyServerInfo, applySession, expireSession, maxUploadMegabytes, signOut } from './store'

const user = { id: 'u1', userName: 'admin', displayName: '管理员', role: 'Admin', mustChangePassword: false }

beforeEach(() => {
  appState.user = null
  appState.csrfToken = ''
  appState.schoolName = '校内实训资源平台'
  appState.version = ''
  appState.maxUploadBytes = 16 * 1024 * 1024
  appState.authNotice = ''
  // signOut clears remembered list filters through the session storage API.
  vi.stubGlobal('sessionStorage', {
    length: 0,
    key: () => null,
    removeItem: () => {},
    getItem: () => null,
    setItem: () => {}
  })
})

afterEach(() => vi.unstubAllGlobals())

describe('session state', () => {
  it('keeps the signed-in user and the token that mutating requests need', () => {
    applySession({ user, csrfToken: 'token-1' })
    expect(appState.user).toEqual(user)
    expect(appState.csrfToken).toBe('token-1')
    expect(appState.authNotice).toBe('')
  })

  it('does not keep a token the server did not send', () => {
    applySession({ user, csrfToken: '' })
    expect(appState.csrfToken).toBe('')
  })

  it('drops the session, the token and any notice on sign-out', () => {
    applySession({ user, csrfToken: 'token-1' })
    signOut()
    expect(appState.user).toBeNull()
    expect(appState.csrfToken).toBe('')
    expect(appState.authNotice).toBe('')
  })

  it('explains an expired session once', () => {
    applySession({ user, csrfToken: 'token-1' })
    expect(expireSession()).toBe(true)
    expect(appState.user).toBeNull()
    expect(appState.authNotice).toBe('登录状态已失效，请重新登录。')
  })

  it('stays quiet when an anonymous visitor is rejected', () => {
    expect(expireSession()).toBe(false)
    expect(appState.authNotice).toBe('')
  })
})

describe('server info', () => {
  it('takes branding, version and the upload limit from the server', () => {
    applyServerInfo({ version: '1.2.3', schoolName: '实训中心', maxUploadBytes: 32 * 1024 * 1024 })
    expect(appState.version).toBe('1.2.3')
    expect(appState.schoolName).toBe('实训中心')
    expect(maxUploadMegabytes()).toBe(32)
  })

  it('ignores a missing school name and a nonsensical limit', () => {
    applyServerInfo({ version: '', schoolName: null, maxUploadBytes: 0 })
    expect(appState.schoolName).toBe('校内实训资源平台')
    expect(appState.version).toBe('')
    expect(maxUploadMegabytes()).toBe(16)
  })
})

import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, post } from './api'
import { cancelPageReads } from './requestScope'
import { appState, applySession } from './store'

const signals: AbortSignal[] = []

function hangFetch() {
  vi.stubGlobal(
    'fetch',
    vi.fn((_url: string, init: RequestInit) => {
      const signal = init.signal as AbortSignal
      signals.push(signal)
      return new Promise((_resolve, reject) => {
        signal.addEventListener('abort', () => {
          const reason = signal.reason
          reject(reason instanceof DOMException ? reason : new DOMException('aborted', 'AbortError'))
        })
      })
    })
  )
}

afterEach(() => {
  signals.length = 0
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

describe('request timeout', () => {
  it('turns a hung request into a timeout error the page can show', async () => {
    vi.useFakeTimers()
    hangFetch()
    const pending = api('/slow', { timeoutMs: 20 })
    const assertion = expect(pending).rejects.toMatchObject({ status: 0, details: { code: 'timeout' } })
    await vi.advanceTimersByTimeAsync(40)
    await assertion
    expect(signals[0].aborted).toBe(true)
  })

  it('waits indefinitely when the caller opts out', async () => {
    vi.useFakeTimers()
    hangFetch()
    const pending = api('/backup', { method: 'POST', timeoutMs: 0 })
    pending.catch(() => {})
    await vi.advanceTimersByTimeAsync(120_000)
    expect(signals[0].aborted).toBe(false)
  })
})

describe('navigation cancels reads only', () => {
  it('abandons an in-flight list request without handing the page an error', async () => {
    hangFetch()
    let settled = false
    api('/courses').then(
      () => (settled = true),
      () => (settled = true)
    )
    cancelPageReads()
    await new Promise(resolve => setTimeout(resolve, 10))
    expect(signals[0].aborted).toBe(true)
    expect(settled).toBe(false)
  })

  it('leaves writes and app-level reads running', async () => {
    hangFetch()
    const save = post('/courses/1', { name: '课程' })
    const menu = api('/teaching/availability', { pageScoped: false })
    save.catch(() => {})
    menu.catch(() => {})
    cancelPageReads()
    expect(signals[0].aborted).toBe(false)
    expect(signals[1].aborted).toBe(false)
  })

  it('still surfaces the server message on a failed response', async () => {
    const headers = new Headers({ 'content-type': 'application/json' })
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify({ message: '课程名称已存在' }), { status: 409, headers }))
    )
    await expect(api('/courses')).rejects.toMatchObject({ status: 409, message: '课程名称已存在' })
  })

  it('ends the session when the server rejects the cookie, without a second error toast', async () => {
    vi.stubGlobal('sessionStorage', {
      length: 0,
      key: () => null,
      removeItem: () => {},
      getItem: () => null,
      setItem: () => {}
    })
    applySession({
      user: { id: 'u1', userName: 'admin', displayName: '管理员', role: 'Admin', mustChangePassword: false },
      csrfToken: 't'
    })
    const headers = new Headers({ 'content-type': 'application/json' })
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify({ message: '请先登录' }), { status: 401, headers }))
    )
    let settled = false
    api('/courses').then(
      () => (settled = true),
      () => (settled = true)
    )
    await new Promise(resolve => setTimeout(resolve, 10))
    expect(settled).toBe(false)
    expect(appState.user).toBeNull()
    expect(appState.authNotice).toBe('登录状态已失效，请重新登录。')
  })
})

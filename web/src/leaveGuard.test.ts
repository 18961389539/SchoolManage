import { afterEach, describe, expect, it } from 'vitest'
import { canLeave, hasLeaveGuard, registerLeaveGuard, resetLeaveGuard } from './leaveGuard'

afterEach(() => resetLeaveGuard())

describe('leave guard', () => {
  it('allows navigation when no page is holding work', async () => {
    expect(hasLeaveGuard()).toBe(false)
    await expect(canLeave()).resolves.toBe(true)
  })

  it('honours a refusal from the active page', async () => {
    registerLeaveGuard(() => false)
    await expect(canLeave()).resolves.toBe(false)
  })

  it('releases the guard when the page unmounts', async () => {
    const release = registerLeaveGuard(() => false)
    release()
    expect(hasLeaveGuard()).toBe(false)
    await expect(canLeave()).resolves.toBe(true)
  })

  it('does not let a stale page release a newer guard', async () => {
    const release = registerLeaveGuard(() => false)
    registerLeaveGuard(() => true)
    release()
    await expect(canLeave()).resolves.toBe(true)
  })

  it('treats a guard that throws as a refusal', async () => {
    registerLeaveGuard(() => {
      throw new Error('保存失败')
    })
    await expect(canLeave()).resolves.toBe(false)
  })

  it('awaits an asynchronous guard', async () => {
    let settle: (value: boolean) => void = () => {}
    registerLeaveGuard(() => new Promise<boolean>(resolve => (settle = resolve)))
    const pending = canLeave()
    settle(true)
    await expect(pending).resolves.toBe(true)
  })

  it('refuses a second attempt while one confirmation is still open', async () => {
    let settle: (value: boolean) => void = () => {}
    registerLeaveGuard(() => new Promise<boolean>(resolve => (settle = resolve)))
    const first = canLeave()
    await expect(canLeave()).resolves.toBe(false)
    settle(true)
    await expect(first).resolves.toBe(true)
  })
})

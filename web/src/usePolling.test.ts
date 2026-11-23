import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { startPolling } from './usePolling'

function visibility() {
  let hidden = false
  let listener: (() => void) | undefined
  return {
    isHidden: () => hidden,
    watch: (next: () => void) => {
      listener = next
      return () => {
        listener = undefined
      }
    },
    set(value: boolean) {
      hidden = value
      if (!value) listener?.()
    }
  }
}

function clock(refresh: () => unknown, intervalMs: number, view = visibility()) {
  const seen: number[] = []
  const stop = startPolling({
    refresh,
    intervalMs,
    clockMs: 1000,
    isHidden: view.isHidden,
    onNow: value => seen.push(value),
    watchVisibility: view.watch
  })
  return { view, seen, stop }
}

describe('startPolling', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-01-01T00:00:00.000Z'))
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('ticks the clock and refreshes on its own cadence', async () => {
    const refresh = vi.fn()
    const { seen } = clock(refresh, 30_000)

    await vi.advanceTimersByTimeAsync(30_000)

    expect(seen).toHaveLength(30)
    expect(refresh).toHaveBeenCalledTimes(1)
  })

  it('skips ticks while hidden and catches up when the tab returns', async () => {
    const refresh = vi.fn()
    const { view, seen } = clock(refresh, 30_000)

    view.set(true)
    await vi.advanceTimersByTimeAsync(60_000)
    expect(refresh).not.toHaveBeenCalled()
    expect(seen).toHaveLength(0)

    view.set(false)
    expect(refresh).toHaveBeenCalledTimes(1)
    expect(seen).toHaveLength(1)
  })

  it('never overlaps refreshes', async () => {
    let release = () => {}
    const refresh = vi.fn(
      () =>
        new Promise<void>(resolve => {
          release = resolve
        })
    )
    clock(refresh, 30_000)

    await vi.advanceTimersByTimeAsync(30_000)
    expect(refresh).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(60_000)
    expect(refresh).toHaveBeenCalledTimes(1)

    release()
    await vi.advanceTimersByTimeAsync(0)
    await vi.advanceTimersByTimeAsync(30_000)
    expect(refresh).toHaveBeenCalledTimes(2)
  })

  it('turns off the clock and refresh when their cadence is zero', async () => {
    const refresh = vi.fn()
    const seen: number[] = []
    const stop = startPolling({
      refresh,
      intervalMs: 0,
      clockMs: 0,
      isHidden: () => false,
      onNow: value => seen.push(value),
      watchVisibility: () => () => {}
    })

    await vi.advanceTimersByTimeAsync(60_000)

    expect(refresh).not.toHaveBeenCalled()
    expect(seen).toHaveLength(0)
    stop()
  })

  it('stops timers and detaches the visibility listener', async () => {
    const refresh = vi.fn()
    const { view, seen, stop } = clock(refresh, 30_000)

    stop()
    view.set(false)
    await vi.advanceTimersByTimeAsync(60_000)

    expect(refresh).not.toHaveBeenCalled()
    expect(seen).toHaveLength(0)
  })
})

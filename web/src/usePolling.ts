import { onBeforeUnmount, onMounted, ref } from 'vue'

export interface PollingOptions {
  /** Refresh cadence; 0 disables periodic refresh. */
  intervalMs?: number
  /** Clock cadence for countdowns; 0 disables the clock. Defaults to 1 s. */
  clockMs?: number
}

interface PollingClock {
  refresh: () => unknown
  intervalMs: number
  clockMs: number
  isHidden: () => boolean
  onNow: (value: number) => void
  watchVisibility: (listener: () => void) => () => void
}

/**
 * The scheduling rules behind usePolling, split out so they can be tested without a component
 * instance. It never refreshes on start: the caller's first load owns its loading state.
 */
export function startPolling(clock: PollingClock): () => void {
  let pending = false
  let stopped = false
  const refresh = async () => {
    if (stopped || pending || clock.isHidden()) return
    pending = true
    try {
      await clock.refresh()
    } catch {
      // The page reports its own load errors; polling just tries again on the next tick.
    } finally {
      pending = false
    }
  }
  const refreshTimer = clock.intervalMs > 0 ? setInterval(() => void refresh(), clock.intervalMs) : undefined
  const clockTimer =
    clock.clockMs > 0
      ? setInterval(() => {
          if (!clock.isHidden()) clock.onNow(Date.now())
        }, clock.clockMs)
      : undefined
  const detach = clock.watchVisibility(() => {
    if (clock.isHidden()) return
    // A tab that was left in the background catches up as soon as it is looked at again.
    clock.onNow(Date.now())
    void refresh()
  })
  return () => {
    stopped = true
    if (refreshTimer !== undefined) clearInterval(refreshTimer)
    if (clockTimer !== undefined) clearInterval(clockTimer)
    detach()
  }
}

/**
 * A shared countdown clock plus a background refresh. Ticks are skipped while the tab is hidden
 * or a refresh is still running, and returning to the tab refreshes immediately. Keeps
 * undismissed pages from showing stale timers without every page growing its own interval.
 */
export function usePolling(refresh: () => unknown, options: PollingOptions = {}) {
  const now = ref(Date.now())
  let stop = () => {}
  onMounted(() => {
    stop = startPolling({
      refresh,
      intervalMs: options.intervalMs ?? 0,
      clockMs: options.clockMs ?? 1000,
      isHidden: () => document.hidden,
      onNow: value => {
        now.value = value
      },
      watchVisibility: listener => {
        document.addEventListener('visibilitychange', listener)
        return () => document.removeEventListener('visibilitychange', listener)
      }
    })
  })
  onBeforeUnmount(() => stop())
  return { now }
}

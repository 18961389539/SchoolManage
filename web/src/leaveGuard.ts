/**
 * A single in-app navigation guard for work that must not be left silently.
 * Only the page the operator is currently working in registers a guard
 * (an exam or an assessment in progress); the shell asks before every route
 * change so the sidebar, breadcrumbs, hash links and the browser back button
 * all behave the same way. Browser refresh/close is handled per page through
 * `beforeunload`, because that prompt cannot wait for a confirmation dialog.
 */
export type LeaveGuard = () => boolean | Promise<boolean>

let guard: LeaveGuard | null = null
let asking = false

/** Returns the release function; call it when the page unmounts. */
export function registerLeaveGuard(next: LeaveGuard): () => void {
  guard = next
  return () => {
    if (guard === next) guard = null
  }
}

export function hasLeaveGuard() {
  return guard !== null
}

/**
 * True when it is safe to leave. A guard that throws is treated as a refusal:
 * losing exam answers is worse than a navigation that does nothing. While one
 * confirmation is open, further attempts are refused so two dialogs cannot stack.
 */
export async function canLeave(): Promise<boolean> {
  if (!guard) return true
  if (asking) return false
  asking = true
  try {
    return await guard()
  } catch {
    return false
  } finally {
    asking = false
  }
}

/** Test seam: drop the registered guard. */
export function resetLeaveGuard() {
  guard = null
  asking = false
}

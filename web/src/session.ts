import { ApiError, messageOf } from './api'
import type { SessionResponse } from './types'

export type SessionOutcome =
  { kind: 'authenticated'; session: SessionResponse } | { kind: 'anonymous' } | { kind: 'unavailable'; message: string }

/** A 401 means "not signed in"; every other failure means the platform is unreachable. */
export function classifySessionError(error: unknown): 'anonymous' | 'unavailable' {
  return error instanceof ApiError && error.status === 401 ? 'anonymous' : 'unavailable'
}

export async function probeSession(loadSession: () => Promise<SessionResponse>): Promise<SessionOutcome> {
  try {
    return { kind: 'authenticated', session: await loadSession() }
  } catch (error) {
    return classifySessionError(error) === 'anonymous'
      ? { kind: 'anonymous' }
      : { kind: 'unavailable', message: messageOf(error) }
  }
}

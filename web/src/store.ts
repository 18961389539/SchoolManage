import { reactive } from 'vue'
import { clearListStates } from './listState'
import { cancelPageReads } from './requestScope'
import type { SessionResponse, SessionUser, StatusResponse } from './types'

/**
 * The state the shell and the pages share. One reactive object replaces a module variable for
 * the CSRF token, another for upload limits, and window events the shell had to re-parse to
 * learn that the school or display name changed.
 */
export const appState = reactive({
  user: null as SessionUser | null,
  csrfToken: '',
  schoolName: '校内实训资源平台',
  version: '',
  maxUploadBytes: 16 * 1024 * 1024,
  /** One-shot line for the sign-in screen, e.g. after the server rejected the session. */
  authNotice: ''
})

export const maxUploadMegabytes = () => Math.round(appState.maxUploadBytes / (1024 * 1024))

/** Limits and branding come from `/status`; the server value always wins over the defaults. */
export function applyServerInfo(status: Pick<StatusResponse, 'version' | 'schoolName' | 'maxUploadBytes'>) {
  if (status.version) appState.version = String(status.version)
  if (status.schoolName) appState.schoolName = status.schoolName
  if (typeof status.maxUploadBytes === 'number' && status.maxUploadBytes > 0)
    appState.maxUploadBytes = status.maxUploadBytes
}

export function applySession(session: SessionResponse) {
  appState.user = session.user
  appState.csrfToken = session.csrfToken || ''
  appState.authNotice = ''
}

/**
 * Ends the session locally. Filters and in-flight reads belong to the account that left, so
 * both sign-out paths — the button and a rejected cookie — go through here.
 */
export function signOut(notice = '') {
  appState.user = null
  appState.csrfToken = ''
  appState.authNotice = notice
  clearListStates()
  cancelPageReads()
}

/**
 * 401 on a request we believed was authorized: the cookie is gone, say so once.
 * Returns false when nobody was signed in — an anonymous probe is not an expiry.
 */
export function expireSession(): boolean {
  if (!appState.user) return false
  signOut('登录状态已失效，请重新登录。')
  return true
}

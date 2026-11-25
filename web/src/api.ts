import { flushEmergencyDrafts } from './draftPersistence'
import { pageLeft, trackPageRead } from './requestScope'
import { appState, expireSession } from './store'

export type Row = Record<string, any>
export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
    public details?: unknown
  ) {
    super(message)
  }
}
export interface ApiOptions extends RequestInit {
  /**
   * Milliseconds before the request is abandoned. 0 waits indefinitely — reserve it for
   * operations the server may legitimately spend minutes on (backups, VM power, file transfer).
   */
  timeoutMs?: number
  /**
   * Reads are cancelled when the operator changes page; set false for app-level reads whose
   * result still matters after navigation (menu availability, session probe).
   */
  pageScoped?: boolean
}
const defaultTimeoutMs = 60_000
const fileTimeoutMs = 10 * 60_000
const timedOut = { reason: 'timeout' }

function requestTimeout(init: RequestInit, override?: number): number {
  if (override !== undefined) return override
  const isUpload = typeof FormData !== 'undefined' && init.body instanceof FormData
  return isUpload ? fileTimeoutMs : defaultTimeoutMs
}
/** Mirror an optional caller signal onto our controller so `fetch` only listens to one. */
function relayAbort(controller: AbortController, external?: AbortSignal | null): () => void {
  if (!external) return () => {}
  const relay = () => controller.abort(external.reason)
  if (external.aborted) controller.abort(external.reason)
  else external.addEventListener('abort', relay, { once: true })
  return () => external.removeEventListener('abort', relay)
}
const readMethods = new Set(['GET', 'HEAD'])
function buildHeaders(options: RequestInit): Headers {
  const headers = new Headers(options.headers)
  if (!(options.body instanceof FormData) && options.body) headers.set('Content-Type', 'application/json')
  if (options.method && !['GET', 'HEAD'].includes(options.method.toUpperCase()) && appState.csrfToken)
    headers.set('X-CSRF-TOKEN', appState.csrfToken)
  return headers
}
async function readBody(response: Response): Promise<any> {
  if (response.status === 204) return undefined
  return response.headers.get('content-type')?.includes('json') ? await response.json() : await response.text()
}
function describeError(body: any, status: number): string {
  let message = body?.message || body?.detail || body?.title || `请求失败（${status}）`
  if (body?.errors && typeof body.errors === 'object') {
    const values = Object.values(body.errors).flat().filter(Boolean)
    if (values.length) message += `：${values.join('；')}`
  }
  if (body?.traceId) message += `（追踪号 ${body.traceId}）`
  return message
}
async function request<T>(path: string, options: ApiOptions, read: (response: Response) => Promise<T>): Promise<T> {
  const { timeoutMs, pageScoped, ...init } = options
  const isRead = readMethods.has((init.method ?? 'GET').toUpperCase())
  const limit = requestTimeout(init, timeoutMs)
  // One controller carries both the caller's cancellation and our deadline.
  const controller = new AbortController()
  const detach = isRead && pageScoped !== false ? trackPageRead(controller) : () => {}
  const release = relayAbort(controller, init.signal)
  const timer = limit > 0 ? setTimeout(() => controller.abort(timedOut), limit) : undefined
  try {
    const response = await send(`/api${path}`, {
      ...init,
      headers: buildHeaders(init),
      credentials: 'same-origin',
      signal: controller.signal
    })
    if (!response.ok) {
      const body = await readBody(response)
      throw new ApiError(describeError(body, response.status), response.status, body)
    }
    return await read(response)
  } catch (error) {
    // The page that asked is gone: leave the awaiting caller suspended rather than handing it
    // an error it would render on the screen that has already replaced it.
    if (controller.signal.reason === pageLeft) return new Promise<T>(() => {})
    // A 401 on a live session is handled after that check: the shell is already showing the
    // sign-in notice, so the page that asked must not also paint an error on the way out.
    if (error instanceof ApiError && error.status === 401 && appState.user) {
      flushEmergencyDrafts()
      expireSession()
      throw error
    }
    if (controller.signal.reason === timedOut)
      throw new ApiError(`请求超时（${Math.round(limit / 1000)} 秒），请检查网络后重试`, 0, { code: 'timeout' })
    throw error
  } finally {
    if (timer) clearTimeout(timer)
    release()
    detach()
  }
}

// T has no `any` default on purpose: callers must name the response contract.
export const api = <T>(path: string, options: ApiOptions = {}) =>
  request<T>(path, options, async response => (await readBody(response)) as T)

/**
 * Saves an authenticated API file under the given name. A download is an explicit click like a
 * save, not page content: navigation must not cancel it, and it gets the same long deadline as
 * an upload. Session expiry is still handled by the shared pipeline.
 */
export async function downloadFile(path: string, fileName: string): Promise<void> {
  await request(path, { pageScoped: false, timeoutMs: fileTimeoutMs }, async response => {
    const blob = await response.blob()
    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = fileName
    document.body.append(anchor)
    anchor.click()
    anchor.remove()
    URL.revokeObjectURL(url)
  })
}
async function send(path: string, init: RequestInit) {
  try {
    return await fetch(path, init)
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError('无法连接本地服务，请确认平台服务正在运行后重新连接', 0)
  }
}
export const post = <T = void>(path: string, data?: unknown, options: ApiOptions = {}) =>
  api<T>(path, { ...options, method: 'POST', body: data === undefined ? undefined : JSON.stringify(data) })
export const put = <T = void>(path: string, data: unknown, options: ApiOptions = {}) =>
  api<T>(path, { ...options, method: 'PUT', body: JSON.stringify(data) })
export const del = <T = void>(path: string, options: ApiOptions = {}) => api<T>(path, { ...options, method: 'DELETE' })
export const listItems = <T = Row>(data: unknown): T[] => {
  if (Array.isArray(data)) return data as T[]
  const items = (data as { items?: unknown } | null | undefined)?.items
  return Array.isArray(items) ? (items as T[]) : []
}
export const messageOf = (error: unknown) => (error instanceof Error ? error.message : '操作失败，请稍后重试')
export const passwordIssue = (value: string) =>
  value.length >= 12 && value.length <= 128 && /\p{L}/u.test(value) && /\p{Nd}/u.test(value)
    ? ''
    : '密码须为 12–128 位，且同时包含字母和数字'
export { dateTime } from './time'
export const bytes = (value: unknown) => {
  let number = Number(value)
  if (!Number.isFinite(number)) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let index = 0
  while (number >= 1024 && index < units.length - 1) {
    number /= 1024
    index++
  }
  return `${number.toFixed(index ? 1 : 0)} ${units[index]}`
}

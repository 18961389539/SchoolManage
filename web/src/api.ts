export type Row = Record<string, any>
let token = ''
export const setToken = (value: string) => {
  token = value || ''
}
/**
 * Server-declared limits, read from `/api/status` at boot. The 16 MB default only covers the
 * first render before the status response arrives; the server value always wins so the
 * browser cannot refuse a file the server would accept (or the reverse).
 */
export const limits = { maxUploadBytes: 16 * 1024 * 1024 }
export const setLimits = (status: { maxUploadBytes?: number }) => {
  if (typeof status.maxUploadBytes === 'number' && status.maxUploadBytes > 0)
    limits.maxUploadBytes = status.maxUploadBytes
}
export const maxUploadMegabytes = () => Math.round(limits.maxUploadBytes / (1024 * 1024))
export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
    public details?: unknown
  ) {
    super(message)
  }
}
function buildHeaders(options: RequestInit): Headers {
  const headers = new Headers(options.headers)
  if (!(options.body instanceof FormData) && options.body) headers.set('Content-Type', 'application/json')
  if (options.method && !['GET', 'HEAD'].includes(options.method.toUpperCase()) && token)
    headers.set('X-CSRF-TOKEN', token)
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
// T has no `any` default on purpose: callers must name the response contract.
export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  const response = await send(`/api${path}`, { ...options, headers: buildHeaders(options), credentials: 'same-origin' })
  const body = await readBody(response)
  if (!response.ok) {
    if (response.status === 401) window.dispatchEvent(new CustomEvent('session-expired'))
    throw new ApiError(describeError(body, response.status), response.status, body)
  }
  return body as T
}
async function send(path: string, init: RequestInit) {
  try {
    return await fetch(path, init)
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError('无法连接本地服务，请确认平台服务正在运行后重新连接', 0)
  }
}
export const post = <T = void>(path: string, data?: unknown) =>
  api<T>(path, { method: 'POST', body: data === undefined ? undefined : JSON.stringify(data) })
export const put = <T = void>(path: string, data: unknown) =>
  api<T>(path, { method: 'PUT', body: JSON.stringify(data) })
export const del = <T = void>(path: string) => api<T>(path, { method: 'DELETE' })
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

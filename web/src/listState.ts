import { onScopeDispose, watch } from 'vue'
import { normalizePageSize } from './paging'

/**
 * Remembers a list page's filters and pagination for the browser session, so
 * stepping into another module and coming back does not silently drop the
 * operator back to page 1 with no filters. Session scope avoids resurrecting
 * yesterday's search in a later visit; an unreadable or stale entry falls back
 * to the page defaults.
 */
export interface ListState {
  search: string
  filters: Record<string, string>
  page: number
  pageSize: number
}

type ReadableStorage = Pick<Storage, 'getItem'>
type WritableStorage = Pick<Storage, 'setItem'>
type ClearableStorage = Pick<Storage, 'length' | 'key' | 'removeItem'>

const defaultPageSize = 20
const prefix = 'school-training:list:'

function storageKey(key: string) {
  return `${prefix}${key}`
}

function cleanFilters(value: unknown): Record<string, string> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return {}
  return Object.fromEntries(Object.entries(value).filter(([, item]) => typeof item === 'string'))
}

export function readListState(
  key: string,
  storage: ReadableStorage = sessionStorage,
  allowedPageSizes: number[] = [10, 20, 50, 100],
  fallbackPageSize: number = defaultPageSize
): ListState {
  const empty: ListState = {
    search: '',
    filters: {},
    page: 1,
    pageSize: normalizePageSize(fallbackPageSize, allowedPageSizes, fallbackPageSize)
  }
  try {
    const raw = storage.getItem(storageKey(key))
    if (!raw) return empty
    const data = JSON.parse(raw) as Partial<ListState> | null
    if (!data || typeof data !== 'object') return empty
    return {
      search: typeof data.search === 'string' ? data.search : '',
      filters: cleanFilters(data.filters),
      page: Number.isInteger(data.page) && (data.page ?? 0) > 0 ? (data.page as number) : 1,
      pageSize: normalizePageSize(Number(data.pageSize), allowedPageSizes, fallbackPageSize)
    }
  } catch {
    return empty
  }
}

export function writeListState(key: string, state: Partial<ListState>, storage: WritableStorage = sessionStorage) {
  try {
    storage.setItem(storageKey(key), JSON.stringify(state))
  } catch {
    // Storage can be full or blocked; remembering filters is an optimisation, not a requirement.
  }
}

/** Sign-out boundary: a different account must not inherit the previous filters. */
export function clearListStates(storage: ClearableStorage = sessionStorage) {
  try {
    for (let index = storage.length - 1; index >= 0; index--) {
      const key = storage.key(index)
      if (key?.startsWith(prefix)) storage.removeItem(key)
    }
  } catch {
    // Private browsing can deny storage access entirely.
  }
}

/**
 * Keeps a list's search, filters and paging in session storage while the operator works.
 * Writes are coalesced so typing does not hit storage per keystroke, and the last state is
 * flushed when the page unmounts — otherwise a search typed just before navigating is lost.
 * `key` may be a getter for pages that switch lists in place (entity tables, operations).
 */
export function useListPersist(
  key: string | { value: string } | (() => string),
  snapshot: () => Partial<ListState>,
  delay = 250
): () => void {
  const currentKey = typeof key === 'string' ? () => key : typeof key === 'function' ? key : () => key.value
  // A pending write keeps the key it was captured under: switching entity tables or
  // operations screens must not file the previous list's filters under the new one.
  const pending = new Map<string, Partial<ListState>>()
  let timer: ReturnType<typeof setTimeout> | undefined

  function flush() {
    if (timer !== undefined) {
      clearTimeout(timer)
      timer = undefined
    }
    for (const [pendingKey, state] of pending) writeListState(pendingKey, state)
    pending.clear()
  }

  watch(snapshot, () => {
    pending.set(currentKey(), snapshot())
    if (timer !== undefined) clearTimeout(timer)
    timer = setTimeout(flush, delay)
  })

  onScopeDispose(() => {
    flush()
    writeListState(currentKey(), snapshot())
  })
  return flush
}

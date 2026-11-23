import { afterEach, describe, expect, it, vi } from 'vitest'
import { effectScope, nextTick, ref } from 'vue'
import { clearListStates, readListState, useListPersist, writeListState } from './listState'

class MemoryStorage {
  private data = new Map<string, string>()
  get length() {
    return this.data.size
  }
  key(index: number) {
    return [...this.data.keys()][index] ?? null
  }
  getItem(key: string) {
    return this.data.get(key) ?? null
  }
  setItem(key: string, value: string) {
    this.data.set(key, value)
  }
  removeItem(key: string) {
    this.data.delete(key)
  }
}

describe('list state', () => {
  it('falls back to the first page without a saved entry', () => {
    expect(readListState('students', new MemoryStorage())).toEqual({
      search: '',
      filters: {},
      page: 1,
      pageSize: 20
    })
  })

  it('round-trips search, filters, page and page size', () => {
    const storage = new MemoryStorage()
    writeListState(
      'students',
      { search: '张', filters: { classId: 'c1', keyword: '' }, page: 3, pageSize: 50 },
      storage
    )
    expect(readListState('students', storage)).toEqual({
      search: '张',
      filters: { classId: 'c1', keyword: '' },
      page: 3,
      pageSize: 50
    })
  })

  it('keeps each list isolated', () => {
    const storage = new MemoryStorage()
    writeListState('students', { search: 'a', filters: {}, page: 2, pageSize: 20 }, storage)
    writeListState('teachers', { search: 'b', filters: {}, page: 4, pageSize: 10 }, storage)
    expect(readListState('students', storage).page).toBe(2)
    expect(readListState('teachers', storage).page).toBe(4)
    expect(readListState('classes', storage)).toEqual({ search: '', filters: {}, page: 1, pageSize: 20 })
  })

  it('sanitises stored values instead of trusting them', () => {
    const storage = new MemoryStorage()
    storage.setItem(
      'school-training:list:students',
      JSON.stringify({ search: 7, filters: { classId: 1, ok: 'yes' }, page: -2, pageSize: 99 })
    )
    expect(readListState('students', storage)).toEqual({
      search: '',
      filters: { ok: 'yes' },
      page: 1,
      pageSize: 20
    })
  })

  it('uses a list-specific page size fallback', () => {
    const storage = new MemoryStorage()
    writeListState('resources', { search: '', filters: {}, page: 1, pageSize: 99 }, storage)
    expect(readListState('resources', storage, [12, 24, 48], 12).pageSize).toBe(12)
    expect(readListState('empty', storage, [12, 24, 48], 12).pageSize).toBe(12)
  })

  it('ignores unreadable entries', () => {
    const storage = new MemoryStorage()
    storage.setItem('school-training:list:students', '{not json')
    expect(readListState('students', storage).page).toBe(1)
  })

  it('clears every list when the session ends', () => {
    const storage = new MemoryStorage()
    writeListState('students', { search: 'a', filters: {}, page: 1, pageSize: 20 }, storage)
    writeListState('teachers', { search: 'b', filters: {}, page: 1, pageSize: 20 }, storage)
    storage.setItem('school-training:navigation-collapsed', '{}')
    clearListStates(storage)
    expect(readListState('students', storage).search).toBe('')
    expect(readListState('teachers', storage).search).toBe('')
    expect(storage.getItem('school-training:navigation-collapsed')).toBe('{}')
  })
})

describe('list state persistence', () => {
  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
  })

  function browserStorage() {
    const storage = new MemoryStorage()
    vi.stubGlobal('sessionStorage', storage)
    return storage
  }

  function stored(storage: MemoryStorage, key: string) {
    const raw = storage.getItem(`school-training:list:${key}`)
    return raw ? JSON.parse(raw) : null
  }

  it('coalesces a burst of typing into one write', async () => {
    vi.useFakeTimers()
    const storage = browserStorage()
    const search = ref('')
    const scope = effectScope()
    scope.run(() => useListPersist('students', () => ({ search: search.value, filters: { classId: 'c1' } })))

    search.value = '张'
    await nextTick()
    search.value = '张三'
    await nextTick()
    search.value = '张三丰'
    await nextTick()
    expect(stored(storage, 'students')).toBeNull()

    vi.advanceTimersByTime(250)
    expect(stored(storage, 'students')).toEqual({ search: '张三丰', filters: { classId: 'c1' } })
    scope.stop()
  })

  it('flushes the last state when the page unmounts', () => {
    const storage = browserStorage()
    const scope = effectScope()
    scope.run(() => useListPersist('courses', () => ({ search: '网络', page: 2 })))
    expect(stored(storage, 'courses')).toBeNull()
    scope.stop()
    expect(stored(storage, 'courses')).toEqual({ search: '网络', page: 2 })
  })

  it('writes under the key that is current at flush time', () => {
    const storage = browserStorage()
    const key = ref('students')
    const scope = effectScope()
    scope.run(() =>
      useListPersist(
        () => key.value,
        () => ({ search: '李' })
      )
    )
    key.value = 'teachers'
    scope.stop()
    expect(stored(storage, 'classes')).toBeNull()
    expect(stored(storage, 'teachers')).toEqual({ search: '李' })
  })

  it('files a pending write under the list that produced it', async () => {
    vi.useFakeTimers()
    const storage = browserStorage()
    const key = ref('students')
    const search = ref('')
    const scope = effectScope()
    scope.run(() =>
      useListPersist(
        () => key.value,
        () => ({ search: search.value })
      )
    )

    search.value = '张三'
    await nextTick()
    // The operator switches entity tables before the debounce fires, and the incoming list
    // resets the same refs.
    key.value = 'teachers'
    search.value = ''
    await nextTick()

    vi.advanceTimersByTime(250)
    expect(stored(storage, 'students')).toEqual({ search: '张三' })
    expect(stored(storage, 'teachers')).toEqual({ search: '' })
    scope.stop()
  })
})

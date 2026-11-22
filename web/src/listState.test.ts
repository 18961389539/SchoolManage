import { describe, expect, it } from 'vitest'
import { clearListStates, readListState, writeListState } from './listState'

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

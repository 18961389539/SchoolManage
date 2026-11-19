import { describe, expect, it } from 'vitest'
import { normalizePageSize, recoverPage } from './paging'

describe('recoverPage', () => {
  it('jumps straight to the last page that still has rows', () => {
    expect(recoverPage(100, 20, 5, 0)).toBe(1)
    expect(recoverPage(4, 20, 45, 0)).toBe(3)
    expect(recoverPage(2, 20, 25, 0)).toBe(1)
  })

  it('does nothing when the page already has rows', () => {
    expect(recoverPage(2, 20, 25, 5)).toBeNull()
  })

  it('does nothing on the first page or when there is no data at all', () => {
    expect(recoverPage(1, 20, 25, 0)).toBeNull()
    expect(recoverPage(2, 20, 0, 0)).toBeNull()
  })

  it('respects a non-default page size', () => {
    expect(recoverPage(5, 50, 60, 0)).toBe(2)
  })
})

describe('normalizePageSize', () => {
  it('keeps supported sizes and falls back to the default', () => {
    expect(normalizePageSize(10)).toBe(10)
    expect(normalizePageSize(100)).toBe(100)
    expect(normalizePageSize(25)).toBe(20)
    expect(normalizePageSize(0)).toBe(20)
  })
})

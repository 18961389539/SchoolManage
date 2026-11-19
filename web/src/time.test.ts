import { describe, expect, it } from 'vitest'
import { dateTime, parseApiDate, shanghaiInputTime, shanghaiInputToIso } from './time'

describe('parseApiDate', () => {
  it('treats timestamps without a zone as UTC', () => {
    expect(parseApiDate('2026-11-19T08:30:00').toISOString()).toBe('2026-11-19T08:30:00.000Z')
    expect(parseApiDate('2026-11-19 08:30:00').toISOString()).toBe('2026-11-19T08:30:00.000Z')
  })

  it('keeps explicit offsets', () => {
    expect(parseApiDate('2026-11-19T08:30:00+08:00').toISOString()).toBe('2026-11-19T00:30:00.000Z')
    expect(parseApiDate('2026-11-19T08:30:00Z').toISOString()).toBe('2026-11-19T08:30:00.000Z')
  })

  it('returns Date instances unchanged', () => {
    const value = new Date('2026-11-19T08:30:00Z')
    expect(parseApiDate(value)).toBe(value)
  })
})

describe('dateTime', () => {
  it('renders Asia/Shanghai wall-clock time', () => {
    expect(dateTime('2026-11-19T00:30:00Z')).toBe('2026/11/19 08:30:00')
  })

  it('returns a dash for empty values and the raw text for invalid values', () => {
    expect(dateTime('')).toBe('—')
    expect(dateTime(null)).toBe('—')
    expect(dateTime('not-a-date')).toBe('not-a-date')
  })
})

describe('shanghaiInputTime', () => {
  it('formats for datetime-local inputs in Beijing time', () => {
    expect(shanghaiInputTime('2026-11-19T00:30:00Z')).toBe('2026-11-19T08:30:00')
    expect(shanghaiInputTime('2026-01-01T16:00:00Z')).toBe('2026-01-02T00:00:00')
  })

  it('returns an empty string for invalid input', () => {
    expect(shanghaiInputTime('')).toBe('')
    expect(shanghaiInputTime('nope')).toBe('')
  })
})

describe('shanghaiInputToIso', () => {
  it('converts wall-clock text to UTC', () => {
    expect(shanghaiInputToIso('2026-11-19T08:30')).toBe('2026-11-19T00:30:00.000Z')
    expect(shanghaiInputToIso('2026-11-19T08:30:00')).toBe('2026-11-19T00:30:00.000Z')
    expect(shanghaiInputToIso('2026-11-19T08:30:00.250')).toBe('2026-11-19T00:30:00.250Z')
  })

  it('rejects malformed values', () => {
    expect(shanghaiInputToIso('')).toBeNull()
    expect(shanghaiInputToIso('2026-11-19')).toBeNull()
    expect(shanghaiInputToIso('2026-13-01T00:00')).toBeNull()
  })

  it('round-trips through the input formatter', () => {
    const iso = shanghaiInputToIso('2026-01-01T00:00')
    expect(iso).not.toBeNull()
    expect(shanghaiInputTime(iso)).toBe('2026-01-01T00:00:00')
  })
})

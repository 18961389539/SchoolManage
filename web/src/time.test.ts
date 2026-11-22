import { describe, expect, it } from 'vitest'
import { dateTime, parseApiDate, rangeText, shanghaiInputTime, shanghaiInputToIso } from './time'

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

describe('rangeText', () => {
  it('shares the date for same-day ranges in Beijing time', () => {
    expect(rangeText('2026-11-19T01:00:00Z', '2026-11-19T04:00:00Z')).toBe('11/19 09:00–12:00')
  })

  it('keeps both dates for cross-day ranges', () => {
    expect(rangeText('2026-11-19T01:00:00Z', '2026-11-26T02:00:00Z')).toBe('11/19 09:00→11/26 10:00')
  })

  it('splits at the Beijing midnight boundary, not the UTC one', () => {
    expect(rangeText('2026-11-19T15:00:00Z', '2026-11-19T17:00:00Z')).toBe('11/19 23:00→11/20 01:00')
  })

  it('returns a dash when either end is missing', () => {
    expect(rangeText('', '2026-11-19T01:00:00Z')).toBe('—')
    expect(rangeText('2026-11-19T01:00:00Z', null)).toBe('—')
  })
})

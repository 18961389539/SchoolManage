import { describe, expect, it } from 'vitest'
import { useRequestGeneration } from './requestGeneration'

describe('useRequestGeneration', () => {
  it('accepts the newest request and rejects every older one', () => {
    const generation = useRequestGeneration()
    const first = generation.begin()
    const second = generation.begin()
    expect(generation.isCurrent(first)).toBe(false)
    expect(generation.isCurrent(second)).toBe(true)
  })
})

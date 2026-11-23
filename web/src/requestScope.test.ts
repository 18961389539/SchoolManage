import { describe, expect, it } from 'vitest'
import { cancelPageReads, pageLeft, trackPageRead } from './requestScope'

describe('page read scope', () => {
  it('aborts reads when the page is left, with a reason callers can recognise', () => {
    const controller = new AbortController()
    trackPageRead(controller)
    cancelPageReads()
    expect(controller.signal.aborted).toBe(true)
    expect(controller.signal.reason).toBe(pageLeft)
    expect((pageLeft as DOMException).name).toBe('PageLeftError')
  })

  it('forgets a finished read so the next navigation leaves it alone', () => {
    const controller = new AbortController()
    trackPageRead(controller)()
    cancelPageReads()
    expect(controller.signal.aborted).toBe(false)
  })
})

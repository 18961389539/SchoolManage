/**
 * Reads belong to the page that started them. When the operator navigates, a slow answer from
 * the previous screen is cancelled instead of arriving late in the next one.
 *
 * Writes are deliberately never registered here: leaving a page must not abort a save, and an
 * exam answer that is already on its way to the server has to land.
 */
const pending = new Set<{ controller: AbortController }>()

/**
 * Distinct from a caller-cancelled request. Nobody is waiting for this answer any more, so the
 * transport settles nothing and no catch block on the abandoned page can paint an error.
 */
export const pageLeft = new DOMException('页面已切换', 'PageLeftError')

export function trackPageRead(controller: AbortController): () => void {
  const entry = { controller }
  pending.add(entry)
  return () => {
    pending.delete(entry)
  }
}

export function cancelPageReads(): void {
  for (const entry of pending) {
    pending.delete(entry)
    entry.controller.abort(pageLeft)
  }
}

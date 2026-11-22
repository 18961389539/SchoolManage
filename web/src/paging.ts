/**
 * Pagination recovery for list pages. When the current page becomes empty after
 * deletions or filtering, step back directly to the last page that still has data
 * instead of walking back one page per request.
 */
export function recoverPage(page: number, pageSize: number, total: number, receivedRows: number): number | null {
  if (receivedRows > 0 || page <= 1 || total <= 0) return null
  const size = Math.max(1, pageSize)
  const lastPage = Math.max(1, Math.ceil(total / size))
  return Math.min(page - 1, lastPage)
}

export function normalizePageSize(
  value: number,
  allowed: number[] = [10, 20, 50, 100],
  fallback: number = allowed[1] ?? 20
): number {
  return allowed.includes(value) ? value : fallback
}

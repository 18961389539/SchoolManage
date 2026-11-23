import { ElMessageBox } from 'element-plus'
import { ApiError } from './api'

/**
 * A 409 alone is ambiguous: duplicate names and stale revisions both use it. Only the server's
 * `stale` marker means the copy on screen is behind someone else's save.
 */
export function isStale(error: unknown): boolean {
  if (!(error instanceof ApiError) || error.status !== 409) return false
  return (error.details as { code?: string } | undefined)?.code === 'stale'
}

/** True when the operator accepts dropping local input in exchange for the server copy. */
export async function confirmReload(
  subject: string,
  detail = '这条内容在你编辑期间已被其他人保存。重新加载会放弃你当前输入的内容。'
): Promise<boolean> {
  try {
    await ElMessageBox.confirm(detail, `${subject}已被其他人修改`, {
      type: 'warning',
      confirmButtonText: '重新加载',
      cancelButtonText: '保留我的修改'
    })
    return true
  } catch {
    return false
  }
}

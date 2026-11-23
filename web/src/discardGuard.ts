import { ElMessageBox } from 'element-plus'
import { computed, ref, type ComputedRef, type Ref } from 'vue'

export interface DiscardGuardOptions {
  /** The dialog's open state; when given, `cancel()` closes it after the check. */
  visible?: Ref<boolean>
  /** Snapshot of the editable values; compared by value, so nested arrays are covered. */
  snapshot: () => unknown
  /** What the operator is about to lose, e.g. 「课程信息还没有保存，关闭后不会保留。」 */
  message: string
  /** Keep the dialog open while a save is on the wire. */
  busy?: () => boolean
}

export interface DiscardGuard {
  dirty: ComputedRef<boolean>
  /** Remember the form as saved. Call after the dialog has loaded, never after a failure. */
  capture: () => void
  /** Forget the baseline; the guard then considers the dialog clean. */
  reset: () => void
  /** True when it is safe to close: clean form, or the operator chose to discard. */
  canClose: () => Promise<boolean>
  /** Ready-made `:before-close` handler. */
  beforeClose: (done: () => void) => void
  /** Ready-made 取消 button handler; closes `visible` when allowed. */
  cancel: () => Promise<void>
}

/**
 * One answer for every editable dialog: capture the loaded form once, ask before throwing
 * away changes, and never let a save in flight be cut off by a stray click.
 */
export function useDiscardGuard(options: DiscardGuardOptions): DiscardGuard {
  const baseline = ref<string | null>(null)
  const serialize = () => JSON.stringify(options.snapshot())
  const dirty = computed(() => baseline.value !== null && serialize() !== baseline.value)

  function capture() {
    baseline.value = serialize()
  }

  function reset() {
    baseline.value = null
  }

  async function canClose(): Promise<boolean> {
    // Saving is not something to interrupt; loading is, because there is nothing to lose yet.
    if (options.busy?.()) return false
    if (!dirty.value) return true
    try {
      await ElMessageBox.confirm(options.message, '放弃未保存的修改？', {
        type: 'warning',
        confirmButtonText: '放弃修改',
        cancelButtonText: '继续编辑'
      })
      return true
    } catch {
      return false
    }
  }

  function beforeClose(done: () => void) {
    void canClose().then(close => {
      if (close) done()
    })
  }

  async function cancel() {
    if (options.visible && (await canClose())) options.visible.value = false
  }

  return { dirty, capture, reset, canClose, beforeClose, cancel }
}

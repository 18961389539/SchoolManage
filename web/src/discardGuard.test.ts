import { beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick, reactive, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { useDiscardGuard } from './discardGuard'

vi.mock('element-plus', () => ({ ElMessageBox: { confirm: vi.fn() } }))

const confirm = vi.mocked(ElMessageBox.confirm)

beforeEach(() => {
  confirm.mockReset()
})

function guardWith(form: Record<string, unknown>, busy?: () => boolean) {
  return useDiscardGuard({ snapshot: () => form, message: '表单还没有保存。', busy })
}

describe('discard guard', () => {
  it('stays clean until a baseline is captured', () => {
    const form = reactive({ name: '原值' })
    const guard = guardWith(form)
    expect(guard.dirty.value).toBe(false)
    guard.capture()
    expect(guard.dirty.value).toBe(false)
    form.name = '改过'
    expect(guard.dirty.value).toBe(true)
  })

  it('closes a clean dialog without asking', async () => {
    const guard = guardWith(reactive({ name: '原值' }))
    guard.capture()
    await expect(guard.canClose()).resolves.toBe(true)
    expect(confirm).not.toHaveBeenCalled()
  })

  it('asks before discarding changes and honours the answer', async () => {
    const form = reactive({ name: '原值' })
    const guard = guardWith(form)
    guard.capture()
    form.name = '改过'

    confirm.mockResolvedValueOnce('confirm' as never)
    await expect(guard.canClose()).resolves.toBe(true)

    confirm.mockRejectedValueOnce('cancel')
    await expect(guard.canClose()).resolves.toBe(false)
  })

  it('will not close while a save is in flight, and does not nag about it', async () => {
    const form = reactive({ name: '原值' })
    let saving = true
    const guard = guardWith(form, () => saving)
    guard.capture()
    form.name = '改过'

    await expect(guard.canClose()).resolves.toBe(false)
    expect(confirm).not.toHaveBeenCalled()

    saving = false
    confirm.mockResolvedValueOnce('confirm' as never)
    await expect(guard.canClose()).resolves.toBe(true)
  })

  it('runs the close only when the operator agrees', async () => {
    const form = reactive({ name: '原值' })
    const guard = guardWith(form)
    guard.capture()
    form.name = '改过'

    const done = vi.fn()
    confirm.mockRejectedValueOnce('cancel')
    guard.beforeClose(done)
    await nextTick()
    await Promise.resolve()
    expect(done).not.toHaveBeenCalled()

    confirm.mockResolvedValueOnce('confirm' as never)
    guard.beforeClose(done)
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(done).toHaveBeenCalledTimes(1)
  })

  it('forgets the baseline when the form is reset', () => {
    const form = reactive({ name: '原值' })
    const guard = guardWith(form)
    guard.capture()
    form.name = '改过'
    guard.reset()
    expect(guard.dirty.value).toBe(false)
  })

  it('closes the dialog from the cancel button only when allowed', async () => {
    const form = reactive({ name: '原值' })
    const visible = ref(true)
    const guard = useDiscardGuard({ visible, snapshot: () => form, message: '表单还没有保存。' })
    guard.capture()
    form.name = '改过'

    confirm.mockRejectedValueOnce('cancel')
    await guard.cancel()
    expect(visible.value).toBe(true)

    confirm.mockResolvedValueOnce('confirm' as never)
    await guard.cancel()
    expect(visible.value).toBe(false)
  })

  it('treats a reordered snapshot as unchanged only when the values match', () => {
    const form = reactive({ options: ['A', 'B'] })
    const guard = guardWith(form)
    guard.capture()
    form.options.push('C')
    expect(guard.dirty.value).toBe(true)
    form.options.pop()
    expect(guard.dirty.value).toBe(false)
  })
})

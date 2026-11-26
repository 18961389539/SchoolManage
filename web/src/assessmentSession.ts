import { onBeforeUnmount, onMounted, ref, type Ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { messageOf, post } from './api'
import { clearDraft, readDraft, registerEmergencyFlush, writeDraft } from './draftPersistence'
import { registerLeaveGuard } from './leaveGuard'
import { parseApiDate } from './time'

export type AssessmentSavePhase = 'idle' | 'pending' | 'saving' | 'saved' | 'error'

export interface AssessmentLeaveCopy {
  title: string
  unsaved: string
  saved: string
  confirmUnsaved: string
  confirmSaved: string
  cancel: string
}

export interface AssessmentLabels {
  idle: string
  pending: string
  saving: string
  error: string
  saved: (at: Date | null) => string
}

export interface AssessmentSessionOptions<T> {
  draftKey: () => string
  active: () => boolean
  deadline: () => string | null | undefined
  submitting: () => boolean
  disposed?: () => boolean
  /** Full answer list stored in the emergency draft and ignored by the incremental save. */
  collectDraft: () => T[]
  /** Apply one restored answer. True when the local value changed. */
  restoreAnswer: (answer: T) => boolean
  changedIds: () => string[]
  fingerprint: (id: string) => string
  remember: (id: string, fingerprint: string) => void
  snapshot: (ids: string[]) => unknown
  /** Drop ids that were just copied into the request, so edits made while it is in flight stay dirty. */
  detachSent?: (ids: string[]) => void
  /** Put those ids back when the request fails. */
  redirty?: (ids: string[]) => void
  savePath: () => string
  debounceMs?: number
  /** While the attempt is open, post again on this cadence if something is still dirty. */
  intervalMs?: number
  /** `immediate` posts a follow-up as soon as the current one finishes. `debounce` waits for another quiet period. */
  queue?: 'debounce' | 'immediate'
  scheduleOnRestore?: boolean
  flushOnUnmount?: boolean
  draftOnUnmount?: boolean
  leaveWaits?: boolean
  /** Exam still treats a leftover local edit as unsaved after the flush. Camp only does that when the save failed. */
  treatDirtyAsUnsaved?: boolean
  blockLeave?: () => boolean
  ignoreFailure?: () => boolean
  onConflict?: (failure: unknown) => Promise<void> | void
  onSecond?: (remaining: number) => void
  restoreNotice: string
  announceSaved?: string
  leave: AssessmentLeaveCopy
  labels: AssessmentLabels
}

export interface AssessmentSession {
  remaining: Ref<number>
  phase: Ref<AssessmentSavePhase>
  text: Ref<string>
  saving: Ref<boolean>
  scheduled: Ref<boolean>
  lastSavedAt: Ref<Date | null>
  loadedAt: () => string
  noteServerNow: (serverNow: string) => void
  schedule: (delayMs?: number) => void
  flush: (announce?: boolean) => Promise<void>
  flushPending: () => Promise<void>
  clearSchedule: () => void
  reset: () => void
  restoreDraft: () => boolean
  clearStoredDraft: () => void
  confirmLeave: () => Promise<boolean>
  startClock: () => void
  stopClock: () => void
  attach: () => () => void
}

/**
 * Clock, incremental autosave, emergency draft and leave confirmation shared by the exam
 * and the camp attempt. Each page still owns its answer objects and the words on screen.
 */
export function createAssessmentSession<T>(options: AssessmentSessionOptions<T>): AssessmentSession {
  const remaining = ref(0)
  const phase = ref<AssessmentSavePhase>('idle')
  const saving = ref(false)
  const scheduled = ref(false)
  const lastSavedAt = ref<Date | null>(null)
  const text = ref(options.labels.idle)
  const debounceMs = options.debounceMs ?? 800
  const queue = options.queue ?? 'debounce'
  let baseline = ''
  let serverOffset = 0
  let restorePending = true
  let leaveApproved = false
  let debounce: ReturnType<typeof setTimeout> | undefined
  let clock: ReturnType<typeof setInterval> | undefined
  let autosave: ReturnType<typeof setInterval> | undefined
  let inflight: Promise<void> | undefined
  let queued = false

  function syncText() {
    if (phase.value === 'saving') text.value = options.labels.saving
    else if (phase.value === 'pending') text.value = options.labels.pending
    else if (phase.value === 'error') text.value = options.labels.error
    else if (phase.value === 'saved') text.value = options.labels.saved(lastSavedAt.value)
    else text.value = options.labels.idle
  }

  function syncRemaining() {
    const deadline = options.deadline()
    if (!options.active() || !deadline) {
      remaining.value = 0
      return
    }
    remaining.value = Math.max(0, Math.ceil((parseApiDate(deadline).getTime() - Date.now() - serverOffset) / 1000))
  }

  function clearDebounce() {
    if (debounce === undefined) return
    clearTimeout(debounce)
    debounce = undefined
  }

  function clearSchedule() {
    scheduled.value = false
    clearDebounce()
  }

  function schedule(delayMs = debounceMs) {
    if (!options.active()) return
    scheduled.value = true
    if (!saving.value) phase.value = 'pending'
    syncText()
    clearDebounce()
    debounce = setTimeout(() => {
      debounce = undefined
      void flush()
    }, delayMs)
  }

  async function flush(announce = false): Promise<void> {
    clearDebounce()
    if (!options.active() || options.disposed?.()) return inflight
    if (saving.value) {
      if (queue === 'immediate') queued = true
      return inflight
    }
    const ids = options.changedIds()
    if (ids.length === 0) {
      scheduled.value = false
      if (announce) {
        phase.value = 'saved'
        lastSavedAt.value = new Date()
        syncText()
        if (options.announceSaved) ElMessage.success(options.announceSaved)
      }
      return
    }
    const sent = new Map(ids.map(id => [id, options.fingerprint(id)]))
    const answers = options.snapshot(ids)
    options.detachSent?.(ids)
    scheduled.value = false
    saving.value = true
    phase.value = 'saving'
    syncText()
    const run = (async () => {
      try {
        const result = await post<{ serverNow: string }>(options.savePath(), { answers, loadedAt: baseline })
        if (options.disposed?.()) return
        for (const [id, value] of sent) {
          if (options.fingerprint(id) === value) options.remember(id, value)
        }
        baseline = result.serverNow
        const stillDirty = options.changedIds().length > 0
        if (!stillDirty) clearDraft(options.draftKey())
        phase.value = stillDirty ? 'pending' : 'saved'
        if (!stillDirty) lastSavedAt.value = new Date()
        syncText()
        if (announce && !stillDirty && options.announceSaved) ElMessage.success(options.announceSaved)
      } catch (failure) {
        if (options.ignoreFailure?.()) return
        options.redirty?.(ids)
        phase.value = 'error'
        syncText()
        if (announce) ElMessage.error(messageOf(failure))
        const status = (failure as { status?: number })?.status
        if (status === 409 && options.onConflict && !options.submitting()) await options.onConflict(failure)
      } finally {
        saving.value = false
        inflight = undefined
        if (queue === 'immediate' && (queued || scheduled.value) && options.active() && !options.disposed?.()) {
          queued = false
          void flush()
        } else if (
          queue === 'debounce' &&
          phase.value !== 'error' &&
          options.changedIds().length > 0 &&
          options.active() &&
          !options.submitting() &&
          !options.disposed?.()
        ) {
          schedule()
        }
      }
    })()
    inflight = run
    return run
  }

  async function flushPending() {
    if (inflight) await inflight
    if (options.changedIds().length > 0) await flush()
  }

  function noteServerNow(serverNow: string) {
    serverOffset = parseApiDate(serverNow).getTime() - Date.now()
    baseline = serverNow
    syncRemaining()
  }

  function reset() {
    clearSchedule()
    phase.value = 'idle'
    syncText()
  }

  function restoreDraft() {
    if (!restorePending) return false
    restorePending = false
    if (!options.active()) return false
    const saved = readDraft<{ answers: T[] }>(options.draftKey())
    if (!saved?.answers?.length) return false
    let restored = false
    for (const answer of saved.answers) {
      if (options.restoreAnswer(answer)) restored = true
    }
    if (restored) {
      phase.value = 'pending'
      syncText()
      ElMessage.warning(options.restoreNotice)
      if (options.scheduleOnRestore) schedule()
    }
    return restored
  }

  function clearStoredDraft() {
    clearDraft(options.draftKey())
  }

  async function confirmLeave() {
    if (leaveApproved || !options.active()) return true
    if (options.blockLeave?.()) return false
    if (options.leaveWaits) await flushPending()
    else await flush()
    const unsaved = phase.value === 'error' || (options.treatDirtyAsUnsaved === true && options.changedIds().length > 0)
    try {
      await ElMessageBox.confirm(unsaved ? options.leave.unsaved : options.leave.saved, options.leave.title, {
        type: 'warning',
        confirmButtonText: unsaved ? options.leave.confirmUnsaved : options.leave.confirmSaved,
        cancelButtonText: options.leave.cancel
      })
    } catch {
      return false
    }
    leaveApproved = true
    return true
  }

  function startClock() {
    stopClock()
    syncRemaining()
    clock = setInterval(() => {
      syncRemaining()
      if (options.active()) options.onSecond?.(remaining.value)
    }, 1000)
    if ((options.intervalMs ?? 0) > 0) {
      autosave = setInterval(() => {
        if (saving.value || options.submitting() || !options.active() || options.changedIds().length === 0) return
        void flush()
      }, options.intervalMs)
    }
  }

  function stopClock() {
    if (clock !== undefined) clearInterval(clock)
    if (autosave !== undefined) clearInterval(autosave)
    clock = undefined
    autosave = undefined
  }

  function attach() {
    const releaseFlush = registerEmergencyFlush(() => {
      if (!options.active()) return
      writeDraft(options.draftKey(), { answers: options.collectDraft() })
    })
    const releaseGuard = registerLeaveGuard(() => confirmLeave())
    const onUnload = (event: BeforeUnloadEvent) => {
      if (!options.active()) return
      writeDraft(options.draftKey(), { answers: options.collectDraft() })
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', onUnload)
    return () => {
      releaseFlush()
      releaseGuard()
      window.removeEventListener('beforeunload', onUnload)
    }
  }

  return {
    remaining,
    phase,
    text,
    saving,
    scheduled,
    lastSavedAt,
    loadedAt: () => baseline,
    noteServerNow,
    schedule,
    flush,
    flushPending,
    clearSchedule,
    reset,
    restoreDraft,
    clearStoredDraft,
    confirmLeave,
    startClock,
    stopClock,
    attach
  }
}

export function useAssessmentSession<T>(options: AssessmentSessionOptions<T>) {
  const session = createAssessmentSession(options)
  const detach = session.attach()
  onMounted(() => session.startClock())
  onBeforeUnmount(() => {
    if (options.draftOnUnmount && options.active()) writeDraft(options.draftKey(), { answers: options.collectDraft() })
    if (options.flushOnUnmount && options.active() && !options.submitting()) void session.flush()
    detach()
    session.stopClock()
    session.clearSchedule()
  })
  return session
}

export function focusQuestion(elementId: string) {
  const target = document.getElementById(elementId)
  if (!target) return
  const behavior = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'
  target.focus({ preventScroll: true })
  target.scrollIntoView({ behavior, block: 'start' })
}

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ElMessage, ElMessageBox } from 'element-plus'
import { post } from './api'
import { createAssessmentSession, type AssessmentSessionOptions } from './assessmentSession'

vi.mock('./api', () => ({
  post: vi.fn(),
  messageOf: (error: unknown) => (error instanceof Error ? error.message : '失败')
}))

vi.mock('element-plus', () => ({
  ElMessage: { success: vi.fn(), warning: vi.fn(), error: vi.fn() },
  ElMessageBox: { confirm: vi.fn() }
}))

const send = vi.mocked(post)

interface Answer {
  id: string
  selected: number[]
  text: string
}

function harness() {
  const answers = new Map<string, Answer>([
    ['a', { id: 'a', selected: [], text: '' }],
    ['b', { id: 'b', selected: [], text: '' }]
  ])
  const saved = new Map<string, string>([
    ['a', fingerprint('a')],
    ['b', fingerprint('b')]
  ])
  const active = { value: true }
  const submitting = { value: false }
  let deadline = new Date(Date.now() + 90_000).toISOString()
  const posts: unknown[] = []
  send.mockImplementation(async (_path, body) => {
    posts.push(body)
    return { serverNow: '2026-11-26T00:00:10.000Z' }
  })

  function fingerprint(id: string) {
    const answer = answers.get(id)
    return JSON.stringify({ selected: answer?.selected || [], text: answer?.text || '' })
  }

  const options: AssessmentSessionOptions<Answer> = {
    draftKey: () => 'draft',
    active: () => active.value,
    deadline: () => deadline,
    submitting: () => submitting.value,
    collectDraft: () => [...answers.values()],
    restoreAnswer: answer => {
      const current = answers.get(answer.id)
      if (!current || !answer.selected.length) return false
      current.selected = [...answer.selected]
      return true
    },
    changedIds: () => [...answers.keys()].filter(id => saved.get(id) !== fingerprint(id)),
    fingerprint,
    remember: (id, value) => saved.set(id, value),
    snapshot: ids => ids.map(id => ({ ...answers.get(id) })),
    savePath: () => '/answers',
    debounceMs: 800,
    queue: 'debounce',
    restoreNotice: '已恢复',
    leave: {
      title: '离开',
      unsaved: '未保存',
      saved: '已保存',
      confirmUnsaved: '离开',
      confirmSaved: '离开',
      cancel: '留下'
    },
    labels: {
      idle: '空闲',
      pending: '待保存',
      saving: '保存中',
      error: '失败',
      saved: () => '已保存'
    }
  }
  const session = createAssessmentSession(options)
  return { answers, saved, active, submitting, posts, session, options, setDeadline: (value: string) => { deadline = value } }
}

afterEach(() => {
  vi.useRealTimers()
})

beforeEach(() => {
  send.mockReset()
  vi.mocked(ElMessage.success).mockReset()
  vi.mocked(ElMessage.warning).mockReset()
  vi.mocked(ElMessageBox.confirm).mockReset()
  const store = new Map<string, string>()
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => store.get(key) ?? null,
    setItem: (key: string, value: string) => store.set(key, value),
    removeItem: (key: string) => store.delete(key)
  })
})

describe('assessment session', () => {
  it('posts only the answers that changed after the debounce', async () => {
    vi.useFakeTimers()
    const { answers, posts, session } = harness()
    answers.get('a')!.selected = [1]
    session.noteServerNow('2026-11-26T00:00:00.000Z')
    session.schedule()
    expect(session.phase.value).toBe('pending')
    await vi.advanceTimersByTimeAsync(799)
    expect(send).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(1)
    expect(posts).toEqual([
      {
        answers: [{ id: 'a', selected: [1], text: '' }],
        loadedAt: '2026-11-26T00:00:00.000Z'
      }
    ])
    expect(session.phase.value).toBe('saved')
    vi.useRealTimers()
  })

  it('counts down from the server clock', () => {
    const { session } = harness()
    session.noteServerNow(new Date().toISOString())
    expect(session.remaining.value).toBeGreaterThan(80)
    expect(session.remaining.value).toBeLessThanOrEqual(90)
  })

  it('keeps an edit made during the request for a later save', async () => {
    vi.useFakeTimers()
    let release: (value: { serverNow: string }) => void = () => {}
    send.mockImplementation(
      () =>
        new Promise(resolve => {
          release = resolve
        })
    )
    const { answers, session } = harness()
    answers.get('a')!.selected = [0]
    session.noteServerNow('2026-11-26T00:00:00.000Z')
    const first = session.flush()
    answers.get('b')!.selected = [1]
    session.schedule()
    release({ serverNow: '2026-11-26T00:00:01.000Z' })
    await first
    expect(send).toHaveBeenCalledTimes(1)
    expect(session.phase.value).toBe('pending')
    await vi.advanceTimersByTimeAsync(800)
    expect(send).toHaveBeenCalledTimes(2)
    const second = send.mock.calls[1]?.[1] as { answers: Answer[] }
    expect(second.answers.map(item => item.id)).toEqual(['b'])
    vi.useRealTimers()
  })

  it('asks the page to reload when the save is stale', async () => {
    const conflict = vi.fn()
    const { answers, session, options } = harness()
    options.onConflict = conflict
    send.mockRejectedValue(Object.assign(new Error('别人先保存了'), { status: 409 }))
    answers.get('a')!.selected = [1]
    session.noteServerNow('2026-11-26T00:00:00.000Z')
    await session.flush()
    expect(session.phase.value).toBe('error')
    expect(conflict).toHaveBeenCalledOnce()
  })

  it('restores a local draft without posting it immediately', () => {
    localStorage.setItem('draft', JSON.stringify({ answers: [{ id: 'a', selected: [1], text: '' }] }))
    const { answers, session } = harness()
    expect(session.restoreDraft()).toBe(true)
    expect(answers.get('a')?.selected).toEqual([1])
    expect(session.phase.value).toBe('pending')
    expect(ElMessage.warning).toHaveBeenCalledWith('已恢复')
    expect(send).not.toHaveBeenCalled()
  })
})

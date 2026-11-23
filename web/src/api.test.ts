import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, bytes, downloadFile, listItems, messageOf, passwordIssue } from './api'
import { cancelPageReads } from './requestScope'

const policyMessage = '密码须为 12–128 位，且同时包含字母和数字'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

function stubBrowser() {
  const anchor = { href: '', download: '', click: vi.fn(), remove: vi.fn() }
  const append = vi.fn()
  vi.stubGlobal('document', { createElement: vi.fn(() => anchor), body: { append } })
  vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:test')
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {})
  return { anchor, append }
}

describe('passwordIssue', () => {
  it('accepts 12–128 characters containing letters and digits', () => {
    expect(passwordIssue('abcd12345678')).toBe('')
    expect(passwordIssue('A1b2C3d4E5f6G7h8')).toBe('')
  })

  it('rejects values outside the policy', () => {
    expect(passwordIssue('')).toBe(policyMessage)
    expect(passwordIssue('abcd1234567')).toBe(policyMessage)
    expect(passwordIssue('abcdefghijkl')).toBe(policyMessage)
    expect(passwordIssue('123456789012')).toBe(policyMessage)
    expect(passwordIssue('a'.repeat(128) + '1')).toBe(policyMessage)
  })
})

describe('listItems', () => {
  it('unwraps paged responses and plain arrays', () => {
    expect(listItems([{ id: 1 }])).toEqual([{ id: 1 }])
    expect(listItems({ total: 2, items: [{ id: 1 }, { id: 2 }] })).toHaveLength(2)
  })

  it('returns an empty array for unexpected payloads', () => {
    expect(listItems(null)).toEqual([])
    expect(listItems({ total: 0 })).toEqual([])
    expect(listItems('nope')).toEqual([])
  })
})

describe('bytes', () => {
  it('formats byte sizes with binary units', () => {
    expect(bytes(0)).toBe('0 B')
    expect(bytes(1023)).toBe('1023 B')
    expect(bytes(1024)).toBe('1.0 KB')
    expect(bytes(1536)).toBe('1.5 KB')
    expect(bytes(1024 ** 3)).toBe('1.0 GB')
    expect(bytes('1024')).toBe('1.0 KB')
  })

  it('returns a dash for non-numeric values', () => {
    expect(bytes('abc')).toBe('—')
    expect(bytes(undefined)).toBe('—')
  })
})

describe('messageOf', () => {
  it('uses Error messages and falls back for unknown values', () => {
    expect(messageOf(new Error('boom'))).toBe('boom')
    expect(messageOf(new ApiError('服务不可用', 503))).toBe('服务不可用')
    expect(messageOf('nope')).toBe('操作失败，请稍后重试')
  })
})

describe('downloadFile', () => {
  it('saves the response under the given name and cleans up the object URL', async () => {
    const { anchor, append } = stubBrowser()
    const fetchMock = vi.fn(async () => new Response(new Blob(['data']), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    await downloadFile('/exports/1', '统计.csv')

    expect(fetchMock).toHaveBeenCalledWith('/api/exports/1', expect.objectContaining({ credentials: 'same-origin' }))
    expect(anchor.download).toBe('统计.csv')
    expect(anchor.click).toHaveBeenCalled()
    expect(append).toHaveBeenCalledWith(anchor)
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:test')
  })

  it('reuses the shared error payload and trace id', async () => {
    stubBrowser()
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ message: '导出失败', traceId: 't1' }), {
            status: 500,
            headers: { 'Content-Type': 'application/json' }
          })
      )
    )

    await expect(downloadFile('/exports/1', 'x.csv')).rejects.toThrow('导出失败（追踪号 t1）')
  })

  it('keeps running when the operator leaves the page', async () => {
    stubBrowser()
    let signal: AbortSignal | undefined
    vi.stubGlobal(
      'fetch',
      vi.fn((_url: RequestInfo | URL, init?: RequestInit) => {
        signal = init?.signal ?? undefined
        return new Promise<Response>(resolve =>
          setTimeout(() => resolve(new Response(new Blob(['x']), { status: 200 })), 10)
        )
      })
    )

    const pending = downloadFile('/exports/1', 'x.csv')
    cancelPageReads()
    await pending

    expect(signal?.aborted).toBe(false)
  })
})

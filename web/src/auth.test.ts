import { describe, expect, it } from 'vitest'
import { createAuthForm } from './auth'

describe('createAuthForm', () => {
  it('starts with the administrator account prefilled', () => {
    expect(createAuthForm()).toEqual({
      schoolName: '',
      userName: 'admin',
      displayName: '平台管理员',
      password: '',
      confirmation: ''
    })
  })

  it('returns an independent object each time', () => {
    const first = createAuthForm()
    first.userName = 'changed'
    expect(createAuthForm().userName).toBe('admin')
  })
})

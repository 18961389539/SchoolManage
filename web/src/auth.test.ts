import { describe, expect, it } from 'vitest'
import { createAuthForm } from './auth'
import { findNavGroup, findNavItem, navigation } from './navigation'

describe('navigation', () => {
  it('exposes a unique key for every entry', () => {
    const keys = navigation.flatMap(group => group.items.map(item => item.key))
    expect(new Set(keys).size).toBe(keys.length)
  })

  it('finds items and their groups', () => {
    expect(findNavItem('devices')?.label).toBe('实训设备')
    expect(findNavGroup('devices')?.label).toBe('实训资源')
    expect(findNavItem('missing')).toBeUndefined()
    expect(findNavGroup('missing')).toBeUndefined()
  })
})

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

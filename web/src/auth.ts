export interface AuthForm {
  schoolName: string
  userName: string
  displayName: string
  password: string
  confirmation: string
}

export function createAuthForm(): AuthForm {
  return { schoolName: '', userName: 'admin', displayName: '平台管理员', password: '', confirmation: '' }
}

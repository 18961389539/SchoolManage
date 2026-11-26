// Generated from the OpenAPI document "contract". Do not edit.
// Regenerate with: npm run check:api -- --write

export const contractOperations = [
  "GET /api/auth/me",
  "GET /api/exam-questions",
  "GET /api/exam-questions/{id}",
  "GET /api/status",
  "POST /api/auth/login",
  "POST /api/auth/password",
  "POST /api/setup"
] as const

export interface CaseRubricItem {
  deviceId: string
  expected: string
  field: string
  guidance?: string | null
  interfaceName: string | null
  name: string
  score: number
}

export interface ExamQuestionDetailResponse {
  analysis: string
  answers: number[]
  automaticPractical: boolean
  bankId: string
  bankName: string
  createdAt: string
  id: string
  options: string[]
  papers: ExamQuestionPaperRef[]
  practicalNodeId: string
  practicalResources: string
  practicalRubric: CaseRubricItem[]
  score: number
  stem: string
  type: string
  updatedAt: string
}

export interface ExamQuestionListResponse {
  items: ExamQuestionSummaryResponse[]
  page: number
  pageSize: number
  total: number
}

export interface ExamQuestionPaperRef {
  id: string
  name: string
}

export interface ExamQuestionSummaryResponse {
  analysis: string
  answers: number[]
  automaticPractical: boolean
  bankId: string
  bankName: string
  createdAt: string
  id: string
  options: string[]
  paperCount: number
  score: number
  stem: string
  type: string
  updatedAt: string
}

export interface LoginRequest {
  password: string
  userName: string
}

export interface MessageResponse {
  message: string
}

export interface PasswordRequest {
  currentPassword: string
  newPassword: string
}

export interface SessionResponse {
  csrfToken: string
  user: SessionUserResponse
}

export interface SessionUserResponse {
  displayName: string
  id: string
  mustChangePassword: boolean
  role: string
  userName: string
}

export interface SetupRequest {
  displayName: string
  password: string
  schoolName: string
  userName: string
}

export interface StatusDemoAccount {
  displayName: string
  role: string
  userName: string
}

export interface StatusResponse {
  demo: boolean
  demoAccounts: StatusDemoAccount[] | null
  demoPassword: string | null
  initialized: boolean
  logWriteFailures: number
  maxUploadBytes: number
  product: string
  schemaVersion: number
  schoolName: string | null
  version: string
}

import { describe, expect, it } from 'vitest'
import { contractOperations, type ExamQuestionDetailResponse, type ExamQuestionSummaryResponse } from './api.generated'
import type { ExamQuestionDetail, ExamQuestionSummary } from './types'

type Missing<Hand, Generated> = Exclude<keyof Generated, keyof Hand>
type Extra<Hand, Generated> = Exclude<keyof Hand, keyof Generated>
type Incompatible<Hand, Generated> = {
  [K in keyof Hand & keyof Generated]: Hand[K] extends Generated[K] ? never : K
}[keyof Hand & keyof Generated]
type SummaryKeys = Exclude<keyof ExamQuestionSummaryResponse, 'paperCount'>

const summaryShape: [
  Missing<ExamQuestionSummary, ExamQuestionSummaryResponse>,
  Extra<ExamQuestionSummary, ExamQuestionSummaryResponse>,
  Incompatible<ExamQuestionSummary, ExamQuestionSummaryResponse>
] extends [never, never, never]
  ? true
  : never = true
const detailShape: [
  Missing<ExamQuestionDetail, ExamQuestionDetailResponse>,
  Extra<ExamQuestionDetail, ExamQuestionDetailResponse>,
  Incompatible<ExamQuestionDetail, ExamQuestionDetailResponse>
] extends [never, never, never]
  ? true
  : never = true
const detailKeepsSummary: SummaryKeys extends keyof ExamQuestionDetail ? true : never = true

describe('generated API contract', () => {
  it('covers session, status, and exam-question reads', () => {
    expect(summaryShape && detailShape && detailKeepsSummary).toBe(true)
    expect(contractOperations).toEqual([
      'GET /api/auth/me',
      'GET /api/exam-questions',
      'GET /api/exam-questions/{id}',
      'GET /api/status',
      'POST /api/auth/login',
      'POST /api/auth/password',
      'POST /api/setup'
    ])
  })
})

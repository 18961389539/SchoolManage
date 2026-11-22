import type { ExamQuestionBankType, ExamQuestionType } from './types'

export const examQuestionBankLabels: Record<ExamQuestionBankType, string> = {
  theory: '理论题库',
  practical: '实操题库',
  mixed: '综合题库'
}

export function bankAccepts(bankType: ExamQuestionBankType, questionType: ExamQuestionType): boolean {
  if (bankType === 'mixed') return true
  return bankType === 'practical' ? questionType === 'practical' : questionType !== 'practical'
}

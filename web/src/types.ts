// Explicit request/response contracts mirrored from the ASP.NET Core endpoints.
// Dynamic entity forms and tables still use Row (Record<string, any>) because their
// fields are configured at runtime; everything with a stable shape belongs here.
import type { CaseGrade, CaseRubricItem, CaseTopologyBlueprint } from './caseWork'

export interface SessionUser {
  id: string
  userName: string
  displayName: string
  role: string
  mustChangePassword: boolean
}

export interface SessionResponse {
  user: SessionUser
  csrfToken: string
}

export interface StatusResponse {
  initialized: boolean
  product: string
  version: string
  schemaVersion: number
  logWriteFailures: number
  maxUploadBytes: number
  schoolName?: string | null
}

export interface Paged<T> {
  total: number
  items: T[]
}

export interface MessageResponse {
  message: string
}

export interface AuditEntry {
  id: string
  at: string
  userName: string
  action: string
  target: string
  detail: string
}

export interface DashboardCounts {
  professions: number
  classes: number
  teachers: number
  students: number
  cabinets: number
  devices: number
  nodes: number
}

export interface DashboardResponse {
  schoolName: string
  counts: DashboardCounts
  recentAudits: AuditEntry[]
  server: { machineName: string; os: string; framework: string }
  environment: string
  version: string
}

export interface BootstrapResult {
  created: number
  skippedVirtualMachines: number
  isSimulated: boolean
  message: string
}

export interface BackupItem {
  fileName: string
  createdAt: string
  size: number
}

export interface BackupListResponse {
  items: BackupItem[]
}

export interface LicenseResource {
  name: string
  authorizationCode: string
  quantity: number
}

export interface LicenseItem {
  id: string
  productAuthorizationCode: string
  fileName: string
  importedAt: string
  issuedAt: string
  expiresAt: string | null
  resources: LicenseResource[]
  valid: boolean
}

export interface LicenseListResponse {
  machineCode: string
  publicKeyConfigured: boolean
  mode: string
  items: LicenseItem[]
}

export interface LicenseUploadResult {
  importedCount: number
}

export interface SettingsResponse {
  schoolName: string
  backupDirectory?: string
  dataDirectory?: string
  version?: string
  resourceMode?: string
  backupIncludesKeys?: boolean
}

export interface VirtualMachineRow {
  id: string
  name: string
  nodeId: string
  nodeName: string
  status: string
  cpuCount: number
  memoryMb: number
  diskGb: number
  createdAt?: string
  startedAt?: string | null
  remark: string
  isSimulated: boolean
}

export interface NodeRow {
  id: string
  name: string
  host?: string
  provider: string
  enabled: boolean
  accountCount?: number
  accountQuota?: number
  virtualMachineCount?: number
  virtualMachineQuota?: number
  isSimulated?: boolean
}

export interface AssignedNode {
  id: string
  name: string
  enabled: boolean
}

export interface AssignedDevice {
  id: string
  name: string
  nodeId: string
  nodeName: string
  cabinetName: string
  isSimulated: boolean
  sessionActive: boolean
  canAccess: boolean
}

export type AssignedVirtualMachine = Pick<
  VirtualMachineRow,
  'id' | 'name' | 'nodeId' | 'nodeName' | 'status' | 'cpuCount' | 'memoryMb' | 'diskGb' | 'isSimulated'
> & { uuid: string; ipAddress: string; canAccess: boolean }

export interface MyResourcesResponse {
  nodes: AssignedNode[]
  devices: AssignedDevice[]
  virtualMachines: AssignedVirtualMachine[]
}

export type CourseType = 'general' | 'core' | 'open'
export type CourseStatus = 'draft' | 'published'
export type ResourceKind = 'theory' | 'case' | 'theoryPractice' | 'casePractice'
export type QuestionType = 'single' | 'multiple' | 'judge'
export type ClassroomStatus = 'scheduled' | 'open' | 'closed'

export interface CourseSummary {
  id: string
  name: string
  description: string
  type: CourseType
  status: CourseStatus
  remark: string
  createdAt: string
  publishedAt: string | null
  isActive: boolean
  seriesId: string | null
  chapterCount: number
  sectionCount: number
  resourceCount: number
  theoryCount: number
  theoryPracticeCount: number
  caseCount: number
  casePracticeCount: number
  totalMinutes: number
}

export interface CoreCourseSeries {
  id: string
  name: string
  description: string
  courseCount: number
}

export interface CourseResourceSummary {
  id: string
  name: string
  kind: ResourceKind
  durationMinutes: number
  sortOrder: number
  startupNodeId?: string | null
  questionCount: number
  fileCount?: number
}

export interface CourseResourceFile {
  id: string
  resourceId: string
  name: string
  size: number
  isPinned: boolean
  canPreview: boolean
  sortOrder: number
  createdAt: string
}

export interface CourseSectionNode {
  id: string
  name: string
  durationMinutes: number
  sortOrder: number
  resources: CourseResourceSummary[]
}

export interface CourseChapterNode {
  id: string
  name: string
  sortOrder: number
  sections: CourseSectionNode[]
}

export interface CourseDetail extends CourseSummary {
  chapters: CourseChapterNode[]
}

export interface CoursePreviewQuestion {
  id: string
  type: QuestionType
  stem: string
  options: string[]
  answers: number[]
  analysis: string
  score: number
  sortOrder: number
}

export interface CoursePreviewResource {
  id: string
  name: string
  kind: ResourceKind
  durationMinutes: number
  sortOrder: number
  content: string
  topology: string
  startupNodeId: string | null
  startupNodeName: string
  questionCount: number
  maxScore: number
  files: CourseResourceFile[]
  questions: CoursePreviewQuestion[]
}

export interface CoursePreviewSection {
  id: string
  name: string
  durationMinutes: number
  sortOrder: number
  resources: CoursePreviewResource[]
}

export interface CoursePreviewChapter {
  id: string
  name: string
  sortOrder: number
  sections: CoursePreviewSection[]
}

export interface CoursePreviewResponse {
  course: {
    id: string
    name: string
    description: string
    type: CourseType
    status: CourseStatus
    publishedAt: string | null
  }
  preview: boolean
  showAnswers: boolean
  tree: CoursePreviewChapter[]
}

export type CoursePreviewPracticeResponse = PracticeSubmitResponse

export interface CoursePreviewEnvironment {
  resource: {
    id: string
    name: string
    kind: ResourceKind
    topology: string
    startupNodeId: string | null
    topologyBlueprint: CaseTopologyBlueprint | null
  }
  topologyDevices: { id: string; name: string }[]
  node: { id: string; name: string; host: string; enabled: boolean; remark: string } | null
  devices: {
    id: string
    name: string
    cabinetName: string
    nodeName: string
    isSimulated: boolean
    canAccess: boolean
  }[]
  virtualMachines: {
    id: string
    name: string
    status: string
    cpuCount: number
    memoryMb: number
    diskGb: number
    canConnect: boolean
  }[]
}

export interface CourseImportableResource {
  id: string
  name: string
  kind: ResourceKind
  durationMinutes: number
  sectionId: string
  sectionName: string
  chapterName: string
  courseId: string
  courseName: string
  questionCount: number
  fileCount: number
}

export interface CourseImportableResponse {
  target: { id: string; name: string }
  items: CourseImportableResource[]
}

export interface CourseQuestionParseError {
  rowNumber: number
  column: string
  message: string
  stem: string
}

export interface CourseQuestionParseResponse {
  validCount: number
  invalidCount: number
  errors: CourseQuestionParseError[]
  questions: Omit<CourseQuestion, 'id' | 'sortOrder'>[]
}

export interface CourseQuestion {
  id?: string
  type: QuestionType
  stem: string
  options: string[]
  answers?: number[]
  analysis?: string
  score: number
  sortOrder?: number
}

export interface CourseResourceDetail {
  id: string
  sectionId: string
  name: string
  kind: ResourceKind
  durationMinutes: number
  content: string
  topology: string
  topologyBlueprint: CaseTopologyBlueprint | null
  startupNodeId: string | null
  sortOrder: number
  questions: CourseQuestion[]
  files: CourseResourceFile[]
  caseRubric: CaseRubricItem[]
}

export interface ClassroomSummary {
  id: string
  name: string
  courseId: string
  courseName: string
  courseType: CourseType
  teacherUserId: string
  teacherName: string
  openAt: string
  closeAt: string
  status: ClassroomStatus
  storedStatus: ClassroomStatus
  createdAt: string
  studentCount: number
  resources: Pick<
    CourseSummary,
    'theoryCount' | 'theoryPracticeCount' | 'caseCount' | 'casePracticeCount' | 'totalMinutes'
  >
}

export interface ClassroomStudent {
  userId: string
  personId: string
  name: string
  userName: string
  number: string
  classId: string | null
  className: string
  professionName: string
}

export interface TeachingClass {
  id: string
  name: string
  professionId: string
  professionName: string
  studentCount: number
}

export interface TeachingClassesResponse {
  professions: { id: string; name: string; code: string }[]
  classes: TeachingClass[]
}

export interface TeacherOption {
  id: string
  userName: string
  displayName: string
  role: string
  number: string
}

export interface LearningRecord {
  id: string
  resourceId: string
  status: string
  bestScore: number
  attemptCount: number
  startedAt: string
  completedAt: string | null
}

export interface ClassroomResource extends CourseResourceSummary {
  content: string
  topology: string
  maxScore: number
  completedCount: number
  files: CourseResourceFile[]
  questions: CourseQuestion[]
}

export interface ClassroomSection extends Omit<CourseSectionNode, 'resources'> {
  resources: ClassroomResource[]
}

export interface ClassroomChapter extends Omit<CourseChapterNode, 'sections'> {
  sections: ClassroomSection[]
}

export interface ClassroomContentResponse {
  classroom: {
    id: string
    name: string
    courseId: string
    courseName: string
    courseType: CourseType
    teacherUserId: string
    teacherName: string
    openAt: string
    closeAt: string
    status: ClassroomStatus
    createdAt: string
  }
  course: { id: string; name: string; type: CourseType; description: string }
  canLearn: boolean
  isPreview: boolean
  tree: ClassroomChapter[]
  records: LearningRecord[]
}

export interface ClassroomProgressStudent {
  userId: string
  personId: string
  name: string
  userName: string
  number: string
  classId: string | null
  className: string
  completed: number
  total: number
  averageScore: number
  lastActivity: string | null
}

export interface ClassroomProgressResponse {
  classroomId: string
  name: string
  totalStudents: number
  totalResources: number
  completedRecords: number
  students: ClassroomProgressStudent[]
  resources: { id: string; name: string; kind: ResourceKind; completed: number }[]
}

export interface PracticeSubmitResponse {
  score: number
  maxScore: number
  passed: boolean
  questions: { questionId: string; correct: boolean; correctAnswers: number[]; analysis: string; score: number }[]
}

export interface ClassroomPracticeResult extends PracticeSubmitResponse {
  startedAt: string
  submittedAt: string
  attemptCount: number
  bestScore: number
  averageScore: number
  questions: (PracticeSubmitResponse['questions'][number] & { selected: number[] })[]
}

export type ExamQuestionType = 'single' | 'multiple' | 'judge' | 'practical'
export type ExamQuestionBankType = 'theory' | 'practical' | 'mixed'

export interface ExamQuestionBank {
  id: string
  name: string
  type: ExamQuestionBankType
  isDefault: boolean
  questionCount: number
  createdAt: string
}
export type ExamStatus = 'scheduled' | 'open' | 'closed'
export type AttemptStatus = 'notStarted' | 'inProgress' | 'submitted' | 'graded'

export interface ExamQuestionSummary {
  bankId: string
  bankName: string
  automaticPractical: boolean
  id: string
  type: ExamQuestionType
  stem: string
  options: string[]
  answers: number[]
  analysis: string
  score: number
  createdAt: string
  updatedAt: string
  paperCount: number
}

export interface ExamQuestionDetail extends Omit<ExamQuestionSummary, 'paperCount'> {
  practicalNodeId: string
  practicalRubric: CaseRubricItem[]
  practicalResources: string
  papers: { id: string; name: string }[]
}

export interface ExamQuestionInput {
  bankId?: string
  practicalNodeId?: string
  practicalRubric?: CaseRubricItem[]
  practicalResources?: string
  type: ExamQuestionType
  stem: string
  options: string[]
  answers: number[]
  score: number
  analysis: string
}

export interface ExamPaperSummary {
  id: string
  name: string
  description: string
  remark: string
  createdAt: string
  updatedAt: string
  questionCount: number
  totalScore: number
  typeCounts: Partial<Record<ExamQuestionType, number>>
  examCount: number
}

export interface ExamPaperQuestion {
  id: string
  questionId: string
  type: ExamQuestionType
  stem: string
  options: string[]
  answers: number[]
  analysis: string
  score: number
  sortOrder: number
}

export interface ExamPaperDetail extends Omit<ExamPaperSummary, 'examCount'> {
  questions: ExamPaperQuestion[]
  exams: { id: string; name: string; status: string }[]
  questionEditable: boolean
}

export interface TrainingExamSummary {
  id: string
  name: string
  paperId: string
  description: string
  startAt: string
  endAt: string
  durationMinutes: number
  status: ExamStatus
  storedStatus: ExamStatus
  createdAt: string
  paperName: string
  questionCount: number
  totalScore: number
  studentCount: number
  attemptCount: number
  submittedCount: number
  gradedCount: number
}

export interface TrainingExamDetail extends TrainingExamSummary {
  updatedAt: string
  canEdit: boolean
}

export interface ExamStudentOption {
  userId: string
  personId: string
  name: string
  userName: string
  number: string
  classId: string | null
  className: string
}

export interface ExamRosterStudent {
  gender: string
  phone: string
  email: string
  studentUserId: string
  studentPersonId: string
  name: string
  userName: string
  number: string
  classId: string | null
  className: string
  deploymentStatus: string
  deploymentMessage: string
  deployedAt: string | null
  addedAt: string
  attemptId: string | null
  attemptStatus: AttemptStatus
  startedAt: string | null
  submittedAt: string | null
  autoSubmitted: boolean
  totalScore: number | null
  maxScore: number | null
}

export interface ExamTypeScore {
  type: ExamQuestionType
  label: string
  maxScore: number
  score: number
}

export interface ExamScoreRow {
  studentUserId: string
  name: string
  userName: string
  number: string
  className: string
  attemptId: string | null
  status: AttemptStatus
  statusLabel: string
  startedAt: string | null
  submittedAt: string | null
  elapsedSeconds: number | null
  autoSubmitted: boolean
  objectiveScore: number
  practicalScore: number
  totalScore: number
  maxScore: number
  gradedAt: string | null
  typeScores: ExamTypeScore[]
}

export interface ExamMonitorRow {
  studentUserId: string
  name: string
  userName: string
  number: string
  className: string
  deploymentStatus: string
  deploymentMessage: string
  attemptId: string | null
  status: AttemptStatus
  statusLabel: string
  answeredCount: number
  questionCount: number
  progress: number
  startedAt: string | null
  submittedAt: string | null
  autoSubmitted: boolean
  lastAnsweredAt: string | null
  totalScore: number | null
  maxScore: number | null
}

export interface ExamMonitorResponse {
  items: ExamMonitorRow[]
  counts: {
    expected: number
    notStarted: number
    inProgress: number
    submitted: number
    graded: number
    deployed: number
  }
}

export interface ExamTypeStat {
  type: ExamQuestionType
  label: string
  questionCount: number
  maxScore: number
  answered: number
  correct: number
  accuracy: number
  scoreRate: number
}

export interface ExamStatistics {
  expected: number
  started: number
  submitted: number
  graded: number
  absent: number
  maxScore: number
  averageScore: number
  highestScore: number
  lowestScore: number
  passRate: number
  averageRate: number
  bands: { label: string; count: number; percent: number }[]
  typeStats: ExamTypeStat[]
}

export interface ExamAnalysisRow {
  paperQuestionId: string
  type: ExamQuestionType
  typeLabel: string
  stem: string
  maxScore: number
  sortOrder: number
  number: number
  answered: number
  unanswered: number
  evaluated: number
  correctCount: number
  accuracy: number
}

export interface ExamAnalysisResponse extends Paged<ExamAnalysisRow> {
  finishedCount: number
  maxScore: number
}

export interface AttemptReviewQuestion {
  automaticPractical: boolean
  practicalDetails: CaseGrade['details']
  paperQuestionId: string
  type: ExamQuestionType
  stem: string
  options: string[]
  answers: number[]
  analysis: string
  maxScore: number
  sortOrder: number
  selected: number[]
  textAnswer: string
  scoreAwarded: number | null
  comment: string
  correct: boolean
  answered: boolean
}

export interface AttemptReview {
  attemptId: string
  examId: string
  studentName: string
  studentUserName: string
  studentNumber: string
  className: string
  status: AttemptStatus
  startedAt: string
  deadline: string
  submittedAt: string | null
  autoSubmitted: boolean
  objectiveScore: number
  practicalScore: number
  totalScore: number
  maxScore: number
  gradedAt: string | null
  pendingPractical: number
  questions: AttemptReviewQuestion[]
}

export interface ExamHallItem {
  id: string
  name: string
  description: string
  startAt: string
  endAt: string
  durationMinutes: number
  status: ExamStatus
  storedStatus: ExamStatus
  paperName: string
  questionCount: number
  totalScore: number
  deploymentStatus: string
  deploymentMessage: string
  attemptId: string | null
  attemptStatus: AttemptStatus
  startedAt: string | null
  deadline: string | null
  submittedAt: string | null
  autoSubmitted: boolean
  objectiveScore: number | null
  practicalScore: number | null
  attemptScore: number | null
  maxScore: number | null
  remainingSeconds: number
  canStart: boolean
  canReview: boolean
}

export interface ExamAttemptState {
  id: string
  status: AttemptStatus
  startedAt: string
  deadline: string
  submittedAt: string | null
  autoSubmitted: boolean
  objectiveScore: number
  practicalScore: number
  totalScore: number
  maxScore: number
  gradedAt: string | null
  remainingSeconds: number
}

export interface ExamTakingQuestion {
  automaticPractical: boolean
  practicalSaved: boolean
  practicalResources: string
  paperQuestionId: string
  type: ExamQuestionType
  stem: string
  options: string[]
  score: number
  sortOrder: number
  selected: number[]
  textAnswer: string
}

export interface ExamHallDetail {
  serverNow: string
  id: string
  name: string
  description: string
  startAt: string
  endAt: string
  durationMinutes: number
  status: ExamStatus
  storedStatus: ExamStatus
  paperName: string
  questionCount: number
  totalScore: number
  deploymentStatus: string
  deploymentMessage: string
  canStart: boolean
  attempt: ExamAttemptState | null
  questions: ExamTakingQuestion[] | null
}

export interface ExamSubmitAnswer {
  paperQuestionId: string
  selected: number[]
  text: string
}

export interface ExamSubmitResult {
  attemptId: string
  status: AttemptStatus
  objectiveScore: number
  practicalScore: number
  totalScore: number
  maxScore: number
  autoSubmitted: boolean
  pendingPractical: boolean
}

export interface ExamRecordSummary {
  attemptId: string
  examId: string
  examName: string
  paperName: string
  questionCount: number
  status: AttemptStatus
  startedAt: string
  submittedAt: string | null
  autoSubmitted: boolean
  objectiveScore: number
  practicalScore: number
  totalScore: number
  maxScore: number
  gradedAt: string | null
  canReview: boolean
}

export interface AccountAssignment {
  id: string
  userId: string
  personId: string
  kind: string
  name: string
  userName: string
  number: string
  role: string
  assignedAt: string
}

export interface AccountCandidate {
  userId: string
  personId: string
  kind: string
  name: string
  userName: string
  number: string
  role: string
}

export interface NodeAccountsResponse extends Paged<AccountAssignment> {
  accountQuota: number
  accountCount: number
  enabled: boolean
  semantics: string
}

export interface ConnectionProbe {
  id: string
  name: string
  success: boolean
  message: string
  isSimulated: boolean
  level: string
}

export interface CabinetSummary {
  id: string
  name: string
}

export interface ConnectionProbeList {
  items: ConnectionProbe[]
}

export interface ImportIssue {
  column: string
  message: string
}

export interface ImportRow {
  rowNumber: number
  data: Record<string, string>
  errors: ImportIssue[]
}

export interface ImportPreviewResponse {
  batchId: string
  rows: ImportRow[]
  validCount: number
  errorCount: number
}

export type ImportJobState = 'queued' | 'running' | 'cancelling' | 'completed' | 'failed' | 'cancelled'

export interface ImportStatusResponse {
  batchId: string
  status: ImportJobState
  processedCount: number
  totalCount: number
  importedCount: number
  removedCount: number
  failureMessage: string
  message: string
  createdAt?: string
  queuedAt?: string | null
  startedAt?: string | null
  completedAt?: string | null
}

export type CampAttemptStatus = 'inProgress' | 'graded'

export interface CampSkill {
  id: string
  name: string
  subName: string
  ability: string
  sortOrder: number
}

export interface CampRubricItem {
  item: string
  keyword: string
  score: number
}

export interface CampSkillInput {
  name: string
  subName: string
  ability: string
}

export interface CampRubricItemInput {
  item: string
  keyword: string
  score: number
}

export interface CampQuestionInput {
  type: ExamQuestionType
  stem: string
  options: string[]
  answers: number[]
  score: number
  analysis: string
  skillIndex: number
  rubric?: CampRubricItemInput[]
  practicalRubric?: CaseRubricItem[]
}

export interface CampPositionInput {
  name: string
  description: string
  durationMinutes: number
  startupNodeId: string | null
  skills: CampSkillInput[]
  questions: CampQuestionInput[]
}

export interface CampQuestionAdmin {
  id: string
  type: ExamQuestionType
  stem: string
  options: string[]
  answers: number[]
  analysis: string
  score: number
  sortOrder: number
  skillIndex: number
  rubric: CampRubricItem[]
  practicalRubric: CaseRubricItem[]
}

export interface CampPositionAdminSummary {
  id: string
  name: string
  description: string
  durationMinutes: number
  startupNodeId: string | null
  createdAt: string
  updatedAt: string
  skillCount: number
  questionCount: number
  practicalCount: number
  maxScore: number
  attemptCount: number
}

export interface CampPositionAdminDetail {
  id: string
  name: string
  description: string
  durationMinutes: number
  startupNodeId: string | null
  createdAt: string
  updatedAt: string
  attemptCount: number
  canEditStructure: boolean
  questionCount: number
  practicalCount: number
  maxScore: number
  skills: CampSkill[]
  questions: CampQuestionAdmin[]
}

export interface CampAttemptSnapshot {
  id: string
  status: CampAttemptStatus
  startedAt: string
  deadline: string
  submittedAt: string | null
  autoSubmitted: boolean
  totalScore: number
  maxScore: number
  remainingSeconds: number
}

export interface CampPositionLearnerSummary {
  id: string
  name: string
  description: string
  durationMinutes: number
  startupNodeId: string | null
  skillCount: number
  questionCount: number
  practicalCount: number
  maxScore: number
  attempt: CampAttemptSnapshot | null
  canStart: boolean
}

export interface CampAttemptState {
  id: string
  status: CampAttemptStatus
  startedAt: string
  deadline: string
  submittedAt: string | null
  autoSubmitted: boolean
  objectiveScore: number
  practicalScore: number
  totalScore: number
  maxScore: number
  gradedAt: string | null
  remainingSeconds: number
}

export interface CampTakingQuestion {
  id: string
  type: ExamQuestionType
  stem: string
  options: string[]
  score: number
  sortOrder: number
  skillName: string
  selected: number[]
  textAnswer: string
  automaticPractical: boolean
  hasWork: boolean
}

export interface CampPositionLearnerDetail {
  id: string
  name: string
  description: string
  durationMinutes: number
  startupNodeId: string | null
  questionCount: number
  practicalCount: number
  maxScore: number
  skillCount: number
  attempt: CampAttemptState | null
  canStart: boolean
  canReview: boolean
  questions: CampTakingQuestion[] | null
}

export interface CampStartResponse {
  attemptId: string
  status: CampAttemptStatus
  startedAt: string
  deadline: string
  remainingSeconds: number
}

export interface CampSaveAnswer {
  questionId: string
  selected: number[]
  text: string
}

export interface CampSubmitResponse {
  attemptId: string
  status: CampAttemptStatus
  objectiveScore: number
  practicalScore: number
  totalScore: number
  maxScore: number
  autoSubmitted: boolean
  percent: number
}

export interface CampHistoryRow {
  attemptId: string
  positionId: string
  positionName: string
  status: CampAttemptStatus
  startedAt: string
  deadline: string
  submittedAt: string | null
  autoSubmitted: boolean
  totalScore: number
  maxScore: number
  percent: number
  remainingSeconds: number
  hasReport: boolean
}

export interface CampSkillRow {
  skill: string
  subSkill: string
  ability: string
  questionCount: number
  score: number
  maxScore: number
  percent: number
  result: string
}

export interface CampRadarAxis {
  name: string
  score: number
  maxScore: number
  percent: number
}

export interface CampReport {
  attemptId: string
  position: { id: string; name: string; description: string }
  attempt: {
    studentName: string
    studentNumber: string
    studentUserName: string
    className: string
    startedAt: string
    submittedAt: string | null
    autoSubmitted: boolean
    objectiveScore: number
    practicalScore: number
    totalScore: number
    maxScore: number
    percent: number
    level: string
  }
  skills: CampSkillRow[]
  radar: CampRadarAxis[]
  questionCount: number
  evaluatedAt: string | null
}

export interface CampRecordQuestion {
  questionId: string
  type: ExamQuestionType
  stem: string
  options: string[]
  answers: number[]
  analysis: string
  maxScore: number
  sortOrder: number
  skillName: string
  subSkill: string
  skillAbility: string
  selected: number[]
  textAnswer: string
  scoreAwarded: number
  correct: boolean
  answered: boolean
  automaticPractical: boolean
  practicalDetails: CaseGrade['details']
  rubric: { item: string; keyword: string; score: number; matched: boolean }[]
}

export interface CampRecord {
  attemptId: string
  position: { id: string; name: string; description: string }
  attempt: {
    studentName: string
    studentNumber: string
    studentUserName: string
    className: string
    status: CampAttemptStatus
    startedAt: string
    deadline: string
    submittedAt: string | null
    autoSubmitted: boolean
    objectiveScore: number
    practicalScore: number
    totalScore: number
    maxScore: number
    percent: number
    gradedAt: string | null
  }
  questions: CampRecordQuestion[]
}

export interface CampStaffReportRow {
  attemptId: string
  positionId: string
  positionName: string
  studentName: string
  studentNumber: string
  studentUserName: string
  className: string
  totalScore: number
  maxScore: number
  percent: number
  submittedAt: string | null
  gradedAt: string | null
}

export interface CampStaffRecordRow {
  attemptId: string
  positionId: string
  positionName: string
  studentName: string
  studentNumber: string
  studentUserName: string
  className: string
  totalScore: number
  maxScore: number
  startedAt: string
  submittedAt: string | null
  autoSubmitted: boolean
}

export interface CampMonitorRow {
  attemptId: string
  positionId: string
  positionName: string
  studentName: string
  studentNumber: string
  studentUserName: string
  className: string
  startedAt: string
  deadline: string
  remainingSeconds: number
  virtualMachineId: string | null
  virtualMachineName: string
  virtualMachineStatus: string
  canRestart: boolean
}

export interface CampRestartResponse {
  virtualMachineId: string
  virtualMachineName: string
  virtualMachineStatus: string
  restartedAt: string
}

export interface CampEnvironment {
  nodeId: string | null
  node: { id: string; name: string; host: string; enabled: boolean; remark: string } | null
  devices: { id: string; name: string; cabinetName: string; isSimulated: boolean; canAccess: boolean }[]
  virtualMachines: {
    id: string
    name: string
    status: string
    cpuCount: number
    memoryMb: number
    diskGb: number
    selected: boolean
  }[]
}

export interface ProfileResponse {
  id: string
  userName: string
  role: string
  displayName: string
  kind: string
  name: string
  number: string
  gender: string
  phone: string
  email: string
  classId: string | null
}

export interface ClassroomGroupMember {
  id: string
  groupId: string
  studentUserId: string
  userName: string
  name: string
  number: string
  className: string
}

export interface ClassroomGroup {
  id: string
  name: string
  remark: string
  createdAt: string
  members: ClassroomGroupMember[]
}

export interface ClassroomGroupListResponse {
  total: number
  items: ClassroomGroup[]
}

export interface ClassroomCabinetOption {
  id: string
  name: string
  host: string
  port: number
  jumpHost: string
  jumpPort: number
  isSimulated: boolean
  assigned: boolean
  assignedGroupName: string
  assignedClassroomName: string
}

export interface ClassroomCollaborationOptions {
  groups: { id: string; name: string }[]
  resources: { id: string; name: string; kind: ResourceKind }[]
  cabinets: ClassroomCabinetOption[]
}

export interface ClassroomCabinetAssignment {
  id: string
  cabinetId: string
  name: string
  host: string
  port: number
  jumpHost: string
  jumpPort: number
  isSimulated: boolean
  groupId: string
  groupName: string
  resourceId: string
  questionName: string
  assignedAt: string
}

export interface ClassroomCabinetAssignmentList {
  total: number
  items: ClassroomCabinetAssignment[]
}

export interface ClassroomCabinetProbe {
  assignmentId: string
  cabinetId: string
  name: string
  success: boolean
  message: string
  isSimulated: boolean
}

export interface ClassroomCaseSession {
  sessionId: string
  userId: string
  studentName: string
  studentNumber: string
  userName: string
  className: string
  chapterName: string
  sectionName: string
  resourceId: string
  resourceName: string
  startedAt: string
  endedAt: string | null
  status: 'active' | 'ended'
  restartCount: number
  virtualMachineId: string | null
  virtualMachineName: string
  virtualMachineStatus: string
  nodeId: string | null
  nodeName: string
  evidenceCount: number
  canRestart: boolean
}

export interface ClassroomCaseSessionList {
  classroom: { id: string; name: string; status: ClassroomStatus }
  total: number
  activeCount: number
  items: ClassroomCaseSession[]
}

export interface CaseEvidence {
  id: string
  fileName: string
  size: number
  note: string
  createdAt: string
  capturedByName: string
}

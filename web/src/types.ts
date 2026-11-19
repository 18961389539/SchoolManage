// Explicit request/response contracts mirrored from the ASP.NET Core endpoints.
// Dynamic entity forms and tables still use Row (Record<string, any>) because their
// fields are configured at runtime; everything with a stable shape belongs here.

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
  success: boolean
  message: string
  isSimulated: boolean
  level: string
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

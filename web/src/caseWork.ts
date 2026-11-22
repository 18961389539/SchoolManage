export type CaseField = 'hostname' | 'enabled' | 'description'

export const caseFieldLabel: Record<CaseField, string> = {
  hostname: '主机名',
  enabled: '接口状态',
  description: '接口说明'
}

export interface CaseRubricItem {
  name: string
  deviceId: string
  field: CaseField
  interfaceName: string | null
  expected: string
  score: number
  guidance: string
}

export interface CaseDeviceOption {
  id: string
  name: string
  interfaces: string[]
}

export interface CaseTopologyLink {
  fromDeviceId: string
  fromPort: string
  toDeviceId: string
  toPort: string
  network: string
}

export interface CaseTopologyBlueprint {
  deviceIds: string[]
  links: CaseTopologyLink[]
}

export interface CaseDevice {
  id: string
  name: string
  configuration: {
    hostname: string
    interfaces: Record<string, { enabled: boolean; description: string }>
  }
}

export interface CaseGrade {
  id: string
  isFinal: boolean
  startedAt: string
  scoredAt: string
  score: number
  maxScore: number
  percent: number
  passed: boolean
  details: {
    name: string
    deviceName: string
    field: CaseField
    interfaceName: string | null
    expected: string
    actual: string | null
    score: number
    matched: boolean
    guidance: string | null
  }[]
}

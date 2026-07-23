export type SceneKind = 'Random' | 'Office' | 'Meadow' | 'Kitchen'

export interface FailureTarget {
  workflowName: string
  stepId: string
  stepType: string
  label: string
}

export interface PreviewDiagnostic {
  code: string
  message: string
  severity: 'Warning' | 'Error'
  workflowName?: string
  stepId?: string
  field?: string
}

export interface WorkflowSummary {
  name: string
  stepCount: number
  isEntrypoint: boolean
}

export interface ValidationResponse {
  valid: boolean
  entrypoint?: string
  diagnostics: PreviewDiagnostic[]
  failureTargets: FailureTarget[]
  workflows: WorkflowSummary[]
}

export interface SimulationRequest {
  workflow: string
  inputs?: unknown
  seed?: number
  scene: SceneKind
  speed: number
  failAt?: { workflowName: string; stepId: string }
}

export interface SimulationPrepared {
  simulationId: string
  seed: number
  scene: SceneKind
  durationMs: number
  svg: string
  actorCount: number
  stepEventCount: number
  taskObjectCount: number
  canvasWidth: number
  canvasHeight: number
  laneCount: number
  nodeCount: number
  warnings: PreviewDiagnostic[]
}

export interface SimulationEvent {
  sequence: number
  type: string
  offsetMs: number
  durationMs: number
  workflowInstanceId?: string
  workflowName?: string
  actorId?: string
  targetActorId?: string
  stepId?: string
  stepType?: string
  stationId?: string
  nodeId?: string
  targetNodeId?: string
  edgeId?: string
  taskId?: string
  branchId?: string
  status?: 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Skipped'
  progressCurrent?: number
  progressTotal?: number
  x?: number
  y?: number
  message?: string
}

export interface StreamEnvelope {
  type: string
  timestamp: string
  prepared?: SimulationPrepared
  event?: SimulationEvent
}

export interface ApiError {
  code: string
  message: string
  diagnostics?: PreviewDiagnostic[]
}

export interface FeedItem {
  key: string
  type: string
  message: string
  status?: string
  stepId?: string
  workflowName?: string
}

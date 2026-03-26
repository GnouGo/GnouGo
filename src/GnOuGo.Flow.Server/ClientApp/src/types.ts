// ── Shared types for GnOuGo.Flow Client ──

export type TabId = 'runner' | 'editor'

export type StreamEventType =
  | 'workflow.started'
  | 'workflow.summary'
  | 'workflow.completed'
  | 'workflow.result'
  | 'step.started'
  | 'step.event'
  | 'step.completed'

export interface StepResult {
  stepId: string
  stepType: string
  status: string
  durationMs: number
  error?: string
}

export interface WorkflowResult {
  success: boolean
  outputs?: unknown
  error?: { code: string; message: string; retryable: boolean }
  steps: StepResult[]
}

export interface WorkflowUsageSummary {
  inputTokens: number
  outputTokens: number
  totalTokens: number
  estimatedCostUsd?: number | null
  tokenizedStepCount: number
  pricedStepCount: number
  models: string[]
}

export interface StepUsageSummary {
  model?: string | null
  system?: string | null
  inputTokens?: number | null
  outputTokens?: number | null
  totalTokens?: number | null
  estimatedCostUsd?: number | null
  finishReason?: string | null
}

export interface WorkflowStartedStreamData {
  workflowName: string
  documentName?: string | null
  inputs?: unknown
}

export interface WorkflowSummaryStreamData {
  summary: WorkflowUsageSummary
}

export interface WorkflowCompletedStreamData {
  success: boolean
  stepsExecuted: number
  durationMs: number
  errorCode?: string | null
  errorMessage?: string | null
  summary: WorkflowUsageSummary
}

export interface StepStartedStreamData {
  stepId: string
  stepType: string
  callDepth: number
  input?: unknown
}

export interface StepTelemetryEventStreamData {
  stepId: string
  stepType: string
  callDepth: number
  name: string
  attributes: Record<string, unknown>
  contentText?: string | null
  contentJson?: unknown
}

export interface StepCompletedStreamData {
  stepId: string
  stepType: string
  callDepth: number
  status: string
  durationMs: number
  output?: unknown
  errorCode?: string | null
  errorMessage?: string | null
  attributes: Record<string, unknown>
  usage: StepUsageSummary
}

export interface WorkflowResultStreamData {
  response: WorkflowResult
  summary?: WorkflowUsageSummary
}

export interface StreamEnvelope<T = unknown> {
  type: StreamEventType
  timestamp: string
  data: T
}

export interface LiveStep {
  key: string
  order: number
  stepId: string
  stepType: string
  callDepth: number
  status: string
  durationMs?: number
  error?: string
  input?: unknown
  output?: unknown
  prompt?: string
  completion?: string
  usage?: StepUsageSummary
  attributes?: Record<string, unknown>
  telemetryEvents: Array<{ name: string; contentText?: string | null; contentJson?: unknown }>
  thinkingMessages: ThinkingMessage[]
}

export interface ThinkingMessage {
  message: string
  level: string
  timestamp: string
}

export interface HumanInputFieldDef {
  name: string
  type: string
  required?: boolean
  description?: string
  options?: string[]
  default?: string
}

export interface PendingHumanInput {
  runId: string
  stepId: string
  prompt: string
  choices?: string[]
  fields?: HumanInputFieldDef[]
  context?: unknown
  timeout_ms?: number
  /** ISO timestamp when the request was emitted (from the SSE envelope). */
  requestedAt?: string
}


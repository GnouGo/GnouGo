// ── TelemetrySummary — summary cards + status badge ──

import type {
  WorkflowCompletedStreamData,
  WorkflowStartedStreamData,
  WorkflowUsageSummary,
} from '../types'
import { formatCurrencyUsd } from '../utils'

interface Props {
  workflowStarted: WorkflowStartedStreamData | null
  workflowCompleted: WorkflowCompletedStreamData | null
  summary: WorkflowUsageSummary | null
}

export function TelemetrySummary({ workflowStarted, workflowCompleted, summary }: Props) {
  if (!workflowStarted && !summary && !workflowCompleted) return null

  return (
    <div className="telemetry-summary">
      <div className="telemetry-summary__grid">
        <div className="telemetry-summary__card">
          <span className="telemetry-summary__label">Workflow</span>
          <strong>{workflowStarted?.workflowName ?? '—'}</strong>
          {workflowStarted?.documentName && <span>{workflowStarted.documentName}</span>}
        </div>
        <div className="telemetry-summary__card">
          <span className="telemetry-summary__label">Tokens</span>
          <strong>{summary?.totalTokens ?? 0}</strong>
          <span>in {summary?.inputTokens ?? 0} / out {summary?.outputTokens ?? 0}</span>
        </div>
        <div className="telemetry-summary__card">
          <span className="telemetry-summary__label">Estimated cost</span>
          <strong>{formatCurrencyUsd(summary?.estimatedCostUsd)}</strong>
          <span>{summary?.pricedStepCount ?? 0} priced step(s)</span>
        </div>
        <div className="telemetry-summary__card">
          <span className="telemetry-summary__label">Models</span>
          <strong>{summary?.models?.length ? summary.models.join(', ') : '—'}</strong>
          <span>{summary?.tokenizedStepCount ?? 0} step(s) with usage</span>
        </div>
      </div>

      {workflowCompleted && (
        <div
          className={`status-badge ${workflowCompleted.success ? 'status-badge--success' : 'status-badge--failure'}`}
        >
          {workflowCompleted.success ? '✅ Success' : '❌ Failed'}
          {' — '}
          {workflowCompleted.stepsExecuted} step(s), {workflowCompleted.durationMs.toFixed(1)}ms
          {workflowCompleted.errorMessage && ` — ${workflowCompleted.errorMessage}`}
        </div>
      )}
    </div>
  )
}


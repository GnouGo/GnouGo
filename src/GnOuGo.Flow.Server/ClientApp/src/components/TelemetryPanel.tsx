// ── TelemetryPanel — right panel: live telemetry, steps, outputs ──

import { useState } from 'react'
import type { WorkflowStreamState } from '../hooks/useWorkflowStream'
<<<<<<< human-in-the-loop
import type { PendingHumanInput } from '../types'
=======
>>>>>>> feature/structured-output
import { ThinkingFeed } from './ThinkingFeed'
import { TelemetrySummary } from './TelemetrySummary'
import { StepList } from './StepList'
import { FinalOutputs } from './FinalOutputs'
<<<<<<< human-in-the-loop
import { HumanInputForm } from './HumanInputForm'
=======
>>>>>>> feature/structured-output

type Props = Pick<
  WorkflowStreamState,
  | 'loading'
  | 'error'
  | 'result'
  | 'summary'
  | 'workflowStarted'
  | 'workflowCompleted'
  | 'orderedSteps'
  | 'allThinkingMessages'
<<<<<<< human-in-the-loop
> & {
  pendingHumanInput?: PendingHumanInput | null
  onSubmitHumanInput?: (data: unknown) => void
}
=======
>
>>>>>>> feature/structured-output

export function TelemetryPanel({
  loading,
  error,
  result,
  summary,
  workflowStarted,
  workflowCompleted,
  orderedSteps,
  allThinkingMessages,
<<<<<<< human-in-the-loop
  pendingHumanInput,
  onSubmitHumanInput,
=======
>>>>>>> feature/structured-output
}: Props) {
  const [showStepDetails, setShowStepDetails] = useState(false)

  const streamStatus = loading
    ? 'running'
    : result
      ? result.success
        ? 'success'
        : 'failure'
      : 'idle'

  const streamLabel = loading
    ? 'Streaming'
    : result
      ? result.success
        ? 'Completed'
        : 'Failed'
      : 'Idle'

  return (
    <div className="panel panel--output">
      <div className="panel__header">
        <h2>📡 Live Telemetry</h2>
        <div className="panel__header-actions">
          <label className="toggle-label">
            <input
              type="checkbox"
              checked={showStepDetails}
              onChange={e => setShowStepDetails(e.target.checked)}
            />
            Show I/O details
          </label>
          <span className={`stream-state stream-state--${streamStatus}`}>{streamLabel}</span>
        </div>
      </div>

      <ThinkingFeed messages={allThinkingMessages} />

<<<<<<< human-in-the-loop
      {pendingHumanInput && onSubmitHumanInput && (
        <HumanInputForm pending={pendingHumanInput} onSubmit={onSubmitHumanInput} />
      )}

=======
>>>>>>> feature/structured-output
      {error && (
        <div className="error-box">
          <strong>Error:</strong> {error}
        </div>
      )}

      <TelemetrySummary
        workflowStarted={workflowStarted}
        workflowCompleted={workflowCompleted}
        summary={summary}
      />

      <StepList steps={orderedSteps} showDetails={showStepDetails} />

      <FinalOutputs result={result} />

      {!error && !loading && !workflowStarted && orderedSteps.length === 0 && !result && (
        <div className="placeholder">
          Click <strong>Execute Workflow</strong> to stream telemetry and debug each workflow node
          live.
        </div>
      )}
    </div>
  )
}


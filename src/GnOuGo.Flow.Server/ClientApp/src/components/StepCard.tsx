// ── StepCard — renders a single live step with usage, errors, I/O details ──

import type { LiveStep } from '../types'
import { formatCurrencyUsd, formatValueAsYaml } from '../utils'
import { ThinkingFeed } from './ThinkingFeed'
import { YamlCodeEditor } from '../YamlCodeEditor'

interface Props {
  step: LiveStep
  showDetails: boolean
}

export function StepCard({ step, showDetails }: Props) {
  return (
    <div className={`step-card step-card--${step.status.toLowerCase()}`}>
      <div className="step-card__header">
        <div>
          <div className="step-card__title">{step.stepId}</div>
          <div className="step-card__meta">
            <span>{step.stepType}</span>
            <span>depth {step.callDepth}</span>
            {step.usage?.model && <span>{step.usage.model}</span>}
          </div>
        </div>
        <div className="step-card__status">
          <strong>{step.status}</strong>
          {step.durationMs !== undefined && <span>{step.durationMs.toFixed(1)}ms</span>}
        </div>
      </div>

      {(step.usage?.inputTokens !== undefined || step.usage?.estimatedCostUsd !== undefined) && (
        <div className="step-card__usage">
          <span>tokens: {step.usage?.totalTokens ?? 0}</span>
          <span>in {step.usage?.inputTokens ?? 0}</span>
          <span>out {step.usage?.outputTokens ?? 0}</span>
          <span>cost {formatCurrencyUsd(step.usage?.estimatedCostUsd)}</span>
          {step.usage?.finishReason && <span>finish: {step.usage.finishReason}</span>}
        </div>
      )}

      {step.error && <div className="step-card__error">{step.error}</div>}

      <ThinkingFeed messages={step.thinkingMessages} inline />

      {showDetails && step.input !== undefined && (
        <div className="step-card__section">
          <h4>Input</h4>
          <YamlCodeEditor
            value={formatValueAsYaml(step.input)}
            onChange={() => {}}
            readOnly
            className="editor editor--readonly"
          />
        </div>
      )}

      {showDetails && step.prompt && (
        <div className="step-card__section">
          <h4>Prompt</h4>
          <pre className="json-output">{step.prompt}</pre>
        </div>
      )}

      {showDetails && step.completion && (
        <div className="step-card__section">
          <h4>Completion</h4>
          <pre className="json-output">{step.completion}</pre>
        </div>
      )}

      {showDetails && step.output !== undefined && (
        <div className="step-card__section">
          <h4>Output</h4>
          <YamlCodeEditor
            value={formatValueAsYaml(step.output)}
            onChange={() => {}}
            readOnly
            className="editor editor--readonly"
          />
        </div>
      )}
    </div>
  )
}


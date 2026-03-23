// ── StepList — renders the "Steps (N)" section with all step cards ──

import type { LiveStep } from '../types'
import { StepCard } from './StepCard'

interface Props {
  steps: LiveStep[]
  showDetails: boolean
}

export function StepList({ steps, showDetails }: Props) {
  if (steps.length === 0) return null

  return (
    <div className="steps-section">
      <h3>Steps ({steps.length})</h3>
      <div className="step-cards">
        {steps.map(step => (
          <StepCard key={step.key} step={step} showDetails={showDetails} />
        ))}
      </div>
    </div>
  )
}


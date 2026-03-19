/* ─── Custom React Flow node for a workflow step ─────────── */
import { memo } from 'react'
import { Handle, Position } from '@xyflow/react'
import type { StepNodeData } from './yamlGraph'

interface StepNodeProps {
  data: StepNodeData
  selected?: boolean
}

function StepNodeComponent({ data, selected }: StepNodeProps) {
  const hasErrors = data.errors.filter(e => e.severity === 'error').length > 0
  const hasWarnings = data.errors.filter(e => e.severity === 'warning').length > 0

  return (
    <div
      className={`step-node ${selected ? 'step-node--selected' : ''} ${hasErrors ? 'step-node--has-errors' : ''}`}
      style={{ borderColor: data.color }}
    >
      <Handle type="target" position={Position.Top} className="step-handle" />

      <div className="step-node__header" style={{ background: data.color }}>
        <span className="step-node__icon">{data.icon}</span>
        <span className="step-node__type">{data.stepType}</span>
        {hasErrors && <span className="step-node__badge step-node__badge--error">✗</span>}
        {!hasErrors && hasWarnings && <span className="step-node__badge step-node__badge--warning">!</span>}
      </div>

      <div className="step-node__body">
        <div className="step-node__id">{data.label || '(no id)'}</div>
        {data.step.if && (
          <div className="step-node__guard" title={`if: ${data.step.if}`}>
            🛡️ <code>{data.step.if.length > 30 ? data.step.if.slice(0, 30) + '…' : data.step.if}</code>
          </div>
        )}
        {data.errors.length > 0 && (
          <div className="step-node__errors">
            {data.errors.slice(0, 2).map((e, i) => (
              <div key={i} className={`step-node__error step-node__error--${e.severity}`}>
                {e.message}
              </div>
            ))}
            {data.errors.length > 2 && (
              <div className="step-node__error-more">+{data.errors.length - 2} more</div>
            )}
          </div>
        )}
      </div>

      <Handle type="source" position={Position.Bottom} className="step-handle" />
    </div>
  )
}

export const StepNode = memo(StepNodeComponent)


// ── FinalOutputs — shows the resolved workflow outputs in YAML ──

import { useMemo } from 'react'
import type { WorkflowResult } from '../types'
import { formatValueAsYaml } from '../utils'
import { YamlCodeEditor } from '../YamlCodeEditor'

interface Props {
  result: WorkflowResult | null
}

export function FinalOutputs({ result }: Props) {
  const outputYaml = useMemo(() => {
    if (result?.outputs === undefined || result.outputs === null) return null
    try {
      return formatValueAsYaml(result.outputs)
    } catch {
      return typeof result.outputs === 'string'
        ? result.outputs
        : JSON.stringify(result.outputs, null, 2)
    }
  }, [result])

  if (!outputYaml) return null

  return (
    <div className="outputs-section">
      <h3>Final Outputs</h3>
      <YamlCodeEditor value={outputYaml} onChange={() => {}} readOnly className="editor" />
    </div>
  )
}


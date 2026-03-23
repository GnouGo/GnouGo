// ── WorkflowInputPanel — left panel: workflow YAML + inputs + run button ──

import { YamlCodeEditor } from '../YamlCodeEditor'

interface Props {
  workflow: string
  onWorkflowChange: (v: string) => void
  inputs: string
  onInputsChange: (v: string) => void
  loading: boolean
  onRun: () => void
}

export function WorkflowInputPanel({
  workflow,
  onWorkflowChange,
  inputs,
  onInputsChange,
  loading,
  onRun,
}: Props) {
  return (
    <div className="panel panel--input">
      <div className="panel__header">
        <h2>📝 Workflow</h2>
      </div>
      <YamlCodeEditor
        value={workflow}
        onChange={onWorkflowChange}
        placeholder="Paste YAML workflow here..."
      />

      <div className="panel__header">
        <h2>📦 Inputs (YAML)</h2>
      </div>
      <YamlCodeEditor
        value={inputs}
        onChange={onInputsChange}
        placeholder={'items:\n  - alpha\nmode: standard'}
        className="editor editor--inputs"
      />

      <button className="run-btn" onClick={onRun} disabled={loading}>
        {loading ? '⏳ Streaming...' : '▶ Execute Workflow'}
      </button>
    </div>
  )
}


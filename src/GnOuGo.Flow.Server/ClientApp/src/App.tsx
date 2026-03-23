// ── App — thin shell: header + tab routing ──

import { useState } from 'react'
import './App.scss'
import { FlowEditor } from './editor'
import './editor/FlowEditor.scss'
import './YamlCodeEditor.scss'

import type { TabId } from './types'
import { EXAMPLE_WORKFLOW, EXAMPLE_INPUTS } from './constants'
import { useWorkflowStream } from './hooks/useWorkflowStream'
import { WorkflowInputPanel, TelemetryPanel } from './components'

export default function App() {
  const [workflow, setWorkflow] = useState(EXAMPLE_WORKFLOW)
  const [inputs, setInputs] = useState(EXAMPLE_INPUTS)
  const [activeTab, setActiveTab] = useState<TabId>('editor')

  const stream = useWorkflowStream()

  const handleRun = () => stream.run(workflow, inputs)

  return (
    <div className="app">
      <header className="header">
        <h1>⚡ GnOuGo.Flow</h1>
        <nav className="header__tabs">
          <button
            className={`header__tab ${activeTab === 'editor' ? 'header__tab--active' : ''}`}
            onClick={() => setActiveTab('editor')}
          >
            🎨 Editor
          </button>
          <button
            className={`header__tab ${activeTab === 'runner' ? 'header__tab--active' : ''}`}
            onClick={() => setActiveTab('runner')}
          >
            ▶ Runner
          </button>
        </nav>
      </header>

      {activeTab === 'editor' && (
        <FlowEditor yamlValue={workflow} onYamlChange={setWorkflow} />
      )}

      {activeTab === 'runner' && (
        <div className="panels">
          <WorkflowInputPanel
            workflow={workflow}
            onWorkflowChange={setWorkflow}
            inputs={inputs}
            onInputsChange={setInputs}
            loading={stream.loading}
            onRun={handleRun}
          />
          <TelemetryPanel
            loading={stream.loading}
            error={stream.error}
            result={stream.result}
            summary={stream.summary}
            workflowStarted={stream.workflowStarted}
            workflowCompleted={stream.workflowCompleted}
            orderedSteps={stream.orderedSteps}
            allThinkingMessages={stream.allThinkingMessages}
          />
        </div>
      )}
    </div>
  )
}


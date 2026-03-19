import { useMemo, useState } from 'react'
import yaml from 'js-yaml'
import './App.scss'
import { FlowEditor } from './editor'
import './editor/FlowEditor.scss'
import { YamlCodeEditor } from './YamlCodeEditor'
import './YamlCodeEditor.scss'

type TabId = 'runner' | 'editor'

type StreamEventType =
  | 'workflow.started'
  | 'workflow.summary'
  | 'workflow.completed'
  | 'workflow.result'
  | 'step.started'
  | 'step.event'
  | 'step.completed'

interface StepResult {
  stepId: string
  stepType: string
  status: string
  durationMs: number
  error?: string
}

interface WorkflowResult {
  success: boolean
  outputs?: unknown
  error?: { code: string; message: string; retryable: boolean }
  steps: StepResult[]
}

interface WorkflowUsageSummary {
  inputTokens: number
  outputTokens: number
  totalTokens: number
  estimatedCostUsd?: number | null
  tokenizedStepCount: number
  pricedStepCount: number
  models: string[]
}

interface StepUsageSummary {
  model?: string | null
  system?: string | null
  inputTokens?: number | null
  outputTokens?: number | null
  totalTokens?: number | null
  estimatedCostUsd?: number | null
  finishReason?: string | null
}

interface WorkflowStartedStreamData {
  workflowName: string
  documentName?: string | null
  inputs?: unknown
}

interface WorkflowSummaryStreamData {
  summary: WorkflowUsageSummary
}

interface WorkflowCompletedStreamData {
  success: boolean
  stepsExecuted: number
  durationMs: number
  errorCode?: string | null
  errorMessage?: string | null
  summary: WorkflowUsageSummary
}

interface StepStartedStreamData {
  stepId: string
  stepType: string
  callDepth: number
  input?: unknown
}

interface StepTelemetryEventStreamData {
  stepId: string
  stepType: string
  callDepth: number
  name: string
  attributes: Record<string, unknown>
  contentText?: string | null
  contentJson?: unknown
}

interface StepCompletedStreamData {
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

interface WorkflowResultStreamData {
  response: WorkflowResult
  summary?: WorkflowUsageSummary
}

interface StreamEnvelope<T = unknown> {
  type: StreamEventType
  timestamp: string
  data: T
}

interface LiveStep {
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

interface ThinkingMessage {
  message: string
  level: string
  timestamp: string
}

// ── Pre-filled example workflow ──
const EXAMPLE_WORKFLOW = `dsl: 1
name: demo-workflow
meta:
  description: A comprehensive demo with variables, templates, LLM, MCP and loops

workflows:
  main:
    inputs:
      items:
        type: array
        required: true
      mode:
        type: string
        default: "standard"

    steps:
      - id: config
        type: set
        input:
          item_count: "\${len(data.inputs.items)}"
          is_fast: '\${data.inputs.mode == "fast"}'

      - id: think_start
        type: emit
        input:
          message: "Starting workflow — processing \${data.steps.config.item_count} items in \${data.inputs.mode} mode"
          level: info

      - id: greet
        type: template.render
        input:
          engine: mustache
          template: "Processing {{count}} items in {{mode}} mode"
          data:
            count: "\${data.steps.config.item_count}"
            mode: "\${data.inputs.mode}"
          mode: text

      - id: think_loop
        type: emit
        input:
          message: "Processing items in parallel…"
          level: thinking

      - id: process_items
        type: loop.parallel
        input:
          items: "\${data.inputs.items}"
          max_concurrency: 2
        steps:
          - id: transform
            type: template.render
            input:
              engine: mustache
              template: "Processed: {{value}}"
              data:
                value: "\${data.item}"
              mode: text

      - id: think_llm
        type: emit
        input:
          message: "Asking LLM to summarize \${data.steps.config.item_count} results…"
          level: thinking

      - id: summarize
        type: llm.call
        input:
          model: gpt-4
          prompt: "Summarize these results: \${json(data.steps.process_items)}"

      - id: decide
        type: switch
        cases:
          - when: "\${data.steps.config.is_fast}"
            steps:
              - id: fast_msg
                type: template.render
                input:
                  engine: mustache
                  template: "Fast mode: skipping extra steps"
                  data: {}
                  mode: text
        default:
          - id: standard_msg
            type: template.render
            input:
              engine: mustache
              template: "Standard mode: full processing complete"
              data: {}
              mode: text

      - id: discover
        type: mcp.list
        input:
          server: demo
          include:
            - tools
            - prompts

      - id: think_mcp
        type: emit
        input:
          message: "Fetching weather data from MCP server…"
          level: thinking

      - id: weather
        type: mcp.call
        input:
          server: demo
          method: get_weather
          request:
            location: Paris

      - id: think_done
        type: emit
        input:
          message: "Workflow complete ✓"
          level: progress

    outputs:
      greeting: "\${data.steps.greet.text}"
      summary: "\${data.steps.summarize.text}"
      tool_count: "\${len(data.steps.discover.tools)}"
      weather: "\${json(data.steps.weather)}"
`

const EXAMPLE_INPUTS = `items:
  - alpha
  - beta
  - gamma
  - delta
mode: standard
`

function parseInputsYamlToJsonString(inputText: string): string | undefined {
  const trimmed = inputText.trim()
  if (!trimmed) return undefined

  const parsed = yaml.load(trimmed)
  return JSON.stringify(parsed ?? null)
}

function formatValueAsYaml(value: unknown): string {
  if (typeof value === 'string') return value
  return yaml.dump(value, {
    noRefs: true,
    lineWidth: -1,
    sortKeys: false
  })
}

function formatCurrencyUsd(value?: number | null): string {
  if (value === null || value === undefined) return '—'
  if (value === 0) return '$0.000000'
  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: value < 0.01 ? 6 : 2,
    maximumFractionDigits: value < 0.01 ? 6 : 4
  }).format(value)
}

function makeStepKey(stepId: string, callDepth: number): string {
  return `${callDepth}:${stepId}`
}

function readNdjsonLines(chunk: string): string[] {
  return chunk.split(/\r?\n/).filter(line => line.trim().length > 0)
}

export default function App() {
  const [workflow, setWorkflow] = useState(EXAMPLE_WORKFLOW)
  const [inputs, setInputs] = useState(EXAMPLE_INPUTS)
  const [result, setResult] = useState<WorkflowResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [activeTab, setActiveTab] = useState<TabId>('editor')
  const [summary, setSummary] = useState<WorkflowUsageSummary | null>(null)
  const [workflowStarted, setWorkflowStarted] = useState<WorkflowStartedStreamData | null>(null)
  const [workflowCompleted, setWorkflowCompleted] = useState<WorkflowCompletedStreamData | null>(null)
  const [liveSteps, setLiveSteps] = useState<Record<string, LiveStep>>({})
  const [showStepDetails, setShowStepDetails] = useState(false)
  const [allThinkingMessages, setAllThinkingMessages] = useState<ThinkingMessage[]>([])

  const orderedSteps = useMemo(
    () => Object.values(liveSteps).sort((a, b) => a.order - b.order),
    [liveSteps]
  )

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

  const updateLiveStep = (
    stepKey: string,
    updater: (current: LiveStep | undefined, stepCount: number) => LiveStep
  ) => {
    setLiveSteps(prev => {
      const next = updater(prev[stepKey], Object.keys(prev).length)
      return { ...prev, [stepKey]: next }
    })
  }

  const handleStreamEvent = (event: StreamEnvelope) => {
    switch (event.type) {
      case 'workflow.started': {
        setWorkflowStarted(event.data as WorkflowStartedStreamData)
        return
      }
      case 'workflow.summary': {
        setSummary((event.data as WorkflowSummaryStreamData).summary)
        return
      }
      case 'workflow.completed': {
        const data = event.data as WorkflowCompletedStreamData
        setWorkflowCompleted(data)
        setSummary(data.summary)
        return
      }
      case 'workflow.result': {
        const data = event.data as WorkflowResultStreamData
        setResult(data.response)
        if (data.summary) setSummary(data.summary)
        return
      }
      case 'step.started': {
        const data = event.data as StepStartedStreamData
        const stepKey = makeStepKey(data.stepId, data.callDepth)
        updateLiveStep(stepKey, (current, stepCount) => ({
          key: stepKey,
          order: current?.order ?? stepCount,
          stepId: data.stepId,
          stepType: data.stepType,
          callDepth: data.callDepth,
          status: 'Running',
          durationMs: current?.durationMs,
          error: current?.error,
          input: data.input ?? current?.input,
          output: current?.output,
          prompt: current?.prompt,
          completion: current?.completion,
          usage: current?.usage,
          attributes: current?.attributes,
          telemetryEvents: current?.telemetryEvents ?? [],
          thinkingMessages: current?.thinkingMessages ?? []
        }))
        return
      }
      case 'step.event': {
        const data = event.data as StepTelemetryEventStreamData
        const stepKey = makeStepKey(data.stepId, data.callDepth)

        // ── Extract thinking messages ──
        if (data.name === 'gnougo-flow.step.thinking') {
          const msg: ThinkingMessage = {
            message: (data.attributes?.['gnougo-flow.thinking.message'] as string) ?? '',
            level: (data.attributes?.['gnougo-flow.thinking.level'] as string) ?? 'thinking',
            timestamp: event.timestamp
          }
          setAllThinkingMessages(prev => [...prev, msg])
          updateLiveStep(stepKey, (current, stepCount) => ({
            key: stepKey,
            order: current?.order ?? stepCount,
            stepId: data.stepId,
            stepType: data.stepType,
            callDepth: data.callDepth,
            status: current?.status ?? 'Running',
            durationMs: current?.durationMs,
            error: current?.error,
            input: current?.input,
            output: current?.output,
            prompt: current?.prompt,
            completion: current?.completion,
            usage: current?.usage,
            attributes: current?.attributes,
            telemetryEvents: [...(current?.telemetryEvents ?? []), {
              name: data.name,
              contentText: data.contentText,
              contentJson: data.contentJson
            }],
            thinkingMessages: [...(current?.thinkingMessages ?? []), msg]
          }))
          return
        }

        updateLiveStep(stepKey, (current, stepCount) => {
          const next: LiveStep = {
            key: stepKey,
            order: current?.order ?? stepCount,
            stepId: data.stepId,
            stepType: data.stepType,
            callDepth: data.callDepth,
            status: current?.status ?? 'Running',
            durationMs: current?.durationMs,
            error: current?.error,
            input: current?.input,
            output: current?.output,
            prompt: current?.prompt,
            completion: current?.completion,
            usage: current?.usage,
            attributes: current?.attributes,
            telemetryEvents: [...(current?.telemetryEvents ?? []), {
              name: data.name,
              contentText: data.contentText,
              contentJson: data.contentJson
            }],
            thinkingMessages: current?.thinkingMessages ?? []
          }

          if (data.name === 'gnougo-flow.step.input' && data.contentJson !== undefined)
            next.input = data.contentJson
          if (data.name === 'gnougo-flow.step.output' && data.contentJson !== undefined)
            next.output = data.contentJson
          if (data.name === 'gen_ai.content.prompt' && data.contentText)
            next.prompt = data.contentText
          if (data.name === 'gen_ai.content.completion' && data.contentText)
            next.completion = data.contentText

          return next
        })
        return
      }
      case 'step.completed': {
        const data = event.data as StepCompletedStreamData
        const stepKey = makeStepKey(data.stepId, data.callDepth)
        updateLiveStep(stepKey, (current, stepCount) => ({
          key: stepKey,
          order: current?.order ?? stepCount,
          stepId: data.stepId,
          stepType: data.stepType,
          callDepth: data.callDepth,
          status: data.status,
          durationMs: data.durationMs,
          error: data.errorMessage ?? current?.error,
          input: current?.input,
          output: data.output ?? current?.output,
          prompt: current?.prompt,
          completion: current?.completion,
          usage: data.usage,
          attributes: data.attributes,
          telemetryEvents: current?.telemetryEvents ?? [],
          thinkingMessages: current?.thinkingMessages ?? []
        }))
      }
    }
  }

  const run = async () => {
    setLoading(true)
    setError(null)
    setResult(null)
    setSummary(null)
    setWorkflowStarted(null)
    setWorkflowCompleted(null)
    setLiveSteps({})
    setAllThinkingMessages([])

    try {
      let normalizedInputs: string | undefined
      try {
        normalizedInputs = parseInputsYamlToJsonString(inputs)
      } catch (e: unknown) {
        setError(e instanceof Error ? `Invalid YAML inputs: ${e.message}` : 'Invalid YAML inputs')
        return
      }

      const response = await fetch('/api/workflow/run/stream', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ workflow, inputs: normalizedInputs })
      })

      if (!response.ok) {
        const err = await response.json().catch(() => ({ error: response.statusText }))
        setError(err.error || err.detail || `HTTP ${response.status}`)
        return
      }

      const reader = response.body?.getReader()
      if (!reader) {
        setError('Streaming HTTP non supporté par ce navigateur.')
        return
      }

      const decoder = new TextDecoder()
      let buffer = ''

      while (true) {
        const { done, value } = await reader.read()
        buffer += decoder.decode(value ?? new Uint8Array(), { stream: !done })

        const parts = buffer.split(/\r?\n/)
        buffer = parts.pop() ?? ''

        for (const line of parts) {
          const trimmed = line.trim()
          if (!trimmed) continue
          handleStreamEvent(JSON.parse(trimmed) as StreamEnvelope)
        }

        if (done) {
          for (const line of readNdjsonLines(buffer))
            handleStreamEvent(JSON.parse(line) as StreamEnvelope)
          break
        }
      }
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Network error')
    } finally {
      setLoading(false)
    }
  }

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
          <div className="panel panel--input">
            <div className="panel__header">
              <h2>📝 Workflow</h2>
            </div>
            <YamlCodeEditor
              value={workflow}
              onChange={setWorkflow}
              placeholder="Paste YAML workflow here..."
            />

            <div className="panel__header">
              <h2>📦 Inputs (YAML)</h2>
            </div>
            <YamlCodeEditor
              value={inputs}
              onChange={setInputs}
              placeholder={'items:\n  - alpha\nmode: standard'}
              className="editor editor--inputs"
            />

            <button className="run-btn" onClick={run} disabled={loading}>
              {loading ? '⏳ Streaming...' : '▶ Execute Workflow'}
            </button>
          </div>

          <div className="panel panel--output">
            <div className="panel__header">
              <h2>📡 Live Telemetry</h2>
              <div className="panel__header-actions">
                <label className="toggle-label">
                  <input type="checkbox" checked={showStepDetails} onChange={e => setShowStepDetails(e.target.checked)} />
                  Show I/O details
                </label>
                <span className={`stream-state stream-state--${loading ? 'running' : result ? (result.success ? 'success' : 'failure') : 'idle'}`}>
                  {loading ? 'Streaming' : result ? (result.success ? 'Completed' : 'Failed') : 'Idle'}
                </span>
              </div>
            </div>

            {allThinkingMessages.length > 0 && (
              <div className="thinking-feed">
                {allThinkingMessages.map((msg, i) => (
                  <div key={i} className={`thinking-msg thinking-msg--${msg.level}`}>
                    <span className="thinking-msg__dot" />
                    <span className="thinking-msg__text">{msg.message}</span>
                  </div>
                ))}
              </div>
            )}

            {error && (
              <div className="error-box">
                <strong>Error:</strong> {error}
              </div>
            )}

            {(workflowStarted || summary || workflowCompleted) && (
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
                    <span className="telemetry-summary__label">Coût estimé</span>
                    <strong>{formatCurrencyUsd(summary?.estimatedCostUsd)}</strong>
                    <span>{summary?.pricedStepCount ?? 0} step(s) tarifés</span>
                  </div>
                  <div className="telemetry-summary__card">
                    <span className="telemetry-summary__label">Modèles</span>
                    <strong>{summary?.models?.length ? summary.models.join(', ') : '—'}</strong>
                    <span>{summary?.tokenizedStepCount ?? 0} step(s) avec usage</span>
                  </div>
                </div>

                {workflowCompleted && (
                  <div className={`status-badge ${workflowCompleted.success ? 'status-badge--success' : 'status-badge--failure'}`}>
                    {workflowCompleted.success ? '✅ Success' : '❌ Failed'}
                    {' — '}
                    {workflowCompleted.stepsExecuted} step(s), {workflowCompleted.durationMs.toFixed(1)}ms
                    {workflowCompleted.errorMessage && ` — ${workflowCompleted.errorMessage}`}
                  </div>
                )}
              </div>
            )}

            {orderedSteps.length > 0 && (
              <div className="steps-section">
                <h3>Steps ({orderedSteps.length})</h3>
                <div className="step-cards">
                  {orderedSteps.map(step => (
                    <div key={step.key} className={`step-card step-card--${step.status.toLowerCase()}`}>
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

                      {step.error && (
                        <div className="step-card__error">
                          {step.error}
                        </div>
                      )}

                      {step.thinkingMessages.length > 0 && (
                        <div className="thinking-feed thinking-feed--inline">
                          {step.thinkingMessages.map((msg, i) => (
                            <div key={i} className={`thinking-msg thinking-msg--${msg.level}`}>
                              <span className="thinking-msg__dot" />
                              <span className="thinking-msg__text">{msg.message}</span>
                            </div>
                          ))}
                        </div>
                      )}

                      {showStepDetails && step.input !== undefined && (
                        <div className="step-card__section">
                          <h4>Input</h4>
                          <YamlCodeEditor value={formatValueAsYaml(step.input)} onChange={() => {}} readOnly className="editor editor--readonly" />
                        </div>
                      )}

                      {showStepDetails && step.prompt && (
                        <div className="step-card__section">
                          <h4>Prompt</h4>
                          <pre className="json-output">{step.prompt}</pre>
                        </div>
                      )}

                      {showStepDetails && step.completion && (
                        <div className="step-card__section">
                          <h4>Completion</h4>
                          <pre className="json-output">{step.completion}</pre>
                        </div>
                      )}

                      {showStepDetails && step.output !== undefined && (
                        <div className="step-card__section">
                          <h4>Output</h4>
                          <YamlCodeEditor value={formatValueAsYaml(step.output)} onChange={() => {}} readOnly className="editor editor--readonly" />
                        </div>
                      )}
                    </div>
                  ))}
                </div>
              </div>
            )}

            {result?.outputs !== undefined && result.outputs !== null && outputYaml && (
              <div className="outputs-section">
                <h3>Final Outputs</h3>
                <YamlCodeEditor
                  value={outputYaml}
                  onChange={() => {}}
                  readOnly
                  className="editor"
                />
              </div>
            )}

            {!error && !loading && !workflowStarted && orderedSteps.length === 0 && !result && (
              <div className="placeholder">
                Click <strong>Execute Workflow</strong> to stream telemetry and debug each workflow node live.
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  )
}



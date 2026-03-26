// ── useWorkflowStream — manages workflow execution state & SSE streaming ──

import { useCallback, useMemo, useState } from 'react'
import type {
  LiveStep,
  PendingHumanInput,
  StreamEnvelope,
  ThinkingMessage,
  WorkflowCompletedStreamData,
  WorkflowResult,
  WorkflowResultStreamData,
  WorkflowStartedStreamData,
  WorkflowSummaryStreamData,
  WorkflowUsageSummary,
  StepStartedStreamData,
  StepTelemetryEventStreamData,
  StepCompletedStreamData,
} from '../types'
import { makeStepKey, parseInputsYamlToJsonString, readNdjsonLines } from '../utils'

export interface WorkflowStreamState {
  result: WorkflowResult | null
  error: string | null
  loading: boolean
  summary: WorkflowUsageSummary | null
  workflowStarted: WorkflowStartedStreamData | null
  workflowCompleted: WorkflowCompletedStreamData | null
  orderedSteps: LiveStep[]
  allThinkingMessages: ThinkingMessage[]
  pendingHumanInput: PendingHumanInput | null
  submitHumanInput: (data: unknown) => Promise<void>
  run: (workflow: string, inputs: string) => Promise<void>
}

export function useWorkflowStream(): WorkflowStreamState {
  const [result, setResult] = useState<WorkflowResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [summary, setSummary] = useState<WorkflowUsageSummary | null>(null)
  const [workflowStarted, setWorkflowStarted] = useState<WorkflowStartedStreamData | null>(null)
  const [workflowCompleted, setWorkflowCompleted] = useState<WorkflowCompletedStreamData | null>(null)
  const [liveSteps, setLiveSteps] = useState<Record<string, LiveStep>>({})
  const [allThinkingMessages, setAllThinkingMessages] = useState<ThinkingMessage[]>([])
  const [pendingHumanInput, setPendingHumanInput] = useState<PendingHumanInput | null>(null)

  const orderedSteps = useMemo(
    () => Object.values(liveSteps).sort((a, b) => a.order - b.order),
    [liveSteps],
  )

  const updateLiveStep = useCallback(
    (stepKey: string, updater: (current: LiveStep | undefined, stepCount: number) => LiveStep) => {
      setLiveSteps(prev => {
        const next = updater(prev[stepKey], Object.keys(prev).length)
        return { ...prev, [stepKey]: next }
      })
    },
    [],
  )

  const handleStreamEvent = useCallback(
    (event: StreamEnvelope) => {
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
            thinkingMessages: current?.thinkingMessages ?? [],
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
              timestamp: event.timestamp,
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
              telemetryEvents: [
                ...(current?.telemetryEvents ?? []),
                { name: data.name, contentText: data.contentText, contentJson: data.contentJson },
              ],
              thinkingMessages: [...(current?.thinkingMessages ?? []), msg],
            }))
            return
          }

          // ── Detect human-input request ──
          if (data.name === 'gnougo-flow.step.waiting_for_human') {
            try {
              const raw = data.attributes?.['gnougo-flow.human.request'] as string
              if (raw) {
                const parsed = JSON.parse(raw)
                const req: PendingHumanInput = {
                  runId: parsed.run_id ?? parsed.runId ?? '',
                  stepId: parsed.step_id ?? parsed.stepId ?? '',
                  prompt: parsed.prompt ?? '',
                  choices: parsed.choices,
                  fields: parsed.fields,
                  context: parsed.context,
                  timeout_ms: parsed.timeout_ms,
                  requestedAt: event.timestamp,
                }
                setPendingHumanInput(req)
              }
            } catch { /* ignore parse errors */ }
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
              telemetryEvents: [
                ...(current?.telemetryEvents ?? []),
                { name: data.name, contentText: data.contentText, contentJson: data.contentJson },
              ],
              thinkingMessages: current?.thinkingMessages ?? [],
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
          // Clear pending human input if this step was the one waiting
          setPendingHumanInput(prev =>
            prev && prev.stepId === data.stepId ? null : prev,
          )
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
            thinkingMessages: current?.thinkingMessages ?? [],
          }))
        }
      }
    },
    [updateLiveStep],
  )

  const run = useCallback(
    async (workflow: string, inputs: string) => {
      setLoading(true)
      setError(null)
      setResult(null)
      setSummary(null)
      setWorkflowStarted(null)
      setWorkflowCompleted(null)
      setLiveSteps({})
      setAllThinkingMessages([])
      setPendingHumanInput(null)

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
          body: JSON.stringify({ workflow, inputs: normalizedInputs }),
        })

        if (!response.ok) {
          const err = await response.json().catch(() => ({ error: response.statusText }))
          setError(err.error || err.detail || `HTTP ${response.status}`)
          return
        }

        const reader = response.body?.getReader()
        if (!reader) {
          setError('Streaming HTTP not supported by this browser.')
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
    },
    [handleStreamEvent],
  )

  const submitHumanInput = useCallback(
    async (data: unknown) => {
      if (!pendingHumanInput) return
      const { runId, stepId } = pendingHumanInput
      // Clear immediately so it doesn't race with the next waiting_for_human event
      // that may arrive on the SSE stream before the fetch response returns.
      setPendingHumanInput(null)
      try {
        await fetch(`/api/workflow/human-input/${runId}/${stepId}`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(data),
        })
      } catch (e) {
        console.error('Failed to submit human input', e)
      }
    },
    [pendingHumanInput],
  )

  return {
    result,
    error,
    loading,
    summary,
    workflowStarted,
    workflowCompleted,
    orderedSteps,
    allThinkingMessages,
    pendingHumanInput,
    submitHumanInput,
    run,
  }
}


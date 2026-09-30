import { useState } from 'react'
import { HumanInputForm } from './HumanInputForm'
import type { PendingHumanInput } from '../types'

type Invocation = {
  id: string; recovery: number; stepType: string; status: string; isFinalization: boolean
  dispatchedAt?: string; completedAt?: string; resolvedInput?: Record<string, unknown>
  output?: Record<string, unknown>; observation?: Record<string, unknown>; error?: { code: string; message: string }
}
type Run = {
  runId: string; workflowName: string; revision: number; status: string; updatedAt: string
  stepsStarted: number; finalizationStepsStarted: number; finalizationStarted: boolean; finalizationCompleted: boolean
  cancelRequested: boolean; limits: { maxTotalStepsExecuted: number; maxFinalizationSteps: number }
  invocations: Record<string, Invocation>; modelUsage?: unknown
}
const json = (value: unknown) => JSON.stringify(value, null, 2)

export function RunJournal({ initialTenant }: { initialTenant: string }) {
  const [tenant, setTenant] = useState(initialTenant)
  const [runs, setRuns] = useState<Run[]>([])
  const [selected, setSelected] = useState<Run | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [reason, setReason] = useState('')
  const base = `/api/tenants/${encodeURIComponent(tenant)}/runs`
  async function request(path: string, body?: unknown) {
    const response = await fetch(path, body === undefined ? undefined : {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: json(body),
    })
    if (!response.ok) throw new Error((await response.text()) || `Request failed (${response.status})`)
    return response.status === 204 ? null : response.json()
  }
  async function inspect(id?: string) {
    setError('')
    try { if (id) setSelected(await request(`${base}/${encodeURIComponent(id)}`)); else { setRuns(await request(base)); setSelected(null) } }
    catch (e) { setError(e instanceof Error ? e.message : 'Could not load executions') }
  }
  async function command(action: string, invocationId?: string, stoppedReason?: string) {
    if (!selected) return
    const inspected = selected
    setBusy(true); setError('')
    try { setSelected(await request(`${base}/${encodeURIComponent(inspected.runId)}/${action}`, {
      expectedRevision: inspected.revision, invocationId, confirmedStoppedReason: stoppedReason,
    })) } catch (e) { setError(e instanceof Error ? e.message : 'Execution command failed') }
    finally { setBusy(false) }
  }
  async function submit(invocationId: string, response: unknown) {
    if (!selected) return
    try {
      setError('')
      await request(`${base}/${encodeURIComponent(selected.runId)}/human-input`, { expectedRevision: selected.revision, invocationId, response })
      await inspect(selected.runId)
    } catch (e) { setError(e instanceof Error ? e.message : 'Response could not be saved') }
  }
  return <main className="run-journal" style={{ padding: '1.5rem', maxWidth: 1200, margin: 'auto' }}>
    <h2>Execution journal</h2>
    <label>Tenant <input value={tenant} onChange={e => { setTenant(e.target.value); setSelected(null); setRuns([]) }} /></label>{' '}
    <button onClick={() => void inspect()}>List executions</button>{' '}
    {selected && <button onClick={() => void inspect(selected.runId)}>Refresh execution</button>}
    {error && <p role="alert">{error}</p>}
    {busy && <p role="status">Execution command is running. You can refresh the journal or request cancellation.</p>}
    {selected ? <>
      <h3>{selected.workflowName} · {selected.status}</h3>
      <p><code>{selected.runId}</code> · Revision {selected.revision}</p>
      <p>{selected.stepsStarted}/{selected.limits.maxTotalStepsExecuted} steps · {selected.finalizationStepsStarted}/{selected.limits.maxFinalizationSteps} cleanup steps</p>
      <p>Cleanup: {selected.finalizationCompleted ? 'completed' : selected.finalizationStarted ? 'in progress' : 'pending'}</p>
      <button disabled={busy} onClick={() => void command('resume')}>Resume this revision</button>{' '}
      <button disabled={selected.cancelRequested} onClick={() => void command('cancel')}>Cancel this revision</button>
      {selected.modelUsage && <details><summary>Model usage</summary><pre>{json(selected.modelUsage)}</pre></details>}
      {Object.values(selected.invocations).map(invocation => <section key={invocation.id} style={{ borderTop: '1px solid #888', marginTop: '1rem', paddingTop: '1rem' }}>
        <h4>{invocation.stepType} · {invocation.status}{invocation.isFinalization ? ' · cleanup' : ''}</h4>
        <code>{invocation.id}</code>
        {invocation.stepType === 'agent.run' && <>
          <p>{String(invocation.resolvedInput?.objective ?? '')}</p>
          <details><summary>Approved scope and budgets</summary><pre>{json(invocation.resolvedInput)}</pre></details>
          <details open><summary>Evidence, verification and usage</summary><pre>{json(invocation.output ?? invocation.observation)}</pre></details>
        </>}
        {invocation.error && <p>{invocation.error.code}: {invocation.error.message}</p>}
        {invocation.recovery === 0 && invocation.dispatchedAt && !invocation.completedAt && <>
          <p>The external outcome is unresolved. Execution and cleanup wait for reconciliation.</p>
          {invocation.stepType === 'agent.run' && <button disabled={busy} onClick={() => void command('reconcile', invocation.id)}>Inspect agent receipt</button>}
          <label>Confirmation that work has stopped, with an audit reason <input value={reason} onChange={e => setReason(e.target.value)} /></label>
          <button disabled={busy || !reason.trim()} onClick={() => void command('reconcile', invocation.id, reason)}>Record confirmed stop as failure</button>
        </>}
        {invocation.status === 'waiting_for_human' && <>
          <HumanInputForm pending={{ ...invocation.resolvedInput, runId: selected.runId, stepId: invocation.id, requestedAt: invocation.dispatchedAt } as PendingHumanInput}
            onSubmit={response => void submit(invocation.id, response)} />
        </>}
        <details><summary>Completed output</summary><pre>{json(invocation.output)}</pre></details>
      </section>)}
    </> : <table><thead><tr><th>Workflow</th><th>Status</th><th>Revision</th><th>Updated</th></tr></thead><tbody>
      {runs.map(run => <tr key={run.runId}><td><button onClick={() => void inspect(run.runId)}>{run.workflowName}</button><br /><code>{run.runId}</code></td><td>{run.status}</td><td>{run.revision}</td><td>{run.updatedAt}</td></tr>)}
    </tbody></table>}
  </main>
}

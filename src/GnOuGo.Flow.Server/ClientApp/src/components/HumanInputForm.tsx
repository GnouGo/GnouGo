// ── HumanInputForm — renders inline form when workflow awaits human input ──

import { useState, useEffect } from 'react'
import type { PendingHumanInput } from '../types'

interface Props {
  pending: PendingHumanInput
  onSubmit: (data: unknown) => void
}

/** Formats remaining milliseconds as "Xm Ys". */
function formatRemaining(ms: number): string {
  if (ms <= 0) return 'expired'
  const totalSec = Math.ceil(ms / 1000)
  const m = Math.floor(totalSec / 60)
  const s = totalSec % 60
  return m > 0 ? `${m}m ${s}s` : `${s}s`
}

export function HumanInputForm({ pending, onSubmit }: Props) {
  const [values, setValues] = useState<Record<string, string>>(() => {
    const init: Record<string, string> = {}
    if (pending.fields) {
      for (const f of pending.fields) {
        init[f.name] = f.default ?? ''
      }
    }
    return init
  })
  const [freeText, setFreeText] = useState('')
  const [remaining, setRemaining] = useState<number | null>(null)

  // ── Timeout countdown ──
  useEffect(() => {
    if (!pending.timeout_ms || !pending.requestedAt) return
    const deadline = new Date(pending.requestedAt).getTime() + pending.timeout_ms
    const tick = () => setRemaining(Math.max(0, deadline - Date.now()))
    tick()
    const id = setInterval(tick, 1000)
    return () => clearInterval(id)
  }, [pending.timeout_ms, pending.requestedAt])

  const handleChoice = (choice: string) => {
    onSubmit({ response: choice, source: 'browser' })
  }

  const handleFieldSubmit = () => {
    const result: Record<string, unknown> = { source: 'browser' }
    if (pending.fields) {
      for (const f of pending.fields) {
        const v = values[f.name] ?? ''
        if (f.type === 'number') result[f.name] = parseFloat(v) || 0
        else if (f.type === 'boolean') result[f.name] = v === 'true' || v === '1'
        else result[f.name] = v
      }
    }
    onSubmit(result)
  }

  const handleFreeSubmit = () => {
    try {
      onSubmit(JSON.parse(freeText))
    } catch {
      onSubmit({ response: freeText, source: 'browser' })
    }
  }

  const isExpired = remaining !== null && remaining <= 0

  return (
    <div className="human-input-form">
      <div className="human-input-form__header">
        <span className="human-input-form__icon">🙋</span>
        <strong>Human Input Required</strong>
        {remaining !== null && (
          <span
            className={`human-input-form__timer ${isExpired ? 'human-input-form__timer--expired' : ''}`}
            title="Time remaining before timeout"
          >
            ⏱ {formatRemaining(remaining)}
          </span>
        )}
      </div>
      <p className="human-input-form__prompt">{pending.prompt}</p>

      {pending.context != null && (
        <details className="human-input-form__context">
          <summary>📋 Context</summary>
          <pre>{typeof pending.context === 'string' ? pending.context : JSON.stringify(pending.context, null, 2)}</pre>
        </details>
      )}

      {pending.choices && pending.choices.length > 0 && (
        <div className="human-input-form__choices">
          {pending.choices.map(c => (
            <button key={c} className="human-input-form__choice" onClick={() => handleChoice(c)} disabled={isExpired}>
              {c}
            </button>
          ))}
        </div>
      )}

      {pending.fields && pending.fields.length > 0 && (
        <div className="human-input-form__fields">
          {pending.fields.map(f => (
            <div key={f.name} className="human-input-form__field">
              <label>
                {f.description ?? f.name}
                {f.required && <span className="human-input-form__required">*</span>}
              </label>
              {f.options && f.options.length > 0 ? (
                <select
                  value={values[f.name] ?? ''}
                  onChange={e => setValues(prev => ({ ...prev, [f.name]: e.target.value }))}
                  disabled={isExpired}
                >
                  <option value="">-- select --</option>
                  {f.options.map(o => (
                    <option key={o} value={o}>{o}</option>
                  ))}
                </select>
              ) : (
                <input
                  type={f.type === 'number' ? 'number' : 'text'}
                  value={values[f.name] ?? ''}
                  placeholder={f.description ?? f.name}
                  onChange={e => setValues(prev => ({ ...prev, [f.name]: e.target.value }))}
                  disabled={isExpired}
                />
              )}
            </div>
          ))}
          <button className="run-btn" onClick={handleFieldSubmit} disabled={isExpired}>Submit</button>
        </div>
      )}

      {!pending.choices?.length && !pending.fields?.length && (
        <div className="human-input-form__free">
          <input
            type="text"
            value={freeText}
            placeholder="Type your response..."
            onChange={e => setFreeText(e.target.value)}
            onKeyDown={e => e.key === 'Enter' && !isExpired && handleFreeSubmit()}
            disabled={isExpired}
          />
          <button className="run-btn" onClick={handleFreeSubmit} disabled={isExpired}>Send</button>
        </div>
      )}
    </div>
  )
}

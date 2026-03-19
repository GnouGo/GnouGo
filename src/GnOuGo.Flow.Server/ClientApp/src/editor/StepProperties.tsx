/* ─── Step property editor panel (right sidebar) ───────── */
import { useState } from 'react'
import { STEP_TYPE_MAP, STEP_TYPES, CATEGORIES, type FieldDef } from './stepTypes'
import type { ParsedStep, ValidationError } from './yamlGraph'

interface Props {
  step: ParsedStep
  errors: ValidationError[]
  onChange: (field: string, value: unknown) => void
  onDelete: () => void
  onChangeType: (newType: string) => void
}

export function StepProperties({ step, errors, onChange, onDelete, onChangeType }: Props) {
  const typeDef = STEP_TYPE_MAP.get(step.type)
  const [showAdvanced, setShowAdvanced] = useState(false)

  const fieldError = (fieldName: string) =>
    errors.find(e => e.field === fieldName)

  return (
    <div className="step-props">
      <div className="step-props__header">
        <span className="step-props__icon">{typeDef?.icon ?? '❓'}</span>
        <h3>Step Properties</h3>
        <button className="step-props__delete" onClick={onDelete} title="Delete step">🗑️</button>
      </div>

      {/* Core fields */}
      <div className="step-props__field">
        <label>ID</label>
        <input
          type="text"
          value={step.id}
          onChange={e => onChange('id', e.target.value)}
          className={fieldError('id') ? 'has-error' : ''}
          placeholder="step_id"
        />
        {fieldError('id') && <span className="field-error">{fieldError('id')!.message}</span>}
      </div>

      <div className="step-props__field">
        <label>Type</label>
        <select
          value={step.type}
          onChange={e => onChangeType(e.target.value)}
          className={fieldError('type') ? 'has-error' : ''}
        >
          <option value="">-- Select type --</option>
          {CATEGORIES.map(cat => (
            <optgroup key={cat.key} label={`${cat.icon} ${cat.label}`}>
              {STEP_TYPES.filter(t => t.category === cat.key).map(t => (
                <option key={t.type} value={t.type}>
                  {t.icon} {t.label}
                </option>
              ))}
            </optgroup>
          ))}
        </select>
        {fieldError('type') && <span className="field-error">{fieldError('type')!.message}</span>}
        {typeDef && <div className="step-props__desc">{typeDef.description}</div>}
      </div>

      <div className="step-props__field">
        <label>Condition (if)</label>
        <input
          type="text"
          value={step.if ?? ''}
          onChange={e => onChange('if', e.target.value || undefined)}
          placeholder='${data.inputs.enabled}'
        />
      </div>

      {/* Type-specific input fields */}
      {typeDef && typeDef.fields.length > 0 && (
        <div className="step-props__section">
          <h4>Input</h4>
          {typeDef.fields.map(field => (
            <FieldInput
              key={field.name}
              field={field}
              value={step.input?.[field.name]}
              error={fieldError(field.name)}
              onChange={val => onChange(field.name, val)}
            />
          ))}
        </div>
      )}

      {/* Raw input editor for steps with no defined fields (set, sequence, etc.) */}
      {typeDef && typeDef.fields.length === 0 && (
        <div className="step-props__section">
          <h4>Input (JSON)</h4>
          <div className="step-props__field">
            <textarea
              value={step.input ? JSON.stringify(step.input, null, 2) : ''}
              onChange={e => {
                const raw = e.target.value
                if (raw.trim() === '') {
                  onChange('__raw_input__', undefined)
                  return
                }
                try {
                  onChange('__raw_input__', JSON.parse(raw))
                } catch {
                  // keep as-is while user is typing
                }
              }}
              placeholder={'{\n  "key": "value",\n  "count": "${len(data.inputs.items)}"\n}'}
              rows={6}
              spellCheck={false}
              className="step-props__raw-input"
            />
            <div className="step-props__hint">
              Each key becomes a variable accessible via data.steps.{step.id}.key
            </div>
          </div>
        </div>
      )}

      {/* Advanced */}
      <button
        className="step-props__toggle"
        onClick={() => setShowAdvanced(!showAdvanced)}
      >
        {showAdvanced ? '▼' : '▶'} Advanced
      </button>

      {showAdvanced && (
        <div className="step-props__advanced">
          <div className="step-props__field">
            <label>Output alias</label>
            <input
              type="text"
              value={step.output ?? ''}
              onChange={e => onChange('output', e.target.value || undefined)}
              placeholder="my_output"
            />
          </div>

          <div className="step-props__field">
            <label>Retry max</label>
            <input
              type="number"
              value={step.retry?.max ?? ''}
              onChange={e => {
                const v = parseInt(e.target.value)
                onChange('retry', v > 0 ? { ...step.retry, max: v } : undefined)
              }}
              placeholder="1"
              min={1}
            />
          </div>
        </div>
      )}

      {/* Validation summary */}
      {errors.length > 0 && (
        <div className="step-props__errors">
          <h4>⚠️ Validation ({errors.length})</h4>
          {errors.map((e, i) => (
            <div key={i} className={`step-props__error step-props__error--${e.severity}`}>
              <strong>{e.field}:</strong> {e.message}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

/* ── Generic field input ── */
function FieldInput({
  field,
  value,
  error,
  onChange,
}: {
  field: FieldDef
  value: unknown
  error?: ValidationError
  onChange: (val: unknown) => void
}) {
  const strVal = value === undefined || value === null ? '' : typeof value === 'object' ? JSON.stringify(value, null, 2) : String(value)

  switch (field.type) {
    case 'select':
      return (
        <div className="step-props__field">
          <label>{field.label}{field.required && <span className="required">*</span>}</label>
          <select
            value={strVal}
            onChange={e => onChange(e.target.value)}
            className={error ? 'has-error' : ''}
          >
            <option value="">--</option>
            {field.options?.map(o => <option key={o} value={o}>{o}</option>)}
          </select>
          {field.description && <div className="step-props__hint">{field.description}</div>}
          {error && <span className="field-error">{error.message}</span>}
        </div>
      )

    case 'text':
      return (
        <div className="step-props__field">
          <label>{field.label}{field.required && <span className="required">*</span>}</label>
          <textarea
            value={strVal}
            onChange={e => onChange(e.target.value)}
            className={error ? 'has-error' : ''}
            placeholder={field.placeholder}
            rows={3}
          />
          {error && <span className="field-error">{error.message}</span>}
        </div>
      )

    case 'json':
      return (
        <div className="step-props__field">
          <label>{field.label}{field.required && <span className="required">*</span>}</label>
          <textarea
            value={strVal}
            onChange={e => {
              const raw = e.target.value
              try {
                onChange(JSON.parse(raw))
              } catch {
                onChange(raw) // keep as string if invalid JSON
              }
            }}
            className={error ? 'has-error' : ''}
            placeholder={field.placeholder}
            rows={3}
            spellCheck={false}
          />
          {error && <span className="field-error">{error.message}</span>}
        </div>
      )

    case 'number':
      return (
        <div className="step-props__field">
          <label>{field.label}{field.required && <span className="required">*</span>}</label>
          <input
            type="number"
            value={strVal}
            onChange={e => {
              const v = e.target.value
              onChange(v === '' ? undefined : parseFloat(v))
            }}
            className={error ? 'has-error' : ''}
            placeholder={field.placeholder}
          />
          {error && <span className="field-error">{error.message}</span>}
        </div>
      )

    case 'boolean':
      return (
        <div className="step-props__field step-props__field--checkbox">
          <label>
            <input
              type="checkbox"
              checked={!!value}
              onChange={e => onChange(e.target.checked)}
            />
            {field.label}
          </label>
        </div>
      )

    default: // string, expression
      return (
        <div className="step-props__field">
          <label>
            {field.label}
            {field.required && <span className="required">*</span>}
            {field.type === 'expression' && <span className="tag-expr">expr</span>}
          </label>
          <input
            type="text"
            value={strVal}
            onChange={e => onChange(e.target.value)}
            className={error ? 'has-error' : ''}
            placeholder={field.placeholder}
          />
          {error && <span className="field-error">{error.message}</span>}
        </div>
      )
  }
}


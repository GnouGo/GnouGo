/* ─── Step palette — drag to add steps ──────────────────── */
import { STEP_TYPES, CATEGORIES } from './stepTypes'

interface Props {
  onAdd: (stepType: string) => void
}

export function StepPalette({ onAdd }: Props) {
  return (
    <div className="step-palette">
      <h3>➕ Add Step</h3>
      {CATEGORIES.map(cat => (
        <div key={cat.key} className="step-palette__category">
          <div className="step-palette__cat-label">{cat.icon} {cat.label}</div>
          <div className="step-palette__items">
            {STEP_TYPES.filter(t => t.category === cat.key).map(t => (
              <button
                key={t.type}
                className="step-palette__item"
                style={{ borderLeftColor: t.color }}
                onClick={() => onAdd(t.type)}
                title={t.description}
              >
                <span className="step-palette__icon">{t.icon}</span>
                <span className="step-palette__label">{t.label}</span>
              </button>
            ))}
          </div>
        </div>
      ))}
    </div>
  )
}


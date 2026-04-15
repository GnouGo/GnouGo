/* ─── YamlCodeEditor ────────────────────────────────────────────
   Lightweight YAML code editor with line numbers, syntax
   highlighting, and inline parse-error markers.
   No external dependency — just a <textarea> overlaid with
   a highlighted <pre>.
   ────────────────────────────────────────────────────────────── */
import { useRef, useState, useCallback, useEffect, useMemo } from 'react'
import yaml from 'js-yaml'

// ── Types ──────────────────────────────────────────────────────

export interface YamlError {
  line: number       // 1-based
  message: string
}

interface Props {
  value: string
  onChange: (v: string) => void
  placeholder?: string
  className?: string
  /** Extra error lines from workflow execution results */
  errors?: YamlError[]
  readOnly?: boolean
}

// ── YAML tokeniser (line-by-line) ──────────────────────────────

function highlightYamlLine(line: string): string {
  // Already-escaped HTML is NOT expected — we escape ourselves.
  const esc = (s: string) =>
    s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')

  // Full-line comment
  if (/^\s*#/.test(line))
    return `<span class="yce-comment">${esc(line)}</span>`

  // Empty / whitespace-only
  if (!line.trim()) return esc(line)

  let result = ''
  let rest = line

  // Leading whitespace
  const leadMatch = rest.match(/^(\s+)/)
  if (leadMatch) {
    result += esc(leadMatch[1])
    rest = rest.slice(leadMatch[1].length)
  }

  // "- " list marker
  const dashMatch = rest.match(/^(-\s)/)
  if (dashMatch) {
    result += `<span class="yce-punct">${esc(dashMatch[1])}</span>`
    rest = rest.slice(dashMatch[1].length)
  }

  // key: value
  const kvMatch = rest.match(/^([a-zA-Z0-9_][a-zA-Z0-9_.\-/]*)(\s*:\s*)(.*)$/)
  if (kvMatch) {
    const keyClass = kvMatch[1] === 'version' ? 'yce-key yce-key--version' : 'yce-key'
    result += `<span class="${keyClass}">${esc(kvMatch[1])}</span>`
    result += `<span class="yce-punct">${esc(kvMatch[2])}</span>`
    result += highlightValue(kvMatch[3])
    return result
  }

  // Standalone value (in a list, after "- ")
  result += highlightValue(rest)
  return result
}

function highlightValue(val: string): string {
  const esc = (s: string) =>
    s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')

  if (!val) return ''

  // Inline comment at end — split it
  const commentIdx = val.indexOf(' #')
  if (commentIdx > 0) {
    const before = val.slice(0, commentIdx)
    const after = val.slice(commentIdx)
    return highlightValue(before) + `<span class="yce-comment">${esc(after)}</span>`
  }

  const trimmed = val.trim()

  // Quoted strings
  if (/^".*"$/.test(trimmed) || /^'.*'$/.test(trimmed))
    return `<span class="yce-string">${esc(val)}</span>`

  // Block scalars
  if (trimmed === '|' || trimmed === '>' || trimmed === '|-' || trimmed === '>-')
    return `<span class="yce-punct">${esc(val)}</span>`

  // Boolean
  if (/^(true|false|yes|no|on|off)$/i.test(trimmed))
    return `<span class="yce-bool">${esc(val)}</span>`

  // Null
  if (/^(null|~)$/i.test(trimmed))
    return `<span class="yce-null">${esc(val)}</span>`

  // Number
  if (/^-?\d+(\.\d+)?([eE][+-]?\d+)?$/.test(trimmed))
    return `<span class="yce-number">${esc(val)}</span>`

  // Expression ${...}
  if (/\$\{/.test(val)) {
    return esc(val).replace(
      /\$\{[^}]*\}/g,
      m => `<span class="yce-expr">${m}</span>`,
    )
  }

  return esc(val)
}

// ── Component ──────────────────────────────────────────────────

export function YamlCodeEditor({ value, onChange, placeholder, className, errors: externalErrors, readOnly = false }: Props) {
  const textareaRef = useRef<HTMLTextAreaElement>(null)
  const highlightRef = useRef<HTMLPreElement>(null)
  const gutterRef = useRef<HTMLDivElement>(null)
  const [focused, setFocused] = useState(false)

  // ── YAML parse errors ──
  const parseErrors = useMemo<YamlError[]>(() => {
    if (!value.trim()) return []
    try {
      yaml.load(value)
      return []
    } catch (e: unknown) {
      if (e && typeof e === 'object' && 'mark' in e) {
        const mark = (e as { mark?: { line?: number }; message?: string }).mark
        const msg = (e as { message?: string }).message ?? 'YAML parse error'
        return [{ line: (mark?.line ?? 0) + 1, message: msg }]
      }
      return [{ line: 1, message: String(e) }]
    }
  }, [value])

  const allErrors = useMemo(() => {
    const errs = [...parseErrors]
    if (externalErrors) errs.push(...externalErrors)
    return errs
  }, [parseErrors, externalErrors])

  const errorLineSet = useMemo(() => new Set(allErrors.map(e => e.line)), [allErrors])

  // ── Highlighted HTML ──
  const highlightedHtml = useMemo(() => {
    const lines = value.split('\n')
    return lines.map(l => highlightYamlLine(l)).join('\n')
  }, [value])

  // ── Line count ──
  const lineCount = useMemo(() => value.split('\n').length, [value])

  // ── Sync scroll ──
  const syncScroll = useCallback(() => {
    const ta = textareaRef.current
    if (!ta) return
    if (highlightRef.current) {
      highlightRef.current.scrollTop = ta.scrollTop
      highlightRef.current.scrollLeft = ta.scrollLeft
    }
    if (gutterRef.current) {
      gutterRef.current.scrollTop = ta.scrollTop
    }
  }, [])

  // ── Tab key support ──
  const handleKeyDown = useCallback((e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (readOnly) return
    if (e.key === 'Tab') {
      e.preventDefault()
      const ta = e.currentTarget
      const start = ta.selectionStart
      const end = ta.selectionEnd
      const before = value.slice(0, start)
      const after = value.slice(end)
      const newValue = before + '  ' + after
      onChange(newValue)
      // Restore cursor after React re-render
      requestAnimationFrame(() => {
        ta.selectionStart = ta.selectionEnd = start + 2
      })
    }
  }, [readOnly, value, onChange])

  // ── Keep scroll sync on value change ──
  useEffect(() => {
    syncScroll()
  }, [value, syncScroll])

  // ── Error tooltip state ──
  const [tooltip, setTooltip] = useState<{ line: number; x: number; y: number } | null>(null)

  const handleGutterHover = useCallback((e: React.MouseEvent, lineNum: number) => {
    if (errorLineSet.has(lineNum)) {
      const rect = (e.target as HTMLElement).getBoundingClientRect()
      setTooltip({ line: lineNum, x: rect.right + 8, y: rect.top })
    }
  }, [errorLineSet])

  const handleGutterLeave = useCallback(() => setTooltip(null), [])

  return (
    <div className={`yce ${className ?? ''} ${focused ? 'yce--focused' : ''} ${readOnly ? 'yce--readonly' : ''}`}>
      {/* Gutter (line numbers) */}
      <div className="yce__gutter" ref={gutterRef}>
        {Array.from({ length: lineCount }, (_, i) => {
          const ln = i + 1
          const hasErr = errorLineSet.has(ln)
          return (
            <div
              key={ln}
              className={`yce__ln${hasErr ? ' yce__ln--error' : ''}`}
              onMouseEnter={hasErr ? (e) => handleGutterHover(e, ln) : undefined}
              onMouseLeave={hasErr ? handleGutterLeave : undefined}
            >
              {hasErr ? '●' : ln}
            </div>
          )
        })}
      </div>

      {/* Code area */}
      <div className="yce__code">
        {/* Highlighted overlay */}
        <pre
          ref={highlightRef}
          className="yce__highlight"
          aria-hidden
          dangerouslySetInnerHTML={{ __html: highlightedHtml + '\n' }}
        />
        {/* Actual textarea (transparent text, visible caret) */}
        <textarea
          ref={textareaRef}
          className="yce__input"
          value={value}
          onChange={e => onChange(e.target.value)}
          onScroll={syncScroll}
          onKeyDown={handleKeyDown}
          onFocus={() => setFocused(true)}
          onBlur={() => setFocused(false)}
          spellCheck={false}
          autoComplete="off"
          autoCorrect="off"
          autoCapitalize="off"
          placeholder={placeholder}
          readOnly={readOnly}
        />
      </div>

      {/* Error tooltip */}
      {tooltip && (
        <div
          className="yce__tooltip"
          style={{ top: tooltip.y, left: tooltip.x }}
        >
          {allErrors
            .filter(e => e.line === tooltip.line)
            .map((e, i) => <div key={i}>{e.message}</div>)}
        </div>
      )}
    </div>
  )
}

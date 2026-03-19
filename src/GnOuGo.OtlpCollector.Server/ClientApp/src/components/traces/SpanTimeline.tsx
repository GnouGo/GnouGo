import { useState } from 'react';
import type { Span } from '../../types';

interface SpanTimelineProps {
  spans: Span[];
}

/* ---- helpers ---- */

interface FlatSpan {
  span: Span;
  depth: number;
  startMs: number;
  endMs: number;
  durationMs: number;
}

function getSpanDuration(s: Span): { startMs: number; endMs: number; durationMs: number } {
  const startMs = new Date(s.startUtc).getTime();
  let endMs = new Date(s.endUtc).getTime();
  const server = s.durationMs ?? 0;
  if (endMs <= startMs && server > 0) endMs = startMs + server;
  if (endMs <= startMs) endMs = startMs + 1;
  return { startMs, endMs, durationMs: Math.max(0, endMs - startMs) };
}

/** Build a flat ordered list from the parent→child hierarchy */
function buildWaterfall(spans: Span[]): FlatSpan[] {
  const byParent = new Map<string | null, Span[]>();
  for (const s of spans) {
    const key = s.parentSpanId ?? null;
    if (!byParent.has(key)) byParent.set(key, []);
    byParent.get(key)!.push(s);
  }

  // find roots: spans whose parentSpanId is null or not found in span list
  const spanIds = new Set(spans.map(s => s.spanId));
  const roots = spans.filter(s => !s.parentSpanId || !spanIds.has(s.parentSpanId));

  const flat: FlatSpan[] = [];
  const visit = (list: Span[], depth: number) => {
    // sort by start time
    list.sort((a, b) => new Date(a.startUtc).getTime() - new Date(b.startUtc).getTime());
    for (const s of list) {
      const t = getSpanDuration(s);
      flat.push({ span: s, depth, ...t });
      const children = byParent.get(s.spanId);
      if (children) visit(children, depth + 1);
    }
  };
  visit(roots, 0);
  return flat;
}

function fmtDuration(ms: number): string {
  if (ms <= 0) return '< 1ms';
  if (ms < 1) return `${(ms * 1000).toFixed(0)}µs`;
  if (ms < 1000) return `${ms.toFixed(1)}ms`;
  return `${(ms / 1000).toFixed(2)}s`;
}

const SPAN_COLORS: [test: (s: Span) => boolean, color: string][] = [
  [s => !!(s.attributes?.['gen_ai.request.model'] || s.attributes?.['gen_ai.response.model']), '#8b5cf6'],
  [s => (s.name || '').toLowerCase().includes('embed'), '#3b82f6'],
  [s => /search|retrieval|query/.test((s.name || '').toLowerCase()), '#10b981'],
  [s => /ingestion|pipeline/.test((s.name || '').toLowerCase()), '#f59e0b'],
  [s => (s.name || '').toLowerCase().includes('chunk'), '#06b6d4'],
  [s => (s.name || '').toLowerCase().includes('ocr'), '#ec4899'],
  [s => (s.name || '').toLowerCase().includes('extract'), '#14b8a6'],
  [s => /store|upsert/.test((s.name || '').toLowerCase()), '#f97316'],
  [s => /^(GET|POST|PUT|DELETE|PATCH|HEAD|HTTP)/.test(s.name || ''), '#64748b'],
  [s => s.statusCode === 2, '#ef4444'],
];

function spanColor(s: Span): string {
  for (const [test, c] of SPAN_COLORS) if (test(s)) return c;
  return '#6b7280';
}

const KINDS: Record<number, string> = { 0: 'UNSPECIFIED', 1: 'INTERNAL', 2: 'SERVER', 3: 'CLIENT', 4: 'PRODUCER', 5: 'CONSUMER' };
const STATUSES: Record<number, string> = { 0: 'UNSET', 1: 'OK', 2: 'ERROR' };

/* ---- component ---- */

function SpanTimeline({ spans }: SpanTimelineProps) {
  const [selectedId, setSelectedId] = useState<string | null>(null);

  if (!spans || spans.length === 0) return null;

  const flat = buildWaterfall(spans);
  const minTime = Math.min(...flat.map(f => f.startMs));
  const maxTime = Math.max(...flat.map(f => f.endMs));
  const totalMs = Math.max(1, maxTime - minTime);

  const selected = flat.find(f => f.span.spanId === selectedId)?.span ?? null;

  return (
    <div className="span-timeline">
      <div className="span-timeline__header">
        <h3 className="span-timeline__title">Timeline</h3>
        <span className="span-timeline__total">{fmtDuration(totalMs)}</span>
      </div>

      {/* scale */}
      <div className="span-timeline__scale">
        {[0, 25, 50, 75, 100].map(p => (
          <span key={p} className="span-timeline__scale-mark" style={{ left: `${p}%` }}>
            {fmtDuration((p / 100) * totalMs)}
          </span>
        ))}
      </div>

      {/* waterfall rows */}
      <div className="span-timeline__waterfall">
        {flat.map(({ span, depth, startMs, endMs, durationMs: dur }) => {
          const left = ((startMs - minTime) / totalMs) * 100;
          const width = Math.max(0.4, ((endMs - startMs) / totalMs) * 100);
          const isSelected = span.spanId === selectedId;
          const color = spanColor(span);
          const isError = span.statusCode === 2;

          return (
            <div
              key={span.spanId}
              className={`span-timeline__row ${isSelected ? 'span-timeline__row--selected' : ''} ${isError ? 'span-timeline__row--error' : ''}`}
              onClick={() => setSelectedId(isSelected ? null : span.spanId)}
            >
              {/* label side */}
              <div className="span-timeline__label" style={{ paddingLeft: `${depth * 16 + 4}px` }}>
                <span className="span-timeline__dot" style={{ background: color }} />
                <span className="span-timeline__name">{span.name || '(unnamed)'}</span>
                {isError && (
                  <span className="span-timeline__error-badge" title={span.statusMessage || 'Error'}>ERR</span>
                )}
                <span className="span-timeline__dur">{fmtDuration(dur)}</span>
              </div>

              {/* bar side */}
              <div className="span-timeline__bar-track">
                <div
                  className="span-timeline__bar"
                  style={{ left: `${left}%`, width: `${width}%`, background: color }}
                />
              </div>
            </div>
          );
        })}
      </div>

      {/* detail panel */}
      {selected && (
        <div className="span-timeline__details">
          <div className="span-timeline__details-head">
            <strong>{selected.name || '(unnamed)'}</strong>
            <button className="span-timeline__details-close" onClick={() => setSelectedId(null)}>✕</button>
          </div>

          <table className="span-timeline__details-table">
            <tbody>
              <tr><td>Span ID</td><td>{selected.spanId}</td></tr>
              {selected.parentSpanId && <tr><td>Parent</td><td>{selected.parentSpanId}</td></tr>}
              <tr><td>Duration</td><td>{fmtDuration(selected.durationMs)}</td></tr>
              <tr><td>Start</td><td>{new Date(selected.startUtc).toLocaleString(navigator.language)}</td></tr>
              <tr><td>End</td><td>{new Date(selected.endUtc).toLocaleString(navigator.language)}</td></tr>
              <tr><td>Kind</td><td><span className={`badge badge--${(KINDS[selected.kind] ?? 'unknown').toLowerCase()}`}>{KINDS[selected.kind] ?? 'UNKNOWN'}</span></td></tr>
              <tr><td>Status</td><td><span className={`badge badge--${(STATUSES[selected.statusCode] ?? 'unset').toLowerCase()}`}>{STATUSES[selected.statusCode] ?? 'UNSET'}</span></td></tr>
              {selected.statusMessage && <tr><td>Message</td><td>{selected.statusMessage}</td></tr>}
            </tbody>
          </table>

          {selected.resource && Object.keys(selected.resource).length > 0 && (
            <details className="span-timeline__details-section">
              <summary>Resource</summary>
              <pre>{JSON.stringify(selected.resource, null, 2)}</pre>
            </details>
          )}
          {selected.scope && Object.keys(selected.scope).length > 0 && (
            <details className="span-timeline__details-section">
              <summary>Scope</summary>
              <pre>{JSON.stringify(selected.scope, null, 2)}</pre>
            </details>
          )}
          {Object.keys(selected.attributes || {}).length > 0 && (
            <details className="span-timeline__details-section" open>
              <summary>Attributes</summary>
              <AttributeTable attributes={selected.attributes!} />
            </details>
          )}
          {selected.events && selected.events.length > 0 && (
            <details className="span-timeline__details-section">
              <summary>Events ({selected.events.length})</summary>
              {selected.events.map((ev, i) => (
                <div key={i} className="span-timeline__event">
                  <strong>{ev.name}</strong> — <span>{new Date(ev.timeUtc).toLocaleTimeString(navigator.language)}</span>
                  {ev.attributes && Object.keys(ev.attributes).length > 0 && (
                    <AttributeTable attributes={ev.attributes} />
                  )}
                </div>
              ))}
            </details>
          )}
        </div>
      )}
    </div>
  );
}

export default SpanTimeline;

/* ---- Attribute rendering helpers ---- */

const ATTR_VALUE_TRUNCATE = 300;

function AttributeValue({ value }: { value: unknown }) {
  const [expanded, setExpanded] = useState(false);
  const str = typeof value === 'string' ? value : JSON.stringify(value, null, 2);
  const isLong = str.length > ATTR_VALUE_TRUNCATE;

  if (!isLong) return <span className="span-timeline__attr-value">{str}</span>;

  return (
    <span className="span-timeline__attr-value span-timeline__attr-value--long">
      <span>{expanded ? str : str.slice(0, ATTR_VALUE_TRUNCATE) + '…'}</span>
      <button
        className="span-timeline__attr-toggle"
        onClick={() => setExpanded(!expanded)}
      >
        {expanded ? '▲ less' : `▼ more (${str.length.toLocaleString()} chars)`}
      </button>
    </span>
  );
}

function AttributeTable({ attributes }: { attributes: Record<string, unknown> }) {
  const entries = Object.entries(attributes);
  if (entries.length === 0) return null;

  return (
    <table className="span-timeline__attr-table">
      <tbody>
        {entries.map(([key, value]) => (
          <tr key={key}>
            <td className="span-timeline__attr-key">{key}</td>
            <td><AttributeValue value={value} /></td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

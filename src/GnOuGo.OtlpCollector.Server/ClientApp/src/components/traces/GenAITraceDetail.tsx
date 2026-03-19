import { useState, useMemo } from 'react';
import GenAISummaryPanel from './GenAISummaryPanel';
import GenAISpanDetail from './GenAISpanDetail';
import type { TraceGroup, Span } from '../../types';

interface GenAITraceDetailProps {
  trace: TraceGroup;
}

/* ---- helpers ---- */

interface FlatSpan {
  span: Span;
  depth: number;
  startMs: number;
  endMs: number;
  durationMs: number;
}


function buildWaterfall(spans: Span[]): FlatSpan[] {
  const byParent = new Map<string | null, Span[]>();
  for (const s of spans) {
    const key = s.parentSpanId ?? null;
    if (!byParent.has(key)) byParent.set(key, []);
    byParent.get(key)!.push(s);
  }
  const spanIds = new Set(spans.map(s => s.spanId));
  const roots = spans.filter(s => !s.parentSpanId || !spanIds.has(s.parentSpanId));

  const flat: FlatSpan[] = [];
  const visit = (list: Span[], depth: number) => {
    list.sort((a, b) => new Date(a.startUtc).getTime() - new Date(b.startUtc).getTime());
    for (const s of list) {
      const startMs = new Date(s.startUtc).getTime();
      let endMs = new Date(s.endUtc).getTime();
      const server = s.durationMs ?? 0;
      if (endMs <= startMs && server > 0) endMs = startMs + server;
      if (endMs <= startMs) endMs = startMs + 1;
      const durationMs = Math.max(0, endMs - startMs);
      flat.push({ span: s, depth, startMs, endMs, durationMs });
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

function getSpanTypeIcon(s: Span): string {
  const attrs = s.attributes || {};
  const name = (s.name || '').toLowerCase();
  if (attrs['gen_ai.request.model'] || attrs['gen_ai.response.model']) return '🤖';
  if (name.includes('embed')) return '🧬';
  if (name.includes('search') || name.includes('retrieval') || name.includes('query')) return '🔍';
  if (name.includes('chunk')) return '✂️';
  if (name.includes('ocr')) return '👁️';
  if (name.includes('extract')) return '📄';
  if (name.includes('store') || name.includes('upsert')) return '💾';
  if (name.includes('ingestion') || name.includes('pipeline')) return '⚙️';
  if (/^(GET|POST|PUT|DELETE|PATCH|HEAD|HTTP)/.test(s.name || '')) return '🌐';
  if (s.statusCode === 2) return '❌';
  return '📌';
}

/** Extract HTTP URL from span attributes (OTel HTTP semantic conventions) */
function getHttpUrl(s: Span): string | null {
  const a = s.attributes || {};
  const full = String(a['url.full'] || a['http.url'] || '');
  if (full) return full;
  const scheme = a['url.scheme'] || a['http.scheme'] || '';
  const host = a['server.address'] || a['net.peer.name'] || a['http.host'] || '';
  const port = a['server.port'] || a['net.peer.port'] || '';
  const target = a['url.path'] || a['http.target'] || '';
  if (host && target) {
    const portStr = port && port !== 443 && port !== 80 ? `:${port}` : '';
    return `${scheme || 'https'}://${host}${portStr}${target}`;
  }
  if (target) return String(target);
  return null;
}

function isHttpSpan(s: Span): boolean {
  return /^(GET|POST|PUT|DELETE|PATCH|HEAD|HTTP)/i.test(s.name || '') ||
    !!(s.attributes?.['http.method'] || s.attributes?.['http.request.method'] || s.attributes?.['url.full'] || s.attributes?.['http.url']);
}

/* ---- component ---- */

function GenAITraceDetail({ trace }: GenAITraceDetailProps) {
  // null = synthèse, string = spanId
  const [selectedSpanId, setSelectedSpanId] = useState<string | null>(null);

  const flat = useMemo(() => buildWaterfall(trace.spans), [trace.spans]);
  const minTime = useMemo(() => Math.min(...flat.map(f => f.startMs)), [flat]);
  const maxTime = useMemo(() => Math.max(...flat.map(f => f.endMs)), [flat]);
  const totalMs = Math.max(1, maxTime - minTime);

  const selectedSpan = selectedSpanId
    ? flat.find(f => f.span.spanId === selectedSpanId)?.span ?? null
    : null;

  return (
    <div className="genai-split">
      {/* ====== LEFT: Timeline sidebar ====== */}
      <div className="genai-split__left">
        <div className="genai-split__left-header">
          <h3 className="genai-split__left-title">Timeline</h3>
          <span className="genai-split__left-count">{flat.length} spans · {fmtDuration(totalMs)}</span>
        </div>

        {/* Synthèse button */}
        <button
          className={`genai-split__synth-btn ${selectedSpanId === null ? 'genai-split__synth-btn--active' : ''}`}
          onClick={() => setSelectedSpanId(null)}
        >
          <span className="genai-split__synth-icon">📊</span>
          <span className="genai-split__synth-label">Synthèse RAG</span>
          <span className="genai-split__synth-hint">Coût · Temps · Tokens</span>
        </button>

        {/* Timeline items */}
        <div className="genai-split__timeline">
          {flat.map(({ span, depth, startMs, endMs, durationMs: dur }) => {
            const left = ((startMs - minTime) / totalMs) * 100;
            const width = Math.max(1, ((endMs - startMs) / totalMs) * 100);
            const isActive = span.spanId === selectedSpanId;
            const color = spanColor(span);
            const icon = getSpanTypeIcon(span);
            const hasGenAI = !!(span.attributes?.['gen_ai.request.model'] || span.attributes?.['gen_ai.response.model'] || span.attributes?.['gen_ai.operation.name']);
            const httpUrl = isHttpSpan(span) ? getHttpUrl(span) : null;
            const httpStatus = span.attributes?.['http.status_code'] || span.attributes?.['http.response.status_code'];
            const isError = span.statusCode === 2;

            return (
              <div
                key={span.spanId}
                className={`genai-split__tl-item ${isActive ? 'genai-split__tl-item--active' : ''} ${hasGenAI ? 'genai-split__tl-item--genai' : ''} ${isError ? 'genai-split__tl-item--error' : ''}`}
                onClick={() => setSelectedSpanId(isActive ? null : span.spanId)}
              >
                <div className="genai-split__tl-label" style={{ paddingLeft: `${depth * 12 + 4}px` }}>
                  <span className="genai-split__tl-icon">{icon}</span>
                  <span className="genai-split__tl-name" title={span.name}>{span.name || '(unnamed)'}</span>
                  {isError && (
                    <span className="genai-split__tl-error-badge" title={span.statusMessage || 'Error'}>ERR</span>
                  )}
                  {httpStatus && (
                    <span className={`genai-split__tl-http-status ${Number(httpStatus) >= 400 ? 'genai-split__tl-http-status--error' : ''}`}>
                      {String(httpStatus)}
                    </span>
                  )}
                  <span className="genai-split__tl-dur">{fmtDuration(dur)}</span>
                </div>
                {httpUrl && (
                  <div className="genai-split__tl-url" style={{ paddingLeft: `${depth * 12 + 24}px` }} title={httpUrl}>
                    {httpUrl}
                  </div>
                )}
                <div className="genai-split__tl-bar-track">
                  <div
                    className="genai-split__tl-bar"
                    style={{ left: `${left}%`, width: `${width}%`, background: color }}
                  />
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {/* ====== RIGHT: Detail panel ====== */}
      <div className="genai-split__right">
        {selectedSpan ? (
          <GenAISpanDetail span={selectedSpan} allSpans={trace.spans} traceId={trace.traceId} />
        ) : (
          <GenAISummaryPanel trace={trace} />
        )}
      </div>
    </div>
  );
}

export default GenAITraceDetail;


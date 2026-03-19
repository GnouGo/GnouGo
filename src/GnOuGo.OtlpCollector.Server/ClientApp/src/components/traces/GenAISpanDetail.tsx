import { useState, useMemo } from 'react';
import SmartContent, { detectType } from './SmartContent';
import type { Span, SpanEvent } from '../../types';

interface GenAISpanDetailProps {
  span: Span;
  allSpans: Span[];
  traceId: string;
}

const KINDS: Record<number, string> = { 0: 'UNSPECIFIED', 1: 'INTERNAL', 2: 'SERVER', 3: 'CLIENT', 4: 'PRODUCER', 5: 'CONSUMER' };
const STATUSES: Record<number, string> = { 0: 'UNSET', 1: 'OK', 2: 'ERROR' };

type Tab = 'io' | 'attributes' | 'events';

function fmt(ms: number): string {
  if (ms <= 0) return '< 1ms';
  if (ms < 1) return `${(ms * 1000).toFixed(0)}µs`;
  if (ms < 1000) return `${ms.toFixed(1)}ms`;
  return `${(ms / 1000).toFixed(2)}s`;
}

function isGenAIAttr(key: string): boolean {
  return key.startsWith('gen_ai.') || key.startsWith('llm.');
}

function isStructuredOrLong(value: unknown): boolean {
  if (typeof value !== 'string') return false;
  const s = (value as string).trim();
  if (s.length < 20) return false;
  const type = detectType(s);
  return type !== 'text';
}

/* ---- sub-components ---- */

function AttrValue({ value }: { value: unknown }) {
  const [expanded, setExpanded] = useState(false);
  const str = typeof value === 'string' ? value : JSON.stringify(value, null, 2);

  if (isStructuredOrLong(str)) {
    return <SmartContent value={str} maxHeight={250} />;
  }

  const isLong = str.length > 200;
  if (!isLong) return <span className="genai-span-detail__attr-val">{str}</span>;
  return (
    <span className="genai-span-detail__attr-val genai-span-detail__attr-val--long">
      <pre className="genai-span-detail__attr-pre">{expanded ? str : str.slice(0, 200) + '…'}</pre>
      <button className="genai-span-detail__attr-toggle" onClick={() => setExpanded(!expanded)}>
        {expanded ? '▲ moins' : `▼ plus (${str.length.toLocaleString()} chars)`}
      </button>
    </span>
  );
}

function EventCard({ ev }: { ev: SpanEvent }) {
  return (
    <div className="genai-span-detail__event">
      <div className="genai-span-detail__event-head">
        <strong>{ev.name}</strong>
        <span className="genai-span-detail__event-time">{new Date(ev.timeUtc).toLocaleTimeString()}</span>
      </div>
      {ev.attributes && Object.keys(ev.attributes).length > 0 && (
        <table className="genai-span-detail__table">
          <tbody>
            {Object.entries(ev.attributes).map(([k, v]) => (
              <tr key={k}>
                <td className="genai-span-detail__attr-key">{k}</td>
                <td><AttrValue value={v} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

/* ---- main component ---- */

function GenAISpanDetail({ span, allSpans, traceId }: GenAISpanDetailProps) {
  const [activeTab, setActiveTab] = useState<Tab>('io');

  const attrs = span.attributes || {};
  const events = span.events || [];
  const children = allSpans.filter(s => s.parentSpanId === span.spanId);
  const parent = span.parentSpanId ? allSpans.find(s => s.spanId === span.parentSpanId) : null;

  // Separate attributes
  const genAIAttrs = useMemo(() => Object.entries(attrs).filter(([k]) => isGenAIAttr(k)), [attrs]);
  const otherAttrs = useMemo(() => Object.entries(attrs).filter(([k]) => !isGenAIAttr(k)), [attrs]);

  // Separate events by IO type
  const { inputEvents, outputEvents } = useMemo(() => {
    const inp: SpanEvent[] = [];
    const out: SpanEvent[] = [];
    for (const e of events) {
      const n = e.name.toLowerCase();
      if (n.includes('prompt') || n.includes('input') || n.includes('gen_ai.content.prompt'))
        inp.push(e);
      else if (n.includes('completion') || n.includes('output') || n.includes('gen_ai.content.completion') || n.includes('choice'))
        out.push(e);
    }
    return { inputEvents: inp, outputEvents: out };
  }, [events]);

  const kindStr = KINDS[span.kind] ?? 'UNKNOWN';
  const statusStr = STATUSES[span.statusCode] ?? 'UNSET';
  const isError = span.statusCode === 2;

  // Count badges for tabs
  const ioCount = inputEvents.length + outputEvents.length;
  const attrCount = genAIAttrs.length + otherAttrs.length + Object.keys(span.resource || {}).length;
  const eventCount = events.length;

  return (
    <div className="genai-span-detail">
      {/* ── Header ── */}
      <div className="genai-span-detail__header">
        <h3 className="genai-span-detail__title">{span.name || '(unnamed)'}</h3>
        <div className="genai-span-detail__badges">
          <span className={`badge badge--${kindStr.toLowerCase()}`}>{kindStr}</span>
          <span className={`badge badge--${statusStr.toLowerCase()}`}>{statusStr}</span>
          <span className="genai-span-detail__dur-badge">{fmt(span.durationMs)}</span>
        </div>
      </div>

      {isError && span.statusMessage && (
        <div className="genai-span-detail__error">
          ❌ {span.statusMessage}
        </div>
      )}

      {/* ── Identity bar ── */}
      <div className="genai-span-detail__id-bar">
        <span title={traceId}>🔗 {traceId.substring(0, 16)}…</span>
        <span title={span.spanId}>📍 {span.spanId}</span>
        {parent && <span title={parent.name}>⬆ {parent.name}</span>}
        <span>{new Date(span.startUtc).toLocaleTimeString()}</span>
      </div>

      {/* ── Tabs ── */}
      <div className="genai-span-detail__tabs">
        <button
          className={`genai-span-detail__tab ${activeTab === 'io' ? 'genai-span-detail__tab--active' : ''}`}
          onClick={() => setActiveTab('io')}
        >
          📥 Inputs / Outputs
          {ioCount > 0 && <span className="genai-span-detail__tab-badge">{ioCount}</span>}
        </button>
        <button
          className={`genai-span-detail__tab ${activeTab === 'attributes' ? 'genai-span-detail__tab--active' : ''}`}
          onClick={() => setActiveTab('attributes')}
        >
          🏷️ Attributes
          {attrCount > 0 && <span className="genai-span-detail__tab-badge">{attrCount}</span>}
        </button>
        <button
          className={`genai-span-detail__tab ${activeTab === 'events' ? 'genai-span-detail__tab--active' : ''}`}
          onClick={() => setActiveTab('events')}
        >
          ⚡ Events
          {eventCount > 0 && <span className="genai-span-detail__tab-badge">{eventCount}</span>}
        </button>
      </div>

      {/* ── Tab content ── */}
      <div className="genai-span-detail__tab-content">

        {/* ====== TAB: Inputs / Outputs ====== */}
        {activeTab === 'io' && (
          <div className="genai-span-detail__io">
            {/* Inputs */}
            {inputEvents.length > 0 ? (
              <div className="genai-span-detail__section genai-span-detail__section--input">
                <h4 className="genai-span-detail__section-title">📝 Entrée (Input / Prompt)</h4>
                {inputEvents.map((ev, i) => <EventCard key={i} ev={ev} />)}
              </div>
            ) : (
              <div className="genai-span-detail__section genai-span-detail__section--empty">
                <span className="genai-span-detail__empty-label">📝 Pas d'événement d'entrée détecté</span>
              </div>
            )}

            {/* Outputs */}
            {outputEvents.length > 0 ? (
              <div className="genai-span-detail__section genai-span-detail__section--output">
                <h4 className="genai-span-detail__section-title">💬 Sortie (Output / Completion)</h4>
                {outputEvents.map((ev, i) => <EventCard key={i} ev={ev} />)}
              </div>
            ) : (
              <div className="genai-span-detail__section genai-span-detail__section--empty">
                <span className="genai-span-detail__empty-label">💬 Pas d'événement de sortie détecté</span>
              </div>
            )}

            {/* If no IO events at all, show children summary */}
            {ioCount === 0 && children.length > 0 && (
              <div className="genai-span-detail__section">
                <h4 className="genai-span-detail__section-title">🌿 Spans enfants ({children.length})</h4>
                <div className="genai-span-detail__children">
                  {children.map(child => (
                    <div key={child.spanId} className="genai-span-detail__child">
                      <span className="genai-span-detail__child-name">{child.name || '(unnamed)'}</span>
                      <span className="genai-span-detail__child-dur">{fmt(child.durationMs)}</span>
                      <span className={`badge badge--${(STATUSES[child.statusCode] ?? 'unset').toLowerCase()}`}>
                        {STATUSES[child.statusCode] ?? 'UNSET'}
                      </span>
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}

        {/* ====== TAB: Attributes ====== */}
        {activeTab === 'attributes' && (
          <div className="genai-span-detail__attrs-tab">
            {/* GenAI Attributes */}
            {genAIAttrs.length > 0 && (
              <div className="genai-span-detail__section genai-span-detail__section--genai">
                <h4 className="genai-span-detail__section-title">🤖 GenAI ({genAIAttrs.length})</h4>
                <table className="genai-span-detail__table">
                  <tbody>
                    {genAIAttrs.map(([key, value]) => (
                      <tr key={key}>
                        <td className="genai-span-detail__attr-key">{key}</td>
                        <td><AttrValue value={value} /></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {/* Span identity attributes */}
            <div className="genai-span-detail__section">
              <h4 className="genai-span-detail__section-title">📌 Identification</h4>
              <table className="genai-span-detail__table">
                <tbody>
                  <tr><td className="genai-span-detail__attr-key">trace_id</td><td><code>{traceId}</code></td></tr>
                  <tr><td className="genai-span-detail__attr-key">span_id</td><td><code>{span.spanId}</code></td></tr>
                  {span.parentSpanId && <tr><td className="genai-span-detail__attr-key">parent_span_id</td><td><code>{span.parentSpanId}</code>{parent ? ` — ${parent.name}` : ''}</td></tr>}
                  <tr><td className="genai-span-detail__attr-key">duration</td><td><strong>{fmt(span.durationMs)}</strong></td></tr>
                  <tr><td className="genai-span-detail__attr-key">start</td><td>{new Date(span.startUtc).toLocaleString()}</td></tr>
                  <tr><td className="genai-span-detail__attr-key">end</td><td>{new Date(span.endUtc).toLocaleString()}</td></tr>
                  <tr><td className="genai-span-detail__attr-key">kind</td><td>{kindStr}</td></tr>
                  <tr><td className="genai-span-detail__attr-key">status</td><td>{statusStr}{span.statusMessage ? ` — ${span.statusMessage}` : ''}</td></tr>
                </tbody>
              </table>
            </div>

            {/* Other span attributes */}
            {otherAttrs.length > 0 && (
              <div className="genai-span-detail__section">
                <h4 className="genai-span-detail__section-title">🏷️ Span Attributes ({otherAttrs.length})</h4>
                <table className="genai-span-detail__table">
                  <tbody>
                    {otherAttrs.map(([key, value]) => (
                      <tr key={key}>
                        <td className="genai-span-detail__attr-key">{key}</td>
                        <td><AttrValue value={value} /></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {/* Resource attributes */}
            {span.resource && Object.keys(span.resource).length > 0 && (
              <details className="genai-span-detail__section genai-span-detail__section--collapsible" open>
                <summary className="genai-span-detail__section-title">🖥️ Resource ({Object.keys(span.resource).length})</summary>
                <table className="genai-span-detail__table">
                  <tbody>
                    {Object.entries(span.resource).map(([key, value]) => (
                      <tr key={key}>
                        <td className="genai-span-detail__attr-key">{key}</td>
                        <td><AttrValue value={value} /></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </details>
            )}

            {/* Scope */}
            {span.scope && (
              <details className="genai-span-detail__section genai-span-detail__section--collapsible">
                <summary className="genai-span-detail__section-title">📦 Scope</summary>
                <table className="genai-span-detail__table">
                  <tbody>
                    <tr><td className="genai-span-detail__attr-key">name</td><td>{span.scope.name}</td></tr>
                    <tr><td className="genai-span-detail__attr-key">version</td><td>{span.scope.version}</td></tr>
                  </tbody>
                </table>
              </details>
            )}
          </div>
        )}

        {/* ====== TAB: Events ====== */}
        {activeTab === 'events' && (
          <div className="genai-span-detail__events-tab">
            {events.length === 0 ? (
              <div className="genai-span-detail__section genai-span-detail__section--empty">
                <span className="genai-span-detail__empty-label">Aucun événement sur ce span</span>
              </div>
            ) : (
              <>
                {/* All events in chronological order */}
                {events.map((ev, i) => {
                  const n = ev.name.toLowerCase();
                  const isInput = n.includes('prompt') || n.includes('input');
                  const isOutput = n.includes('completion') || n.includes('output') || n.includes('choice');
                  const tagClass = isInput ? 'genai-span-detail__event-tag--input' :
                                   isOutput ? 'genai-span-detail__event-tag--output' : '';
                  const tagLabel = isInput ? 'INPUT' : isOutput ? 'OUTPUT' : 'EVENT';
                  return (
                    <div key={i} className="genai-span-detail__event">
                      <div className="genai-span-detail__event-head">
                        <span className={`genai-span-detail__event-tag ${tagClass}`}>{tagLabel}</span>
                        <strong>{ev.name}</strong>
                        <span className="genai-span-detail__event-time">{new Date(ev.timeUtc).toLocaleTimeString()}</span>
                      </div>
                      {ev.attributes && Object.keys(ev.attributes).length > 0 && (
                        <table className="genai-span-detail__table">
                          <tbody>
                            {Object.entries(ev.attributes).map(([k, v]) => (
                              <tr key={k}>
                                <td className="genai-span-detail__attr-key">{k}</td>
                                <td><AttrValue value={v} /></td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      )}
                    </div>
                  );
                })}
              </>
            )}

            {/* Children in events tab too */}
            {children.length > 0 && (
              <details className="genai-span-detail__section genai-span-detail__section--collapsible">
                <summary className="genai-span-detail__section-title">🌿 Spans enfants ({children.length})</summary>
                <div className="genai-span-detail__children">
                  {children.map(child => (
                    <div key={child.spanId} className="genai-span-detail__child">
                      <span className="genai-span-detail__child-name">{child.name || '(unnamed)'}</span>
                      <span className="genai-span-detail__child-dur">{fmt(child.durationMs)}</span>
                      <span className={`badge badge--${(STATUSES[child.statusCode] ?? 'unset').toLowerCase()}`}>
                        {STATUSES[child.statusCode] ?? 'UNSET'}
                      </span>
                    </div>
                  ))}
                </div>
              </details>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

export default GenAISpanDetail;


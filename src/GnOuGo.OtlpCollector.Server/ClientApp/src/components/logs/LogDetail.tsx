import type { LogEntry } from '../../types';

interface LogDetailProps {
  log: LogEntry | null;
}

const SEVERITY_META: Record<string, { label: string; icon: string; className: string }> = {
  fatal: { label: 'FATAL', icon: '💀', className: 'fatal' },
  error: { label: 'ERROR', icon: '❌', className: 'error' },
  warn:  { label: 'WARN',  icon: '⚠️', className: 'warn' },
  info:  { label: 'INFO',  icon: 'ℹ️', className: 'info' },
  debug: { label: 'DEBUG', icon: '🐛', className: 'debug' },
  trace: { label: 'TRACE', icon: '🔍', className: 'trace' },
};

function severityKey(n: number): string {
  if (n >= 21) return 'fatal';
  if (n >= 17) return 'error';
  if (n >= 13) return 'warn';
  if (n >= 9)  return 'info';
  if (n >= 5)  return 'debug';
  return 'trace';
}

function fmtTimestamp(ts: string): string {
  try {
    if (!ts) return '—';
    const d = new Date(ts);
    if (isNaN(d.getTime())) return ts;
    const date = d.toLocaleDateString(navigator.language, { year: 'numeric', month: '2-digit', day: '2-digit' });
    const time = d.toLocaleTimeString(navigator.language, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    const ms = d.getMilliseconds().toString().padStart(3, '0');
    return `${date} ${time}.${ms}`;
  } catch { return ts || '—'; }
}

function AttrTable({ data, title }: { data: Record<string, unknown>; title: string }) {
  const entries = Object.entries(data);
  if (entries.length === 0) return null;
  return (
    <details className="log-detail__section" open>
      <summary className="log-detail__section-title">{title} <span className="log-detail__section-count">{entries.length}</span></summary>
      <table className="log-detail__table">
        <tbody>
          {entries.map(([k, v]) => (
            <tr key={k}>
              <td className="log-detail__attr-key">{k}</td>
              <td className="log-detail__attr-val">{typeof v === 'object' ? JSON.stringify(v) : String(v)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </details>
  );
}

function LogDetail({ log }: LogDetailProps) {
  if (!log) {
    return (
      <section className="panel panel--scroll">
        <div className="log-detail log-detail--empty">
          <div className="log-detail__placeholder">
            <span className="log-detail__placeholder-icon">📋</span>
            <span className="log-detail__placeholder-text">Sélectionnez un log pour voir les détails</span>
          </div>
        </div>
      </section>
    );
  }

  const sk = severityKey(log.severityNumber);
  const meta = SEVERITY_META[sk] ?? SEVERITY_META.trace;
  const scopeName = log.scope ? String(log.scope['name'] ?? '') : '';
  const scopeVersion = log.scope ? String(log.scope['version'] ?? '') : '';

  return (
    <section className="panel panel--scroll">
      <div className="log-detail">
        {/* Header */}
        <div className={`log-detail__header log-detail__header--${meta.className}`}>
          <span className="log-detail__header-icon">{meta.icon}</span>
          <span className={`badge badge--${meta.className}`}>{log.severityText || meta.label}</span>
          {log.serviceName && <span className="badge badge--service">{log.serviceName}</span>}
          <span className="log-detail__header-ts">{fmtTimestamp(log.receivedUtc)}</span>
        </div>

        {/* Body */}
        <div className="log-detail__body">
          <pre className="log-detail__body-pre">{log.body || '(empty body)'}</pre>
        </div>

        {/* Identity */}
        <div className="log-detail__id-bar">
          <span className="log-detail__id-item">📊 Severity {log.severityNumber}</span>
          {log.traceId && <span className="log-detail__id-item">🔗 Trace {log.traceId.substring(0, 16)}…</span>}
          {log.spanId && <span className="log-detail__id-item">📍 Span {log.spanId}</span>}
          {scopeName && (
            <span className="log-detail__id-item">📦 {scopeName}{scopeVersion ? ` v${scopeVersion}` : ''}</span>
          )}
        </div>

        {/* Attributes */}
        {log.attributes && Object.keys(log.attributes).length > 0 && (
          <AttrTable data={log.attributes} title="🏷️ Attributes" />
        )}

        {/* Resource */}
        {log.resource && Object.keys(log.resource).length > 0 && (
          <AttrTable data={log.resource} title="🖥️ Resource" />
        )}

        {/* Scope */}
        {log.scope && Object.keys(log.scope).length > 0 && (
          <AttrTable data={log.scope} title="📦 Scope" />
        )}

        {/* Raw JSON */}
        <details className="log-detail__section">
          <summary className="log-detail__section-title">🔧 Raw JSON</summary>
          <pre className="log-detail__json">{JSON.stringify(log, null, 2)}</pre>
        </details>
      </div>
    </section>
  );
}

export default LogDetail;


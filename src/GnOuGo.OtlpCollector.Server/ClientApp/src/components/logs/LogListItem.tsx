import type { LogEntry } from '../../types';

interface LogListItemProps {
  log: LogEntry;
  isSelected: boolean;
  onClick: () => void;
}

const SEVERITY_ICONS: Record<string, string> = {
  fatal: '💀', error: '❌', warn: '⚠️', info: 'ℹ️', debug: '🐛', trace: '🔍',
};

function severityKey(n: number): string {
  if (n >= 21) return 'fatal';
  if (n >= 17) return 'error';
  if (n >= 13) return 'warn';
  if (n >= 9)  return 'info';
  if (n >= 5)  return 'debug';
  return 'trace';
}

function severityLabel(text: string, n: number): string {
  if (text) return text;
  if (n >= 21) return 'FATAL';
  if (n >= 17) return 'ERROR';
  if (n >= 13) return 'WARN';
  if (n >= 9)  return 'INFO';
  if (n >= 5)  return 'DEBUG';
  return 'TRACE';
}

function fmtTime(ts: string): string {
  try {
    if (!ts) return '—';
    const d = new Date(ts);
    if (isNaN(d.getTime())) return ts;
    return d.toLocaleTimeString(navigator.language, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  } catch { return ts || '—'; }
}

function LogListItem({ log, isSelected, onClick }: LogListItemProps) {
  const sk = severityKey(log.severityNumber);
  const icon = SEVERITY_ICONS[sk] ?? '📝';
  const label = severityLabel(log.severityText, log.severityNumber);
  const bodyPreview = log.body ? (log.body.length > 120 ? log.body.slice(0, 120) + '…' : log.body) : '(empty)';

  return (
    <article
      className={`log-item log-item--${sk} ${isSelected ? 'log-item--selected' : ''}`}
      onClick={onClick}
    >
      <div className="log-item__indicator" />

      <div className="log-item__content">
        <div className="log-item__top-row">
          <span className="log-item__icon">{icon}</span>
          <span className={`badge badge--${sk}`}>{label}</span>
          <span className="log-item__time">{fmtTime(log.receivedUtc)}</span>
          {log.serviceName && <span className="badge badge--service">{log.serviceName}</span>}
        </div>
        <div className="log-item__body-preview">{bodyPreview}</div>
      </div>
    </article>
  );
}

export default LogListItem;


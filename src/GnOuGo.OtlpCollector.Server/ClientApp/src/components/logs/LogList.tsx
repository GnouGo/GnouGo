import LogListItem from './LogListItem';
import type { LogEntry } from '../../types';

interface LogListProps {
  logs: LogEntry[];
  selectedIdx: number | null;
  onSelectLog: (idx: number) => void;
  loading: boolean;
}

function LogList({ logs, selectedIdx, onSelectLog, loading }: LogListProps) {
  if (loading) {
    return (
      <section className="panel panel--scroll">
        <div className="log-list">
          <div className="log-list__loading">Loading logs...</div>
        </div>
      </section>
    );
  }

  return (
    <section className="panel panel--scroll">
      <div className="log-list">
        <div className="log-list__header">
          <h2 className="log-list__title">Logs</h2>
          <span className="badge">{logs.length}</span>
        </div>

        <div className="log-list__items">
          {logs.length === 0 ? (
            <div className="log-list__empty">
              No logs found. Configure your settings and click the button to load.
            </div>
          ) : (
            logs.map((log, i) => (
              <LogListItem
                key={`${log.receivedUtc}-${i}`}
                log={log}
                isSelected={i === selectedIdx}
                onClick={() => onSelectLog(i)}
              />
            ))
          )}
        </div>
      </div>
    </section>
  );
}

export default LogList;

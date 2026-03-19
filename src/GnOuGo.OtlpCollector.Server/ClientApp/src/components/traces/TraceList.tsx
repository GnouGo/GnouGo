import TraceListItem from './TraceListItem';
import type { TraceGroup } from '../../types';

interface TraceListProps {
  traces: TraceGroup[];
  selectedTraceId: string | null;
  onTraceClick: (traceId: string) => void;
}

function TraceList({ traces, selectedTraceId, onTraceClick }: TraceListProps) {
  return (
    <section className="panel panel--scroll">
      <div className="trace-list">
        <div className="trace-list__header">
          <h2 className="trace-list__title">Recent Traces</h2>
          <span className="badge">{traces.length}</span>
        </div>

        <div className="trace-list__items">
          {traces.length === 0 ? (
            <div className="trace-list__empty">
              No traces found. Configure your settings and click Refresh.
            </div>
          ) : (
            traces.map((trace) => (
              <TraceListItem
                key={trace.traceId}
                trace={trace}
                isSelected={trace.traceId === selectedTraceId}
                onClick={() => onTraceClick(trace.traceId)}
              />
            ))
          )}
        </div>
      </div>
    </section>
  );
}

export default TraceList;


import type { TraceGroup } from '../../types';

function formatDate(dateString: string): string {
  try {
    return new Date(dateString).toLocaleString();
  } catch {
    return String(dateString);
  }
}

function formatDuration(startUtc: string, endUtc: string): string {
  try {
    const start = new Date(startUtc);
    const end = new Date(endUtc);
    const durationMs = Math.max(0, end.getTime() - start.getTime()); // Assurer durée positive
    if (durationMs < 1000) {
      return `${durationMs}ms`;
    }
    return `${(durationMs / 1000).toFixed(2)}s`;
  } catch {
    return 'N/A';
  }
}

interface TraceType {
  type: string;
  icon: string;
  label: string;
}

function detectTraceType(trace: TraceGroup & { rootSpanName?: string; servicesCsv?: string }): TraceType {
  const name = (trace.rootSpanName || '').toLowerCase();
  const services = (trace.servicesCsv || '').toLowerCase();
  
  // Détecter GenAI/LLM
  if (name.includes('llm') || name.includes('completion') || name.includes('chat') || 
      name.includes('gen_ai') || services.includes('openai') || services.includes('llm')) {
    return { type: 'genai', icon: '🤖', label: 'GenAI' };
  }
  
  // Détecter RAG
  if (name.includes('rag') || name.includes('retrieval') || 
      (name.includes('embed') && name.includes('search'))) {
    return { type: 'rag', icon: '🔄', label: 'RAG' };
  }
  
  // Détecter Embedding
  if (name.includes('embed')) {
    return { type: 'embedding', icon: '🧬', label: 'Embedding' };
  }
  
  // Détecter Search/Retrieval
  if (name.includes('search') || name.includes('retrieval') || name.includes('query')) {
    return { type: 'search', icon: '🔍', label: 'Search' };
  }
  
  return { type: 'default', icon: '📊', label: 'Trace' };
}

interface TraceListItemProps {
  trace: TraceGroup & {
    rootSpanName?: string;
    serviceName?: string;
    servicesCsv?: string;
    spanCount?: number;
  };
  isSelected: boolean;
  onClick: () => void;
}

function TraceListItem({ trace, isSelected, onClick }: TraceListItemProps) {
  const traceType = detectTraceType(trace);
  const duration = formatDuration(trace.startUtc, trace.endUtc);
  
  return (
    <article
      className={`trace-item trace-item--${traceType.type} ${isSelected ? 'trace-item--selected' : ''}`}
      onClick={onClick}
    >
      <div className="trace-item__type-indicator">
        <span className="trace-item__icon">{traceType.icon}</span>
      </div>
      
      <div className="trace-item__content">
        <div className="trace-item__header">
          <div className="trace-item__name">
            {trace.rootSpanName || '(no root span)'}
          </div>
          <span className={`badge badge--${traceType.type}`}>{traceType.label}</span>
        </div>
        
        <div className="trace-item__id">{trace.traceId.substring(0, 16)}...</div>
        
        <div className="trace-item__meta">
          <span className="trace-item__time">{formatDate(trace.startUtc)}</span>
          <span className="trace-item__separator">•</span>
          <span className="trace-item__duration">{duration}</span>
          <span className="trace-item__separator">•</span>
          <span className="trace-item__spans">{trace.spanCount || trace.spans.length} spans</span>
        </div>
      </div>
      
      <div className="trace-item__badge">
        <span className="badge badge--service">{trace.serviceName || trace.servicesCsv || 'unknown-service'}</span>
      </div>
    </article>
  );
}

export default TraceListItem;


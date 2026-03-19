import SpanTree from './SpanTree';
import SpanTimeline from './SpanTimeline';
import RAGFlowVisualization from './RAGFlowVisualization';
import LLMMetricsPanel from './LLMMetricsPanel';
import GenAIEventViewer from './GenAIEventViewer';
import GenAITraceDetail from './GenAITraceDetail';
import type { TraceGroup, Span } from '../../types';

interface TraceDetailProps {
  trace: TraceGroup | null;
  selectedTraceId: string | null;
  loading: boolean;
}

/** Détecte si la trace contient des spans GenAI (standard OpenTelemetry GenAI) */
function isGenAITrace(trace: TraceGroup): boolean {
  return trace.spans.some((s: Span) => {
    const a = s.attributes || {};
    const name = (s.name || '').toLowerCase();
    return !!(
      a['gen_ai.request.model'] ||
      a['gen_ai.response.model'] ||
      a['gen_ai.operation.name'] ||
      a['gen_ai.system'] ||
      a['llm.request.model'] ||
      name.includes('llm') ||
      name.includes('completion') ||
      name.includes('gen_ai')
    );
  });
}

function TraceDetail({ trace, selectedTraceId, loading }: TraceDetailProps) {
  if (!selectedTraceId) {
    return (
      <section className="panel panel--scroll">
        <div className="trace-detail">
          <div className="trace-detail__empty">
            Select a trace to view details
          </div>
        </div>
      </section>
    );
  }

  if (loading) {
    return (
      <section className="panel panel--scroll">
        <div className="trace-detail">
          <div className="trace-detail__loading">
            Loading trace details...
          </div>
        </div>
      </section>
    );
  }

  if (!trace) {
    return (
      <section className="panel panel--scroll">
        <div className="trace-detail">
          <div className="trace-detail__error">
            Failed to load trace details
          </div>
        </div>
      </section>
    );
  }

  // Si la trace est de type GenAI, afficher la vue split dédiée
  if (isGenAITrace(trace)) {
    return (
      <section className="panel panel--scroll">
        <div className="trace-detail">
          <div className="trace-detail__header">
            <h2 className="trace-detail__title">🤖 GenAI Trace</h2>
            <span className="badge badge--genai">{trace.traceId.substring(0, 16)}...</span>
          </div>
          <GenAITraceDetail trace={trace} />
        </div>
      </section>
    );
  }

  // Vue classique pour les traces non-GenAI
  return (
    <section className="panel panel--scroll">
      <div className="trace-detail">
        <div className="trace-detail__header">
          <h2 className="trace-detail__title">Trace Details</h2>
          <span className="badge">{trace.traceId.substring(0, 16)}...</span>
        </div>

        <LLMMetricsPanel trace={trace} />
        <RAGFlowVisualization trace={trace} />
        <GenAIEventViewer trace={trace} />
        <SpanTimeline spans={trace.spans} />
        <SpanTree spans={trace.spans} />
      </div>
    </section>
  );
}

export default TraceDetail;


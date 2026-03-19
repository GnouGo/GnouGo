import type { TraceGroup, Span } from '../../types';

interface RAGFlowVisualizationProps {
  trace: TraceGroup;
}

interface RAGStep {
  name: string;
  icon: string;
  spans: Span[];
  duration: number;
}

// Helper : durée fiable d'un span
const spanDurationMs = (s: Span): number => {
  if (s.durationMs > 0) return s.durationMs;
  const d = new Date(s.endUtc).getTime() - new Date(s.startUtc).getTime();
  return Math.max(0, d);
};

function RAGFlowVisualization({ trace }: RAGFlowVisualizationProps) {
  const detectRAGSteps = (spans: Span[]): RAGStep[] => {
    const steps: RAGStep[] = [];
    
    const extractionSpans = spans.filter(s => {
      const n = (s.name || '').toLowerCase();
      return n.includes('extraction') || n.includes('extract');
    });

    const ocrSpans = spans.filter(s =>
      (s.name || '').toLowerCase().includes('ocr')
    );

    const chunkingSpans = spans.filter(s =>
      (s.name || '').toLowerCase().includes('chunk')
    );

    const embeddingSpans = spans.filter(s => 
      (s.name || '').toLowerCase().includes('embed') ||
      s.attributes?.['gen_ai.operation.name'] === 'embeddings' ||
      s.attributes?.['gen_ai.operation.name'] === 'embedding' ||
      s.attributes?.['gen_ai.operation.name'] === 'batch_embedding'
    );
    
    const searchSpans = spans.filter(s => 
      (s.name || '').toLowerCase().includes('search') ||
      (s.name || '').toLowerCase().includes('retrieval') ||
      (s.name || '').toLowerCase().includes('query')
    );
    
    const llmSpans = spans.filter(s => 
      s.attributes?.['gen_ai.request.model'] ||
      s.attributes?.['llm.request.model'] ||
      (s.name || '').toLowerCase().includes('completion') ||
      (s.name || '').toLowerCase().includes('chat')
    );

    const storeSpans = spans.filter(s => {
      const n = (s.name || '').toLowerCase();
      return n.includes('store') || n.includes('upsert');
    });

    const addStep = (name: string, icon: string, stepSpans: Span[]) => {
      if (stepSpans.length > 0) {
        steps.push({
          name,
          icon,
          spans: stepSpans,
          duration: stepSpans.reduce((sum, s) => sum + spanDurationMs(s), 0)
        });
      }
    };

    addStep('Extraction', '📄', extractionSpans);
    addStep('OCR', '👁️', ocrSpans);
    addStep('Chunking', '✂️', chunkingSpans);
    addStep('Embedding', '🧬', embeddingSpans);
    addStep('Retrieval', '🔍', searchSpans);
    addStep('Generation', '🤖', llmSpans);
    addStep('Vector Store', '💾', storeSpans);

    return steps;
  };

  const formatDuration = (ms: number): string => {
    if (ms <= 0) return '0ms';
    if (ms < 1) return `${(ms * 1000).toFixed(0)}µs`;
    if (ms < 1000) return `${ms.toFixed(2)}ms`;
    return `${(ms / 1000).toFixed(3)}s`;
  };

  const steps = detectRAGSteps(trace.spans);

  if (steps.length === 0) {
    return null;
  }

  const totalDuration = steps.reduce((sum, step) => sum + step.duration, 0);

  return (
    <div className="rag-flow">
      <h3 className="rag-flow__title">
        🔄 RAG Workflow
      </h3>
      
      <div className="rag-flow__steps">
        {steps.map((step, index) => {
          const percentage = totalDuration > 0 ? (step.duration / totalDuration) * 100 : 0;
          
          return (
            <div key={step.name} className="rag-flow__step">
              <div className="rag-flow__step-header">
                <span className="rag-flow__step-icon">{step.icon}</span>
                <span className="rag-flow__step-name">{step.name}</span>
                <span className="badge">{step.spans.length} span(s)</span>
              </div>
              
              <div className="rag-flow__step-bar">
                <div 
                  className="rag-flow__step-progress"
                  style={{ width: `${percentage}%` }}
                  title={`${percentage.toFixed(1)}% of total time`}
                />
              </div>
              
              <div className="rag-flow__step-meta">
                <span className="rag-flow__step-duration">
                  {formatDuration(step.duration)}
                </span>
                <span className="rag-flow__step-percentage">
                  {percentage.toFixed(1)}%
                </span>
              </div>
              
              {/* Arrow between steps */}
              {index < steps.length - 1 && (
                <div className="rag-flow__arrow">→</div>
              )}
            </div>
          );
        })}
      </div>
      
      <div className="rag-flow__summary">
        <div className="rag-flow__summary-item">
          <span className="rag-flow__summary-label">Total Pipeline Duration:</span>
          <span className="rag-flow__summary-value">{formatDuration(totalDuration)}</span>
        </div>
      </div>
    </div>
  );
}

export default RAGFlowVisualization;


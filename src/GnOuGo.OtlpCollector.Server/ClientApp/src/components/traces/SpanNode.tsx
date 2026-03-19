import { useState } from 'react';
import type { Span } from '../../types';

interface SpanNodeProps {
  span: Span & { children?: Span[] };
  level: number;
}

const SPAN_KINDS: Record<number, string> = {
  0: 'UNSPECIFIED', 1: 'INTERNAL', 2: 'SERVER',
  3: 'CLIENT', 4: 'PRODUCER', 5: 'CONSUMER'
};

const STATUS_LABELS: Record<number, string> = {
  0: 'UNSET', 1: 'OK', 2: 'ERROR'
};

function formatDuration(ms: number): string {
  if (ms <= 0) return '< 1ms';
  if (ms < 1) return `${(ms * 1000).toFixed(0)}µs`;
  if (ms < 1000) return `${ms.toFixed(1)}ms`;
  return `${(ms / 1000).toFixed(2)}s`;
}

function SpanNode({ span, level }: SpanNodeProps) {
  const [isExpanded, setIsExpanded] = useState(true);

  const hasChildren = span.children && span.children.length > 0;
  const isGenAI = span.attributes && (
    'gen_ai.request.model' in span.attributes ||
    'gen_ai.response.model' in span.attributes ||
    'gen_ai.operation.name' in span.attributes
  );
  const kind = SPAN_KINDS[span.kind] ?? 'UNKNOWN';
  const status = STATUS_LABELS[span.statusCode] ?? 'UNSET';

  const rowClass = [
    'span-node__row',
    hasChildren ? 'span-node__row--has-children' : '',
    isGenAI ? 'span-node__row--genai' : ''
  ].filter(Boolean).join(' ');

  return (
    <div className="span-node">
      <div className={rowClass} style={{ paddingLeft: `${level * 20}px` }}>
        {hasChildren && (
          <button
            className={`span-node__toggle ${isExpanded ? 'span-node__toggle--expanded' : ''}`}
            onClick={() => setIsExpanded(!isExpanded)}
          >
            {isExpanded ? '▼' : '▶'}
          </button>
        )}

        <span className="span-node__name">
          {isGenAI && '🤖 '}
          {span.name || '(unnamed)'}
        </span>

        <div className="span-node__badges">
          <span className={`badge badge--${kind.toLowerCase()}`}>{kind}</span>
          <span className={`badge badge--${status.toLowerCase()}`}>{status}</span>
          <span className="span-node__dur">{formatDuration(span.durationMs)}</span>
        </div>
      </div>

      {isExpanded && hasChildren && (
        <div className="span-node__children">
          {span.children!.map(child => (
            <SpanNode key={child.spanId} span={child} level={level + 1} />
          ))}
        </div>
      )}

      {isExpanded && span.events && span.events.length > 0 && (
        <div className="span-node__attributes" style={{ marginLeft: `${level * 20 + 20}px` }}>
          <div className="span-node__ev-title">Events ({span.events.length})</div>
          {span.events.map((evt, idx) => (
            <div key={idx} className="span-node__ev">
              <span className="span-node__ev-name">{evt.name}</span>
              <span className="span-node__ev-time">
                {new Date(evt.timeUtc).toLocaleTimeString(navigator.language)}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

export default SpanNode;


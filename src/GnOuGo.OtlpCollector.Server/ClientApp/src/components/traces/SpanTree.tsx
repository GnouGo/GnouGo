import SpanNode from './SpanNode';
import type { Span } from '../../types';

interface SpanTreeProps {
  spans: Span[];
}

function SpanTree({ spans }: SpanTreeProps) {
  // Construire l'arbre des spans
  const buildTree = (spans: Span[]): Span[] => {
    const spanMap = new Map<string, Span & { children?: Span[] }>();
    const rootSpans: Span[] = [];

    // Créer une map de tous les spans
    spans.forEach(span => {
      spanMap.set(span.spanId, { ...span, children: [] });
    });

    // Construire l'arbre
    spans.forEach(span => {
      const node = spanMap.get(span.spanId);
      if (!node) return;

      if (!span.parentSpanId) {
        rootSpans.push(node);
      } else {
        const parent = spanMap.get(span.parentSpanId);
        if (parent && parent.children) {
          parent.children.push(node);
        } else {
          // Si le parent n'existe pas, c'est un root span
          rootSpans.push(node);
        }
      }
    });

    return rootSpans;
  };

  const rootSpans = buildTree(spans);

  return (
    <div className="span-tree">
      <h3 className="span-tree__title">Span Tree</h3>
      <div className="span-tree__content">
        {rootSpans.map(span => (
          <SpanNode key={span.spanId} span={span} level={0} />
        ))}
      </div>
    </div>
  );
}

export default SpanTree;


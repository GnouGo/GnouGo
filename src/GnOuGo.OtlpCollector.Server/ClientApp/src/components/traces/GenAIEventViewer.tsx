import { useState } from 'react';
import type { TraceGroup, SpanEvent } from '../../types';

interface GenAIEventViewerProps {
  trace: TraceGroup;
}

interface GenAIEventWithSpan extends SpanEvent {
  spanName: string;
  spanId: string;
}

function GenAIEventViewer({ trace }: GenAIEventViewerProps) {
  const [expandedEvent, setExpandedEvent] = useState<number | null>(null);

  const extractGenAIEvents = (): GenAIEventWithSpan[] => {
    const events: GenAIEventWithSpan[] = [];

    trace.spans.forEach(span => {
      if (span.events && span.events.length > 0) {
        span.events.forEach(event => {
          // Filtrer les événements GenAI
          const eventName = event.name.toLowerCase();
          if (
            eventName.includes('gen_ai') ||
            eventName.includes('llm') ||
            eventName.includes('prompt') ||
            eventName.includes('completion') ||
            eventName.includes('embedding') ||
            eventName.includes('ocr') ||
            eventName.includes('chat_scoring') ||
            eventName.includes('rerank')
          ) {
            events.push({
              ...event,
              spanName: span.name || 'Unnamed span',
              spanId: span.spanId
            });
          }
        });
      }
    });

    // Trier par timestamp
    return events.sort((a, b) => 
      new Date(a.timeUtc).getTime() - new Date(b.timeUtc).getTime()
    );
  };

  const formatTimestamp = (timestamp: string): string => {
    try {
      const date = new Date(timestamp);
      const formatted = date.toLocaleTimeString(navigator.language, {
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit'
      });
      const ms = date.getMilliseconds().toString().padStart(3, '0');
      return `${formatted},${ms}`;
    } catch {
      return timestamp;
    }
  };

  const getEventIcon = (eventName: string): string => {
    const name = eventName.toLowerCase();
    if (name.includes('prompt') || name.includes('input')) return '📝';
    if (name.includes('completion') || name.includes('output')) return '💬';
    if (name.includes('embedding')) return '🧬';
    if (name.includes('ocr')) return '👁';
    if (name.includes('scoring') || name.includes('rerank')) return '🏆';
    if (name.includes('error')) return '❌';
    if (name.includes('success')) return '✅';
    return '📌';
  };

  const events = extractGenAIEvents();

  if (events.length === 0) {
    return null;
  }

  return (
    <div className="genai-events">
      <h3 className="genai-events__title">
        🎯 GenAI Events ({events.length})
      </h3>

      <div className="genai-events__list">
        {events.map((event, index) => (
          <div 
            key={index} 
            className={`genai-events__item ${expandedEvent === index ? 'genai-events__item--expanded' : ''}`}
          >
            <div 
              className="genai-events__header"
              onClick={() => setExpandedEvent(expandedEvent === index ? null : index)}
            >
              <span className="genai-events__icon">{getEventIcon(event.name)}</span>
              
              <div className="genai-events__info">
                <div className="genai-events__name">{event.name}</div>
                <div className="genai-events__meta">
                  <span className="genai-events__span-name">{event.spanName}</span>
                  <span className="genai-events__separator">•</span>
                  <span className="genai-events__timestamp">{formatTimestamp(event.timeUtc)}</span>
                </div>
              </div>

              <button 
                className={`genai-events__toggle ${expandedEvent === index ? 'genai-events__toggle--expanded' : ''}`}
                aria-label={expandedEvent === index ? 'Collapse' : 'Expand'}
              >
                {expandedEvent === index ? '▼' : '▶'}
              </button>
            </div>

            {expandedEvent === index && (
              <div className="genai-events__content">
                {/* Afficher les attributs importants en premier */}
                {event.attributes && Object.keys(event.attributes).length > 0 && (
                  <div className="genai-events__attributes">
                    {Object.entries(event.attributes).map(([key, value]) => {
                      // Formater les valeurs longues (prompts, completions)
                      const isLongText = typeof value === 'string' && value.length > 100;
                      
                      return (
                        <div key={key} className="genai-events__attribute">
                          <span className="genai-events__attribute-key">{key}:</span>
                          <div className="genai-events__attribute-value">
                            {isLongText ? (
                              <pre className="genai-events__text-content">
                                {String(value)}
                              </pre>
                            ) : (
                              <span>{String(value)}</span>
                            )}
                          </div>
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

export default GenAIEventViewer;


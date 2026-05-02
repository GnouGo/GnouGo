import type { TraceGroup, Span } from '../../types';

interface LLMMetricsPanelProps {
  trace: TraceGroup;
}

interface LLMMetrics {
  model: string;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  duration: number;
  provider: string;
  cost: number;
  operationName: string;
  temperature?: number;
  maxTokens?: number;
  topP?: number;
  spanName: string;
}

function LLMMetricsPanel({ trace }: LLMMetricsPanelProps) {
  const extractLLMMetrics = (spans: Span[]): LLMMetrics[] => {
    return spans
      .filter(span => {
        const attrs = span.attributes || {};
        return attrs['gen_ai.request.model'] || 
               attrs['llm.request.model'] ||
               attrs['gen_ai.response.model'];
      })
      .map(span => {
        const attrs = span.attributes || {};
        // Utiliser durationMs du serveur (fiable) au lieu de calculer depuis startUtc/endUtc
        const duration = span.durationMs > 0 ? span.durationMs : Math.max(0, new Date(span.endUtc).getTime() - new Date(span.startUtc).getTime());

        // Extraire le modèle
        const model = String(
          attrs['gen_ai.request.model'] || 
          attrs['gen_ai.response.model'] || 
          attrs['llm.request.model'] || 
          'unknown'
        );

        // Extraire les tokens (supporter les deux conventions)
        const promptTokens = Number(
          attrs['gen_ai.usage.prompt_tokens'] || 
          attrs['gen_ai.usage.input_tokens'] || 
          attrs['llm.usage.prompt_tokens'] || 
          0
        );

        const completionTokens = Number(
          attrs['gen_ai.usage.completion_tokens'] || 
          attrs['gen_ai.usage.output_tokens'] || 
          attrs['llm.usage.completion_tokens'] || 
          0
        );

        const totalTokens = Number(
          attrs['gen_ai.usage.total_tokens'] || 
          attrs['llm.usage.total_tokens'] || 
          promptTokens + completionTokens
        );

        // Extraire le provider depuis gen_ai.system (émis par GenAiTelemetry)
        const system = String(attrs['gen_ai.system'] || '').toLowerCase();
        let provider = 'unknown';
        if (system === 'openai' || model.includes('gpt')) provider = 'OpenAI';
        else if (system === 'ollama') provider = 'Ollama';
        else if (system === 'anthropic' || model.includes('claude')) provider = 'Anthropic';
        else if (model.includes('gemini')) provider = 'Google';
        else if (model.includes('llama') || model.includes('mistral')) provider = 'Ollama';


        // Extraire le coût depuis gen_ai.usage.cost (calculé par le backend via ModelMetadataCatalog)
        const cost = Number(attrs['gen_ai.usage.cost'] || 0);

        // Extraire le nom d'opération
        const operationName = String(attrs['gen_ai.operation.name'] || span.name || 'LLM Call');

        // Paramètres optionnels
        const temperature = attrs['gen_ai.request.temperature'] ? 
          Number(attrs['gen_ai.request.temperature']) : undefined;
        const maxTokens = attrs['gen_ai.request.max_tokens'] ? 
          Number(attrs['gen_ai.request.max_tokens']) : undefined;
        const topP = attrs['gen_ai.request.top_p'] ? 
          Number(attrs['gen_ai.request.top_p']) : undefined;

        return {
          model,
          promptTokens,
          completionTokens,
          totalTokens,
          duration,
          provider,
          cost,
          operationName,
          temperature,
          maxTokens,
          topP,
          spanName: span.name || 'LLM Call'
        };
      });
  };

  const formatDuration = (ms: number): string => {
    if (ms < 1) return `${(ms * 1000).toFixed(0)}µs`;
    if (ms < 1000) return `${ms.toFixed(2)}ms`;
    return `${(ms / 1000).toFixed(3)}s`;
  };

  const metrics = extractLLMMetrics(trace.spans);

  if (metrics.length === 0) {
    return null;
  }

  const totalTokens = metrics.reduce((sum, m) => sum + m.totalTokens, 0);
  const totalCost = metrics.reduce((sum, m) => sum + m.cost, 0);
  const totalDuration = metrics.reduce((sum, m) => sum + m.duration, 0);

  return (
    <div className="llm-metrics">
      <h3 className="llm-metrics__title">
        🤖 LLM Metrics
      </h3>

      {/* Summary */}
      <div className="llm-metrics__summary">
        <div className="llm-metrics__stat">
          <span className="llm-metrics__stat-label">Total Tokens</span>
          <span className="llm-metrics__stat-value">{totalTokens.toLocaleString()}</span>
        </div>
        <div className="llm-metrics__stat">
          <span className="llm-metrics__stat-label">Total Duration</span>
          <span className="llm-metrics__stat-value">{formatDuration(totalDuration)}</span>
        </div>
        <div className="llm-metrics__stat">
          <span className="llm-metrics__stat-label">Est. Cost</span>
          <span className="llm-metrics__stat-value">${totalCost.toFixed(6)}</span>
        </div>
        <div className="llm-metrics__stat">
          <span className="llm-metrics__stat-label">Calls</span>
          <span className="llm-metrics__stat-value">{metrics.length}</span>
        </div>
      </div>

      {/* Detailed metrics per call */}
      <div className="llm-metrics__calls">
        {metrics.map((metric, index) => (
          <div key={index} className="llm-metrics__call">
            <div className="llm-metrics__call-header">
              <span className="llm-metrics__call-name">{metric.operationName}</span>
              <span className="badge badge--service">{metric.provider}</span>
            </div>

            <div className="llm-metrics__call-model">
              <span className="llm-metrics__call-label">Model:</span>
              <span className="llm-metrics__call-value">{metric.model}</span>
            </div>

            <div className="llm-metrics__call-tokens">
              <div className="llm-metrics__token-bar">
                <div 
                  className="llm-metrics__token-segment llm-metrics__token-segment--prompt"
                  style={{ 
                    width: `${metric.totalTokens > 0 ? (metric.promptTokens / metric.totalTokens) * 100 : 0}%` 
                  }}
                  title={`Prompt: ${metric.promptTokens} tokens`}
                />
                <div 
                  className="llm-metrics__token-segment llm-metrics__token-segment--completion"
                  style={{ 
                    width: `${metric.totalTokens > 0 ? (metric.completionTokens / metric.totalTokens) * 100 : 0}%` 
                  }}
                  title={`Completion: ${metric.completionTokens} tokens`}
                />
              </div>
              
              <div className="llm-metrics__token-details">
                <span className="llm-metrics__token-item">
                  <span className="llm-metrics__token-dot llm-metrics__token-dot--prompt" />
                  Prompt: {metric.promptTokens.toLocaleString()}
                </span>
                <span className="llm-metrics__token-item">
                  <span className="llm-metrics__token-dot llm-metrics__token-dot--completion" />
                  Completion: {metric.completionTokens.toLocaleString()}
                </span>
                <span className="llm-metrics__token-item">
                  Total: {metric.totalTokens.toLocaleString()}
                </span>
              </div>
            </div>

            <div className="llm-metrics__call-meta">
              <span className="llm-metrics__call-duration">
                ⏱️ {formatDuration(metric.duration)}
              </span>
              <span className="llm-metrics__call-cost">
                💰 ${metric.cost.toFixed(6)}
              </span>
              {metric.temperature !== undefined && (
                <span className="llm-metrics__call-param">
                  🌡️ temp: {metric.temperature}
                </span>
              )}
              {metric.maxTokens !== undefined && (
                <span className="llm-metrics__call-param">
                  📏 max: {metric.maxTokens}
                </span>
              )}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

export default LLMMetricsPanel;


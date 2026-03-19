import { useMemo } from 'react';
import type { TraceGroup, Span } from '../../types';

interface GenAISummaryPanelProps {
  trace: TraceGroup;
}

interface LLMCallMetric {
  operationName: string;
  model: string;
  provider: string;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  duration: number;
  cost: number;
  temperature?: number;
}

interface RAGStep {
  name: string;
  icon: string;
  spanCount: number;
  duration: number;
  percentage: number;
}

function spanDurMs(s: Span): number {
  if (s.durationMs > 0) return s.durationMs;
  return Math.max(0, new Date(s.endUtc).getTime() - new Date(s.startUtc).getTime());
}

function prov(a: Record<string, string | number | boolean>, model: string): string {
  const sys = String(a['gen_ai.system'] || '').toLowerCase();
  if (sys === 'openai' || model.includes('gpt')) return 'OpenAI';
  if (sys === 'ollama') return 'Ollama';
  if (sys === 'anthropic' || model.includes('claude')) return 'Anthropic';
  if (model.includes('gemini')) return 'Google';
  if (sys) return sys;
  return 'unknown';
}

function extractMetrics(spans: Span[]): LLMCallMetric[] {
  return spans
    .filter(s => {
      const a = s.attributes || {};
      return a['gen_ai.request.model'] || a['llm.request.model'] || a['gen_ai.response.model'];
    })
    .map(s => {
      const a = s.attributes || {};
      const model = String(a['gen_ai.request.model'] || a['gen_ai.response.model'] || a['llm.request.model'] || 'unknown');
      const pt = Number(a['gen_ai.usage.prompt_tokens'] || a['gen_ai.usage.input_tokens'] || a['llm.usage.prompt_tokens'] || 0);
      const ct = Number(a['gen_ai.usage.completion_tokens'] || a['gen_ai.usage.output_tokens'] || a['llm.usage.completion_tokens'] || 0);
      const tt = Number(a['gen_ai.usage.total_tokens'] || a['llm.usage.total_tokens'] || pt + ct);
      return {
        operationName: String(a['gen_ai.operation.name'] || s.name || 'LLM Call'),
        model,
        provider: prov(a, model),
        promptTokens: pt,
        completionTokens: ct,
        totalTokens: tt,
        duration: spanDurMs(s),
        cost: Number(a['gen_ai.usage.cost'] || 0),
        temperature: a['gen_ai.request.temperature'] ? Number(a['gen_ai.request.temperature']) : undefined,
      };
    });
}

function detectRAGSteps(spans: Span[]): RAGStep[] {
  const cats: [string, string, (s: Span) => boolean][] = [
    ['Extraction', '📄', s => /extract/.test((s.name || '').toLowerCase())],
    ['OCR', '👁️', s => (s.name || '').toLowerCase().includes('ocr')],
    ['Chunking', '✂️', s => (s.name || '').toLowerCase().includes('chunk')],
    ['Embedding', '🧬', s => {
      const n = (s.name || '').toLowerCase();
      const op = String(s.attributes?.['gen_ai.operation.name'] || '');
      return n.includes('embed') || ['embeddings', 'embedding', 'batch_embedding'].includes(op);
    }],
    ['Retrieval', '🔍', s => /search|retrieval|query/.test((s.name || '').toLowerCase())],
    ['Generation', '🤖', s => !!(s.attributes?.['gen_ai.request.model'] || s.attributes?.['llm.request.model'] || /completion|chat/.test((s.name || '').toLowerCase()))],
    ['Vector Store', '💾', s => /store|upsert/.test((s.name || '').toLowerCase())],
  ];
  const steps: RAGStep[] = [];
  let total = 0;
  for (const [name, icon, test] of cats) {
    const m = spans.filter(test);
    if (m.length > 0) {
      const d = m.reduce((sum, sp) => sum + spanDurMs(sp), 0);
      total += d;
      steps.push({ name, icon, spanCount: m.length, duration: d, percentage: 0 });
    }
  }
  if (total > 0) steps.forEach(s => (s.percentage = (s.duration / total) * 100));
  return steps;
}

function fmt(ms: number): string {
  if (ms <= 0) return '< 1ms';
  if (ms < 1) return `${(ms * 1000).toFixed(0)}µs`;
  if (ms < 1000) return `${ms.toFixed(1)}ms`;
  return `${(ms / 1000).toFixed(2)}s`;
}

function GenAISummaryPanel({ trace }: GenAISummaryPanelProps) {
  const metrics = useMemo(() => extractMetrics(trace.spans), [trace.spans]);
  const ragSteps = useMemo(() => detectRAGSteps(trace.spans), [trace.spans]);

  const totalTokens = metrics.reduce((s, m) => s + m.totalTokens, 0);
  const totalPrompt = metrics.reduce((s, m) => s + m.promptTokens, 0);
  const totalCompletion = metrics.reduce((s, m) => s + m.completionTokens, 0);
  const totalCost = metrics.reduce((s, m) => s + m.cost, 0);
  const totalDuration = metrics.reduce((s, m) => s + m.duration, 0);
  const traceDur = Math.max(0, new Date(trace.endUtc).getTime() - new Date(trace.startUtc).getTime());
  const models = [...new Set(metrics.map(m => m.model))];
  const providers = [...new Set(metrics.map(m => m.provider).filter(p => p !== 'unknown'))];

  return (
    <div className="genai-summary">
      <div className="genai-summary__header">
        <h3 className="genai-summary__title">📊 Synthèse GenAI / RAG</h3>
        <span className="genai-summary__trace-id">Trace: {trace.traceId.substring(0, 16)}…</span>
      </div>

      <div className="genai-summary__kpis">
        {[
          { icon: '🎯', val: totalTokens.toLocaleString(), label: 'Total Tokens', cls: 'tokens' },
          { icon: '💰', val: `$${totalCost.toFixed(6)}`, label: 'Coût estimé', cls: 'cost' },
          { icon: '⏱️', val: fmt(traceDur), label: 'Durée totale', cls: 'time' },
          { icon: '🤖', val: fmt(totalDuration), label: 'Temps LLM', cls: 'llm' },
          { icon: '📡', val: String(metrics.length), label: 'Appels LLM', cls: 'calls' },
          { icon: '🔗', val: String(trace.spans.length), label: 'Spans total', cls: 'spans' },
        ].map(k => (
          <div key={k.cls} className={`genai-summary__kpi genai-summary__kpi--${k.cls}`}>
            <span className="genai-summary__kpi-icon">{k.icon}</span>
            <div className="genai-summary__kpi-content">
              <span className="genai-summary__kpi-value">{k.val}</span>
              <span className="genai-summary__kpi-label">{k.label}</span>
            </div>
          </div>
        ))}
      </div>

      {totalTokens > 0 && (
        <div className="genai-summary__section">
          <h4 className="genai-summary__section-title">Répartition des Tokens</h4>
          <div className="genai-summary__token-bar">
            <div className="genai-summary__token-seg genai-summary__token-seg--prompt" style={{ width: `${(totalPrompt / totalTokens) * 100}%` }} />
            <div className="genai-summary__token-seg genai-summary__token-seg--completion" style={{ width: `${(totalCompletion / totalTokens) * 100}%` }} />
          </div>
          <div className="genai-summary__token-legend">
            <span><span className="genai-summary__token-dot genai-summary__token-dot--prompt" /> Prompt: {totalPrompt.toLocaleString()}</span>
            <span><span className="genai-summary__token-dot genai-summary__token-dot--completion" /> Completion: {totalCompletion.toLocaleString()}</span>
          </div>
        </div>
      )}

      {(models.length > 0 || providers.length > 0) && (
        <div className="genai-summary__section">
          <h4 className="genai-summary__section-title">Modèles & Fournisseurs</h4>
          <div className="genai-summary__tags">
            {providers.map(p => <span key={p} className="genai-summary__tag genai-summary__tag--provider">{p}</span>)}
            {models.map(m => <span key={m} className="genai-summary__tag genai-summary__tag--model">{m}</span>)}
          </div>
        </div>
      )}

      {ragSteps.length > 0 && (
        <div className="genai-summary__section">
          <h4 className="genai-summary__section-title">🔄 Pipeline RAG</h4>
          <div className="genai-summary__rag-steps">
            {ragSteps.map((step, i) => (
              <div key={step.name} className="genai-summary__rag-step">
                <div className="genai-summary__rag-step-head">
                  <span>{step.icon}</span>
                  <span className="genai-summary__rag-step-name">{step.name}</span>
                  <span className="badge">{step.spanCount} span(s)</span>
                </div>
                <div className="genai-summary__rag-step-bar">
                  <div className="genai-summary__rag-step-fill" style={{ width: `${step.percentage}%` }} />
                </div>
                <div className="genai-summary__rag-step-meta">
                  <span>{fmt(step.duration)}</span>
                  <span>{step.percentage.toFixed(1)}%</span>
                </div>
                {i < ragSteps.length - 1 && <div className="genai-summary__rag-arrow">→</div>}
              </div>
            ))}
          </div>
        </div>
      )}

      {metrics.length > 0 && (
        <div className="genai-summary__section">
          <h4 className="genai-summary__section-title">Détail par appel LLM</h4>
          <div className="genai-summary__calls">
            {metrics.map((m, i) => (
              <div key={i} className="genai-summary__call">
                <div className="genai-summary__call-head">
                  <span className="genai-summary__call-op">{m.operationName}</span>
                  <span className="badge badge--service">{m.provider}</span>
                </div>
                <div className="genai-summary__call-model">Modèle: <code>{m.model}</code></div>
                <div className="genai-summary__call-stats">
                  <span>🎯 {m.totalTokens.toLocaleString()} tok</span>
                  <span>⏱️ {fmt(m.duration)}</span>
                  <span>💰 ${m.cost.toFixed(6)}</span>
                  {m.temperature !== undefined && <span>🌡️ {m.temperature}</span>}
                </div>
                {m.totalTokens > 0 && (
                  <div className="genai-summary__call-token-bar">
                    <div className="genai-summary__token-seg genai-summary__token-seg--prompt" style={{ width: `${(m.promptTokens / m.totalTokens) * 100}%` }} />
                    <div className="genai-summary__token-seg genai-summary__token-seg--completion" style={{ width: `${(m.completionTokens / m.totalTokens) * 100}%` }} />
                  </div>
                )}
                <div className="genai-summary__call-token-detail">
                  <span>Prompt: {m.promptTokens.toLocaleString()}</span>
                  <span>Completion: {m.completionTokens.toLocaleString()}</span>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

export default GenAISummaryPanel;


// Types pour l'application OTLP Tenant Collector

export interface Tenant {
  id: string;
  name: string;
  createdAt: string;
}

export interface Span {
  spanId: string;
  parentSpanId: string | null;
  name: string;
  kind: number;
  startUtc: string;
  endUtc: string;
  durationMs: number;
  statusCode: number;
  statusMessage: string | null;
  attributes: Record<string, string | number | boolean>;
  events: SpanEvent[];
  resource: Record<string, string | number | boolean>;
  scope: {
    name: string;
    version: string;
  };
}

export interface SpanEvent {
  name: string;
  timeUtc: string;
  attributes: Record<string, string | number | boolean>;
}

export interface TraceGroup {
  traceId: string;
  startUtc: string;
  endUtc: string;
  spans: Span[];
}

export interface LogEntry {
  receivedUtc: string;
  severityNumber: number;
  severityText: string;
  body: string;
  traceId: string | null;
  spanId: string | null;
  attributes: Record<string, unknown>;
  resource: Record<string, unknown>;
  scope: Record<string, unknown> | null;
  serviceName: string | null;
}

export interface TraceFilters {
  serviceName: string;
  startTime: string;
  endTime: string;
}

export interface LogFilters {
  serviceName: string;
  severityLevel: string[];
  startTime: string;
  endTime: string;
}

export interface GenAIEvent {
  eventType: string;
  timestamp: string;
  attributes: Record<string, string | number | boolean>;
}

export interface LLMMetrics {
  totalTokens: number;
  promptTokens: number;
  completionTokens: number;
  model: string;
  duration: number;
}


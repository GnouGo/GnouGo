import { useState, useRef, useCallback } from 'react';
import Navigation from './Navigation';
import Header from './Header';
import ConfigPanel from './ConfigPanel';
import TraceFilters from './traces/TraceFilters';
import TraceList from './traces/TraceList';
import TraceDetail from './traces/TraceDetail';
import type { TraceGroup } from '../types';

function App() {
  const [tenantId, setTenantId] = useState<string>(localStorage.getItem('tenantId') || '');
  const [limit, setLimit] = useState<number>(50);
  const [serviceName, setServiceName] = useState<string>('');
  const [startUtc, setStartUtc] = useState<string>('');
  const [endUtc, setEndUtc] = useState<string>('');
  const [traceIdFilter, setTraceIdFilter] = useState<string>('');
  const [attributeContains, setAttributeContains] = useState<string>('');
  const [traces, setTraces] = useState<TraceGroup[]>([]);
  const [selectedTraceId, setSelectedTraceId] = useState<string | null>(null);
  const [trace, setTrace] = useState<TraceGroup | null>(null);
  const [error, setError] = useState<string>('');
  const [loading, setLoading] = useState<boolean>(false);
  const [connected, setConnected] = useState<boolean>(false);

  // Ref to hold the current EventSource so we can close it
  const eventSourceRef = useRef<EventSource | null>(null);
  // Ref to track the currently selected traceId for auto-refresh
  const selectedTraceIdRef = useRef<string | null>(null);
  selectedTraceIdRef.current = selectedTraceId;

  const handleClearFilters = (): void => {
    setServiceName('');
    setStartUtc('');
    setEndUtc('');
    setTraceIdFilter('');
    setAttributeContains('');
  };

  /** Fetch trace detail from API. Shared by loadTrace & refreshTrace. */
  const fetchTrace = useCallback(async (traceId: string): Promise<TraceGroup> => {
    const params = new URLSearchParams();
    if (localStorage.getItem('tenantId')) {
      params.append('tenantId', localStorage.getItem('tenantId')!);
    }

    const url = `/api/tenants/traces/${encodeURIComponent(traceId)}?${params.toString()}`;
    const response = await fetch(url);

    if (!response.ok) {
      throw new Error(`Failed to load trace (${response.status})`);
    }

    return await response.json() as TraceGroup;
  }, []);

  /** Full load with loading indicator (user clicks a trace). */
  const loadTrace = useCallback(async (traceId: string): Promise<void> => {
    setError('');
    setSelectedTraceId(traceId);
    setLoading(true);

    try {
      const data = await fetchTrace(traceId);
      setTrace(data);
    } catch (err) {
      setError((err as Error).message);
      setTrace(null);
    } finally {
      setLoading(false);
    }
  }, [fetchTrace]);

  /** Silent refresh — updates trace data WITHOUT setting loading=true,
   *  so GenAITraceDetail stays mounted and preserves its selectedSpanId. */
  const refreshTrace = useCallback(async (traceId: string): Promise<void> => {
    try {
      const data = await fetchTrace(traceId);
      setTrace(data);
    } catch {
      // Silently ignore — the existing detail stays visible
    }
  }, [fetchTrace]);

  const disconnect = useCallback(() => {
    if (eventSourceRef.current) {
      eventSourceRef.current.close();
      eventSourceRef.current = null;
    }
    setConnected(false);
  }, []);

  const connect = useCallback(() => {
    setError('');
    setSelectedTraceId(null);
    setTrace(null);
    setLoading(true);

    // Save settings
    localStorage.setItem('tenantId', tenantId);

    // Build SSE URL with filter params
    const params = new URLSearchParams();
    params.append('limit', limit.toString());
    if (tenantId) params.append('tenantId', tenantId);
    if (serviceName) params.append('serviceName', serviceName);
    if (startUtc) params.append('startUtc', startUtc);
    if (endUtc) params.append('endUtc', endUtc);
    if (traceIdFilter) params.append('traceIdFilter', traceIdFilter);
    if (attributeContains) params.append('attributeContains', attributeContains);

    const url = `/api/tenants/traces/stream?${params.toString()}`;
    const es = new EventSource(url);
    eventSourceRef.current = es;

    es.addEventListener('init', (e: MessageEvent) => {
      try {
        const data: TraceGroup[] = JSON.parse(e.data);
        setTraces(data);
        setConnected(true);
        setLoading(false);
      } catch (err) {
        setError('Failed to parse initial data');
        setLoading(false);
      }
    });

    es.addEventListener('update', (e: MessageEvent) => {
      try {
        const data: TraceGroup[] = JSON.parse(e.data);
        setTraces(data);

        // If we have a selected trace that got updated, silently refresh its detail
        // (no loading state → GenAITraceDetail stays mounted, preserving selectedSpanId)
        const currentId = selectedTraceIdRef.current;
        if (currentId) {
          const updatedTrace = data.find(t => t.traceId === currentId);
          if (updatedTrace) {
            refreshTrace(currentId);
          }
        }
      } catch {
        // Ignore parse errors on updates
      }
    });

    es.onerror = () => {
      // EventSource will auto-reconnect, but if it closes permanently we update state
      if (es.readyState === EventSource.CLOSED) {
        setConnected(false);
        setLoading(false);
        setError('Connection lost. Click Connect to reconnect.');
        eventSourceRef.current = null;
      }
    };
  }, [tenantId, limit, serviceName, startUtc, endUtc, traceIdFilter, attributeContains, refreshTrace]);

  const handleToggleConnection = useCallback(() => {
    if (connected) {
      disconnect();
    } else {
      connect();
    }
  }, [connected, connect, disconnect]);

  return (
    <div className="app">
      <Navigation />
      
      <div className="app__container">
        <Header
          connected={connected}
          onToggleConnection={handleToggleConnection}
          loading={loading}
        />
        
        <ConfigPanel
          tenantId={tenantId}
          limit={limit}
          error={error}
          onTenantIdChange={setTenantId}
          onLimitChange={setLimit}
        />

        <TraceFilters
          serviceName={serviceName}
          startUtc={startUtc}
          endUtc={endUtc}
          traceIdFilter={traceIdFilter}
          attributeContains={attributeContains}
          onServiceNameChange={setServiceName}
          onStartUtcChange={setStartUtc}
          onEndUtcChange={setEndUtc}
          onTraceIdFilterChange={setTraceIdFilter}
          onAttributeContainsChange={setAttributeContains}
          onClearFilters={handleClearFilters}
        />

        <div className="grid">
          <TraceList
            traces={traces}
            selectedTraceId={selectedTraceId}
            onTraceClick={loadTrace}
          />
          
          <TraceDetail
            trace={trace}
            selectedTraceId={selectedTraceId}
            loading={loading}
          />
        </div>
      </div>
    </div>
  );
}

export default App;


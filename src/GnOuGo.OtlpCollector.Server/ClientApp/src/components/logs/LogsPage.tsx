import { useState } from 'react';
import Navigation from '../Navigation';
import Header from '../Header';
import ConfigPanel from '../ConfigPanel';
import LogFilters from './LogFilters';
import LogList from './LogList';
import LogDetail from './LogDetail';
import type { LogEntry } from '../../types';

const MAX_CLIENT_LOGS = 1000;

function LogsPage() {
  const [tenantId, setTenantId] = useState<string>(localStorage.getItem('tenantId') || '');
  const [limit, setLimit] = useState<number>(100);
  const [serviceName, setServiceName] = useState<string>('');
  const [startUtc, setStartUtc] = useState<string>('');
  const [endUtc, setEndUtc] = useState<string>('');
  const [severityLevels, setSeverityLevels] = useState<string[]>([]);
  const [traceIdFilter, setTraceIdFilter] = useState<string>('');
  const [attributeContains, setAttributeContains] = useState<string>('');
  const [logs, setLogs] = useState<LogEntry[]>([]);
  const [selectedIdx, setSelectedIdx] = useState<number | null>(null);
  const [error, setError] = useState<string>('');
  const [loading, setLoading] = useState<boolean>(false);

  const selectedLog = selectedIdx !== null ? logs[selectedIdx] ?? null : null;

  const loadRecentLogs = async (): Promise<void> => {
    setError('');
    setLoading(true);

    localStorage.setItem('tenantId', tenantId);

    try {
      const params = new URLSearchParams();
      if (tenantId) params.append('tenantId', tenantId);
      params.append('limit', Math.min(limit, MAX_CLIENT_LOGS).toString());
      if (serviceName) params.append('serviceName', serviceName);
      if (startUtc) params.append('startUtc', startUtc);
      if (endUtc) params.append('endUtc', endUtc);
      if (traceIdFilter) params.append('traceIdFilter', traceIdFilter);
      if (attributeContains) params.append('attributeContains', attributeContains);
      for (const lv of severityLevels) {
        params.append('severityLevels', lv);
      }

      const url = `/api/tenants/logs/recent?${params.toString()}`;
      const response = await fetch(url);

      if (!response.ok) {
        throw new Error(`Failed to load logs (${response.status})`);
      }

      const data: LogEntry[] = await response.json();
      // Safety cap: keep only the last MAX_CLIENT_LOGS entries
      setLogs(data.length > MAX_CLIENT_LOGS ? data.slice(-MAX_CLIENT_LOGS) : data);
    } catch (err) {
      setError((err as Error).message);
      setLogs([]);
    } finally {
      setLoading(false);
    }
  };

  const handleClearFilters = (): void => {
    setServiceName('');
    setStartUtc('');
    setEndUtc('');
    setSeverityLevels([]);
    setTraceIdFilter('');
    setAttributeContains('');
  };

  return (
    <div className="app">
      <Navigation />
      
      <div className="app__container">
        <Header
          connected={false}
          onToggleConnection={loadRecentLogs}
          loading={loading}
        />
        
        <ConfigPanel
          tenantId={tenantId}
          limit={limit}
          error={error}
          onTenantIdChange={setTenantId}
          onLimitChange={(v) => setLimit(Math.min(v, MAX_CLIENT_LOGS))}
        />

        <LogFilters
          serviceName={serviceName}
          startUtc={startUtc}
          endUtc={endUtc}
          severityLevels={severityLevels}
          traceIdFilter={traceIdFilter}
          attributeContains={attributeContains}
          onServiceNameChange={setServiceName}
          onStartUtcChange={setStartUtc}
          onEndUtcChange={setEndUtc}
          onSeverityLevelsChange={setSeverityLevels}
          onTraceIdFilterChange={setTraceIdFilter}
          onAttributeContainsChange={setAttributeContains}
          onClearFilters={handleClearFilters}
        />

        <div className="grid">
          <LogList
            logs={logs}
            selectedIdx={selectedIdx}
            onSelectLog={setSelectedIdx}
            loading={loading}
          />
          <LogDetail log={selectedLog} />
        </div>
      </div>
    </div>
  );
}

export default LogsPage;


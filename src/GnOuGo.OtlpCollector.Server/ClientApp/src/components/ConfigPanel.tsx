import { useState } from 'react';

interface ConfigPanelProps {
  tenantId: string;
  limit: number;
  error: string;
  onTenantIdChange: (value: string) => void;
  onLimitChange: (value: number) => void;
}

function ConfigPanel({ 
  tenantId, 
  limit, 
  error,
  onTenantIdChange, 
  onLimitChange 
}: ConfigPanelProps) {
  const [showPurgeModal, setShowPurgeModal] = useState(false);
  const [purging, setPurging] = useState(false);
  const [purgeMessage, setPurgeMessage] = useState('');

  const handlePurge = async () => {
    setPurging(true);
    setPurgeMessage('');
    try {
      const params = new URLSearchParams();
      if (tenantId) {
        params.append('tenantId', tenantId);
      }
      const res = await fetch(`/api/tenants/data?${params.toString()}`, { method: 'DELETE' });
      if (!res.ok) throw new Error(`Failed (${res.status})`);
      const data = await res.json();
      setPurgeMessage(data.message ?? 'Data purged');
    } catch (e) {
      setPurgeMessage((e as Error).message);
    } finally {
      setPurging(false);
      setShowPurgeModal(false);
    }
  };

  return (
    <section className="panel">
      <div className="panel__row">
        <div className="field">
          <label className="field__label" htmlFor="tenantId">Tenant ID</label>
          <input
            id="tenantId"
            className="field__input"
            type="text"
            value={tenantId}
            onChange={(e) => onTenantIdChange(e.target.value)}
            placeholder="Leave empty for DevMode (no tenant)"
          />
        </div>

        <div className="field">
          <label className="field__label" htmlFor="limit">Limit</label>
          <input
            id="limit"
            className="field__input"
            type="number"
            min="1"
            max="500"
            value={limit}
            onChange={(e) => onLimitChange(parseInt(e.target.value || '50', 10))}
          />
        </div>

        <div className="field" style={{ alignSelf: 'flex-end' }}>
          <button
            className="button button--danger button--small"
            onClick={() => setShowPurgeModal(true)}
            title="Delete all traces & logs for this tenant (or all unassigned data if empty)"
          >
            🗑️ Purge Data
          </button>
        </div>
      </div>

      {error && <div className="error">{error}</div>}
      {purgeMessage && <div className="error" style={{ color: '#4ade80' }}>{purgeMessage}</div>}

      {/* Purge confirmation modal */}
      {showPurgeModal && (
        <div className="modal-overlay" onClick={() => setShowPurgeModal(false)}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal__header">
              <h3 className="modal__title">⚠️ Purge Telemetry Data</h3>
            </div>
            <div className="modal__body">
              <p>This will permanently delete <strong>all traces and logs</strong> for tenant:</p>
              <code style={{ display: 'block', padding: '8px', margin: '8px 0', background: 'rgba(255,255,255,0.05)', borderRadius: '4px', fontSize: '12px' }}>
                {tenantId}
              </code>
              <p>The tenant itself will be kept. This action cannot be undone.</p>
            </div>
            <div className="modal__footer">
              <button className="button" onClick={() => setShowPurgeModal(false)}>Cancel</button>
              <button className="button button--danger" onClick={handlePurge} disabled={purging}>
                {purging ? 'Purging...' : 'Confirm Purge'}
              </button>
            </div>
          </div>
        </div>
      )}
    </section>
  );
}

export default ConfigPanel;

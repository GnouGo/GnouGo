import { useState, useEffect, useCallback } from 'react';
import { fetchTenants, fetchSecrets, setSecret, getSecretValue, deleteSecret, fetchVersions } from '../api';
import type { TenantDto, SecretDto, SecretVersionDto } from '../types';

export function SecretsPage() {
  const [tenants, setTenants] = useState<TenantDto[]>([]);
  const [selectedTenantId, setSelectedTenantId] = useState<string | null>(null);
  const [secrets, setSecrets] = useState<SecretDto[]>([]);
  const [author, setAuthor] = useState('');

  // Form
  const [newKey, setNewKey] = useState('');
  const [newValue, setNewValue] = useState('');
  const [saving, setSaving] = useState(false);

  // Detail view
  const [detailKey, setDetailKey] = useState<string | null>(null);
  const [detailValue, setDetailValue] = useState<string | null>(null);
  const [versions, setVersions] = useState<SecretVersionDto[]>([]);

  const loadTenants = useCallback(() => {
    fetchTenants().then(t => setTenants(t)).catch(console.error);
  }, []);

  const loadSecrets = useCallback(() => {
    fetchSecrets(selectedTenantId ?? undefined).then(setSecrets).catch(console.error);
  }, [selectedTenantId]);

  useEffect(loadTenants, [loadTenants]);
  useEffect(loadSecrets, [loadSecrets]);

  const handleSet = async () => {
    if (!newKey.trim() || !newValue.trim() || !author.trim()) return;
    setSaving(true);
    try {
      await setSecret(newKey.trim(), { value: newValue.trim(), author: author.trim(), tenantId: selectedTenantId });
      setNewKey('');
      setNewValue('');
      loadSecrets();
    } catch (e) { console.error(e); }
    finally { setSaving(false); }
  };

  const handleReveal = async (key: string) => {
    if (!author.trim()) { alert('Please enter an author name'); return; }
    try {
      const sv = await getSecretValue(key, selectedTenantId, author.trim());
      setDetailKey(key);
      setDetailValue(sv.value);
      const vers = await fetchVersions(key, selectedTenantId);
      setVersions(vers);
    } catch (e) { console.error(e); }
  };

  const handleDelete = async (key: string) => {
    if (!author.trim()) { alert('Please enter an author name'); return; }
    if (!confirm(`Delete secret "${key}"?`)) return;
    await deleteSecret(key, selectedTenantId, author.trim());
    setDetailKey(null);
    loadSecrets();
  };

  return (
    <section className="page">
      <h2 className="page__title">Secrets</h2>

      <div className="filter-bar">
        <label className="filter-bar__label">
          Tenant:
          <select className="filter-bar__select" value={selectedTenantId ?? ''} onChange={e => { setSelectedTenantId(e.target.value || null); setDetailKey(null); }}>
            <option value="">Default (no tenant)</option>
            {tenants.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
          </select>
        </label>
        <label className="filter-bar__label">
          Author:
          <input className="filter-bar__input" placeholder="Your name" value={author} onChange={e => setAuthor(e.target.value)} />
        </label>
      </div>

      {/* New secret form */}
      <div className="form form--inline">
        <input className="form__input" placeholder="Key" value={newKey} onChange={e => setNewKey(e.target.value)} />
        <input className="form__input" placeholder="Value" type="password" value={newValue} onChange={e => setNewValue(e.target.value)} />
        <button className="form__button" disabled={saving || !newKey.trim() || !newValue.trim() || !author.trim()} onClick={handleSet}>
          {saving ? 'Saving…' : 'Set Secret'}
        </button>
      </div>

      {/* Secrets list */}
      <table className="table">
        <thead>
          <tr className="table__header">
            <th className="table__cell">Key</th>
            <th className="table__cell">Version</th>
            <th className="table__cell">Created</th>
            <th className="table__cell">Actions</th>
          </tr>
        </thead>
        <tbody>
          {secrets.map(s => (
            <tr key={s.id} className={`table__row ${detailKey === s.key ? 'table__row--selected' : ''}`}>
              <td className="table__cell table__cell--mono">{s.key}</td>
              <td className="table__cell">v{s.latestVersion}</td>
              <td className="table__cell">{new Date(s.createdAt).toLocaleString()}</td>
              <td className="table__cell">
                <button className="btn btn--sm" onClick={() => handleReveal(s.key)}>👁 Reveal</button>
                <button className="btn btn--danger btn--sm" onClick={() => handleDelete(s.key)}>Delete</button>
              </td>
            </tr>
          ))}
          {secrets.length === 0 && (
            <tr className="table__row"><td className="table__cell" colSpan={4}>No secrets</td></tr>
          )}
        </tbody>
      </table>

      {/* Detail panel */}
      {detailKey && (
        <div className="detail-panel">
          <h3 className="detail-panel__title">🔑 {detailKey}</h3>
          <div className="detail-panel__value">
            <code>{detailValue}</code>
          </div>
          <h4>Version History</h4>
          <ul className="version-list">
            {versions.map(v => (
              <li key={v.id} className="version-list__item">
                <strong>v{v.version}</strong> — {new Date(v.createdAt).toLocaleString()} by {v.createdBy}
              </li>
            ))}
          </ul>
          <button className="btn btn--sm" onClick={() => setDetailKey(null)}>Close</button>
        </div>
      )}
    </section>
  );
}



import { useState, useEffect, useCallback } from 'react';
import { fetchAudit, fetchTenants } from '../api';
import type { AuditEntryDto, TenantDto } from '../types';

export function AuditPage() {
  const [entries, setEntries] = useState<AuditEntryDto[]>([]);
  const [tenants, setTenants] = useState<TenantDto[]>([]);
  const [tenantFilter, setTenantFilter] = useState<string>('');
  const [keyFilter, setKeyFilter] = useState('');
  const [page, setPage] = useState(0);
  const pageSize = 30;

  const load = useCallback(() => {
    fetchAudit(tenantFilter || undefined, keyFilter || undefined, page * pageSize, pageSize)
      .then(setEntries).catch(console.error);
  }, [tenantFilter, keyFilter, page]);

  useEffect(() => { fetchTenants().then(setTenants).catch(console.error); }, []);
  useEffect(load, [load]);

  return (
    <section className="page">
      <h2 className="page__title">Audit Log</h2>

      <div className="filter-bar">
        <label className="filter-bar__label">
          Tenant:
          <select className="filter-bar__select" value={tenantFilter} onChange={e => { setTenantFilter(e.target.value); setPage(0); }}>
            <option value="">All</option>
            {tenants.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
          </select>
        </label>
        <label className="filter-bar__label">
          Secret key:
          <input className="filter-bar__input" placeholder="Filter by key" value={keyFilter} onChange={e => { setKeyFilter(e.target.value); setPage(0); }} />
        </label>
      </div>

      <table className="table">
        <thead>
          <tr className="table__header">
            <th className="table__cell">Timestamp</th>
            <th className="table__cell">Operation</th>
            <th className="table__cell">Author</th>
            <th className="table__cell">Secret</th>
            <th className="table__cell">Details</th>
          </tr>
        </thead>
        <tbody>
          {entries.map(e => (
            <tr key={e.id} className="table__row">
              <td className="table__cell">{new Date(e.timestamp).toLocaleString()}</td>
              <td className="table__cell"><span className={`badge badge--${e.operation.toLowerCase()}`}>{e.operation}</span></td>
              <td className="table__cell">{e.author}</td>
              <td className="table__cell table__cell--mono">{e.secretKey ?? '—'}</td>
              <td className="table__cell">{e.details ?? ''}</td>
            </tr>
          ))}
          {entries.length === 0 && (
            <tr className="table__row"><td className="table__cell" colSpan={5}>No audit entries</td></tr>
          )}
        </tbody>
      </table>

      <div className="pagination">
        <button className="btn btn--sm" disabled={page === 0} onClick={() => setPage(p => p - 1)}>← Prev</button>
        <span className="pagination__info">Page {page + 1}</span>
        <button className="btn btn--sm" disabled={entries.length < pageSize} onClick={() => setPage(p => p + 1)}>Next →</button>
      </div>
    </section>
  );
}


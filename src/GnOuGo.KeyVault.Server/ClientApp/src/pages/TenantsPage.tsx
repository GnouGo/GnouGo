import { useState, useEffect, useCallback } from 'react';
import { fetchTenants, createTenant, deleteTenant } from '../api';
import type { TenantDto } from '../types';

export function TenantsPage() {
  const [tenants, setTenants] = useState<TenantDto[]>([]);
  const [name, setName] = useState('');
  const [author, setAuthor] = useState('');
  const [loading, setLoading] = useState(false);

  const load = useCallback(() => {
    fetchTenants().then(setTenants).catch(console.error);
  }, []);

  useEffect(load, [load]);

  const handleCreate = async () => {
    if (!name.trim() || !author.trim()) return;
    setLoading(true);
    try {
      await createTenant({ name: name.trim(), author: author.trim() });
      setName('');
      load();
    } catch (e) { console.error(e); }
    finally { setLoading(false); }
  };

  const handleDelete = async (id: string) => {
    if (!author.trim()) { alert('Please enter an author name'); return; }
    await deleteTenant(id, author.trim());
    load();
  };

  return (
    <section className="page">
      <h2 className="page__title">Tenants</h2>

      <div className="form form--inline">
        <input className="form__input" placeholder="Tenant name" value={name} onChange={e => setName(e.target.value)} />
        <input className="form__input" placeholder="Author" value={author} onChange={e => setAuthor(e.target.value)} />
        <button className="form__button" disabled={loading || !name.trim() || !author.trim()} onClick={handleCreate}>
          {loading ? 'Creating…' : 'Create Tenant'}
        </button>
      </div>

      <table className="table">
        <thead>
          <tr className="table__header">
            <th className="table__cell">Name</th>
            <th className="table__cell">Created</th>
            <th className="table__cell">By</th>
            <th className="table__cell">Actions</th>
          </tr>
        </thead>
        <tbody>
          {tenants.map(t => (
            <tr key={t.id} className="table__row">
              <td className="table__cell">{t.name}</td>
              <td className="table__cell">{new Date(t.createdAt).toLocaleString()}</td>
              <td className="table__cell">{t.createdBy}</td>
              <td className="table__cell">
                <button className="btn btn--danger btn--sm" onClick={() => handleDelete(t.id)}>Delete</button>
              </td>
            </tr>
          ))}
          {tenants.length === 0 && (
            <tr className="table__row"><td className="table__cell" colSpan={4}>No tenants yet</td></tr>
          )}
        </tbody>
      </table>
    </section>
  );
}


import { useState, useEffect } from 'react';
import Navigation from '../Navigation';
import TenantList from './TenantList';
import CreateTenantForm from './CreateTenantForm';
import type { Tenant } from '../../types';

function TenantsPage() {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [loading, setLoading] = useState<boolean>(false);
  const [error, setError] = useState<string>('');
  const [showCreateForm, setShowCreateForm] = useState<boolean>(false);

  const loadTenants = async (): Promise<void> => {
    setError('');
    setLoading(true);

    try {
      const response = await fetch('/api/admin/tenants');

      if (!response.ok) {
        throw new Error(`Failed to load tenants (${response.status})`);
      }

      const data: Tenant[] = await response.json();
      setTenants(data);
    } catch (err) {
      setError((err as Error).message);
      setTenants([]);
    } finally {
      setLoading(false);
    }
  };

  const handleTenantCreated = (newTenant: Tenant): void => {
    setTenants([...tenants, newTenant]);
    setShowCreateForm(false);
  };

  const handleTenantDeleted = (tenantId: string): void => {
    setTenants(tenants.filter(t => t.id !== tenantId));
  };

  useEffect(() => {
    loadTenants();
  }, []);

  return (
    <div className="app">
      <Navigation />
      
      <div className="app__container">
        <header className="header">
          <div className="header__content">
            <h1 className="header__title">Tenant Management</h1>
            <button 
              className="button button--primary"
              onClick={() => setShowCreateForm(!showCreateForm)}
            >
              {showCreateForm ? '✖ Cancel' : '➕ Create Tenant'}
            </button>
          </div>
        </header>

        {/* Bouton de chargement */}
        <section className="panel">
          <button 
            className={`button ${loading ? 'button--loading' : ''}`}
            onClick={loadTenants}
            disabled={loading}
          >
            {loading ? 'Loading...' : 'Refresh Tenants'}
          </button>

          {error && (
            <div className="error">
              {error}
            </div>
          )}
        </section>

        {/* Create Tenant Form */}
        {showCreateForm && (
          <CreateTenantForm
            onTenantCreated={handleTenantCreated}
            onCancel={() => setShowCreateForm(false)}
          />
        )}

        {/* Tenant List */}
        <TenantList tenants={tenants} loading={loading} onTenantDeleted={handleTenantDeleted} />
      </div>
    </div>
  );
}

export default TenantsPage;


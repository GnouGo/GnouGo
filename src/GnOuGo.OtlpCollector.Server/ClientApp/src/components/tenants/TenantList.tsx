import TenantCard from './TenantCard';
import type { Tenant } from '../../types';

interface TenantListProps {
  tenants: Tenant[];
  loading: boolean;
  onTenantDeleted?: (tenantId: string) => void;
}

function TenantList({ tenants, loading, onTenantDeleted }: TenantListProps) {
  if (loading) {
    return (
      <section className="panel">
        <div className="tenant-list">
          <div className="tenant-list__loading">
            Loading tenants...
          </div>
        </div>
      </section>
    );
  }

  return (
    <section className="panel">
      <div className="tenant-list">
        <div className="tenant-list__header">
          <h2 className="tenant-list__title">Tenants</h2>
          <span className="badge">{tenants.length}</span>
        </div>

        <div className="tenant-list__grid">
          {tenants.length === 0 ? (
            <div className="tenant-list__empty">
              No tenants found. Create one to get started.
            </div>
          ) : (
            tenants.map((tenant) => (
              <TenantCard key={tenant.id} tenant={tenant} onTenantDeleted={onTenantDeleted} />
            ))
          )}
        </div>
      </div>
    </section>
  );
}

export default TenantList;


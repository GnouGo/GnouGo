import { useState } from 'react';
import type { Tenant } from '../../types';

interface CreateTenantFormProps {
  onTenantCreated: (tenant: Tenant) => void;
  onCancel: () => void;
}

function CreateTenantForm({ onTenantCreated, onCancel }: CreateTenantFormProps) {
  const [tenantName, setTenantName] = useState<string>('');
  const [retentionMinutes, setRetentionMinutes] = useState<number>(43200); // 30 jours par défaut
  const [error, setError] = useState<string>('');
  const [loading, setLoading] = useState<boolean>(false);
  const [showSuccess, setShowSuccess] = useState<boolean>(false);
  const [createdTenant, setCreatedTenant] = useState<{ id: string; name: string } | null>(null);

  const handleSubmit = async (e: React.FormEvent<HTMLFormElement>): Promise<void> => {
    e.preventDefault();
    setError('');

    // Validation
    if (!tenantName.trim()) {
      setError('Tenant name is required');
      return;
    }

    if (retentionMinutes < 1) {
      setError('Retention minutes must be greater than 0');
      return;
    }

    setLoading(true);

    try {
      const response = await fetch('/api/admin/tenants', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({
          name: tenantName.trim(),
          retentionMinutes
        })
      });

      if (!response.ok) {
        const errorText = await response.text();
        throw new Error(errorText || `Failed to create tenant (${response.status})`);
      }

      const result = await response.json();
      
      // Sauvegarder les infos du tenant créé
      setCreatedTenant({ id: result.id, name: result.name });
      setShowSuccess(true);
      
      // Créer un objet Tenant pour le callback
      const newTenant: Tenant = {
        id: result.id,
        name: result.name,
        createdAt: new Date().toISOString()
      };
      
      // Reset form
      setTenantName('');
      setRetentionMinutes(43200); // 30 jours
      
      // Attendre que l'utilisateur ferme le modal avant de notifier
      setTimeout(() => {
        onTenantCreated(newTenant);
      }, 100);
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setLoading(false);
    }
  };

  const handleSuccessClose = (): void => {
    setShowSuccess(false);
    setCreatedTenant(null);
  };

  return (
    <>
      <section className="panel">
        <div className="create-tenant-form">
          <h2 className="create-tenant-form__title">Create New Tenant</h2>

          <form onSubmit={handleSubmit} className="create-tenant-form__form">
            <div className="field">
              <label htmlFor="tenantName" className="field__label">
                Tenant Name *
              </label>
              <input
                id="tenantName"
                type="text"
                className="field__input"
                value={tenantName}
                onChange={(e) => setTenantName(e.target.value)}
                placeholder="ACME Corporation"
                required
                disabled={loading}
              />
              <span className="field__hint">
                Friendly display name for this tenant
              </span>
            </div>

            <div className="field">
              <label htmlFor="retentionMinutes" className="field__label">
                Retention Minutes
              </label>
              <input
                id="retentionMinutes"
                type="number"
                className="field__input"
                value={retentionMinutes}
                onChange={(e) => setRetentionMinutes(parseInt(e.target.value, 10))}
                min="1"
                disabled={loading}
              />
              <span className="field__hint">
                Number of minutes to retain telemetry data (e.g., 1440 = 1 day, 43200 = 30 days)
              </span>
            </div>

            {error && (
              <div className="error">
                {error}
              </div>
            )}

            <div className="create-tenant-form__actions">
              <button 
                type="button"
                className="button"
                onClick={onCancel}
                disabled={loading}
              >
                Cancel
              </button>
              <button 
                type="submit"
                className={`button button--primary ${loading ? 'button--loading' : ''}`}
                disabled={loading}
              >
                {loading ? 'Creating...' : 'Create Tenant'}
              </button>
            </div>
          </form>
        </div>
      </section>

      {showSuccess && createdTenant && (
        <div className="modal-overlay" onClick={handleSuccessClose}>
          <div className="modal-dialog" onClick={(e) => e.stopPropagation()}>
            <div className="modal-header">
              <h3 className="modal-title">✅ Tenant Created Successfully</h3>
            </div>
            <div className="modal-body">
              <p><strong>Tenant ID:</strong> {createdTenant.id}</p>
              <p><strong>Tenant Name:</strong> {createdTenant.name}</p>
              <p style={{ marginTop: '1rem', fontSize: '0.9rem', color: '#666' }}>
                The tenant has been created and is ready to receive telemetry data.
              </p>
            </div>
            <div className="modal-footer">
              <button
                type="button"
                className="button button--primary"
                onClick={handleSuccessClose}
              >
                OK
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

export default CreateTenantForm;

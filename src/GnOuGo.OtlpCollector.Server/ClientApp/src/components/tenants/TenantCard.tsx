import { useState } from 'react';
import type { Tenant } from '../../types';
import ConfirmDialog from '../common/ConfirmDialog';

interface TenantCardProps {
  tenant: Tenant;
  onTenantDeleted?: (tenantId: string) => void;
}

function TenantCard({ tenant, onTenantDeleted }: TenantCardProps) {
  const [deleting, setDeleting] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [showSuccessMessage, setShowSuccessMessage] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string>('');

  const formatDate = (dateString: string): string => {
    try {
      return new Date(dateString).toLocaleString(navigator.language, {
        year: 'numeric',
        month: '2-digit',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit'
      });
    } catch {
      return dateString;
    }
  };

  const handleSelectTenant = (): void => {
    // Sauvegarder dans localStorage et rediriger vers traces
    localStorage.setItem('tenantId', tenant.id);
    window.location.href = '/';
  };

  const handleDeleteClick = (): void => {
    setShowDeleteConfirm(true);
  };

  const handleDeleteConfirm = async (): Promise<void> => {
    setShowDeleteConfirm(false);
    setDeleting(true);
    setErrorMessage('');

    try {
      const response = await fetch(`/api/admin/tenants/${tenant.id}`, {
        method: 'DELETE'
      });

      if (!response.ok) {
        const errorText = await response.text();
        throw new Error(errorText || `Failed to delete tenant (${response.status})`);
      }

      setShowSuccessMessage(true);
      
      // Attendre un peu avant de notifier le parent
      setTimeout(() => {
        if (onTenantDeleted) {
          onTenantDeleted(tenant.id);
        }
      }, 1500);
    } catch (err) {
      setErrorMessage((err as Error).message);
    } finally {
      setDeleting(false);
    }
  };

  return (
    <>
      <article className="tenant-card">
        <div className="tenant-card__header">
          <h3 className="tenant-card__name">{tenant.name}</h3>
          <span className="badge badge--service">{tenant.id}</span>
        </div>

        <div className="tenant-card__meta">
          <div className="tenant-card__info">
            <span className="tenant-card__label">Created:</span>
            <span className="tenant-card__value">{formatDate(tenant.createdAt)}</span>
          </div>
        </div>

        {errorMessage && (
          <div className="error" style={{ marginTop: '1rem' }}>
            {errorMessage}
          </div>
        )}

        <div className="tenant-card__actions">
          <button 
            className="button button--primary"
            onClick={handleSelectTenant}
            disabled={deleting}
          >
            View Traces
          </button>
          <button 
            className="button button--danger"
            onClick={handleDeleteClick}
            disabled={deleting}
          >
            {deleting ? 'Deleting...' : 'Delete'}
          </button>
        </div>
      </article>

      {showDeleteConfirm && (
        <ConfirmDialog
          title="Delete Tenant"
          message={`Are you sure you want to delete tenant "${tenant.name}" (${tenant.id})?\n\nThis will permanently delete all associated traces and logs. This action cannot be undone.`}
          confirmText="Delete"
          cancelText="Cancel"
          variant="danger"
          onConfirm={handleDeleteConfirm}
          onCancel={() => setShowDeleteConfirm(false)}
        />
      )}

      {showSuccessMessage && (
        <div className="modal-overlay" onClick={() => setShowSuccessMessage(false)}>
          <div className="modal-dialog" onClick={(e) => e.stopPropagation()}>
            <div className="modal-header">
              <h3 className="modal-title">Success</h3>
            </div>
            <div className="modal-body">
              <p>Tenant "{tenant.name}" has been deleted successfully.</p>
            </div>
            <div className="modal-footer">
              <button
                type="button"
                className="button button--primary"
                onClick={() => setShowSuccessMessage(false)}
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

export default TenantCard;


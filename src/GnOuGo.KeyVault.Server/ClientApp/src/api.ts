import type { TenantDto, SecretDto, SecretValueDto, SecretVersionDto, AuditEntryDto, CreateTenantRequest, SetSecretRequest } from './types';

const BASE = '';

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.text();
    throw new Error(`HTTP ${res.status}: ${body}`);
  }
  return res.json() as Promise<T>;
}

// ── Tenants ──────────────────────────────────────────────────────────

export const fetchTenants = (): Promise<TenantDto[]> =>
  fetch(`${BASE}/api/tenants`).then(r => json(r));

export const createTenant = (req: CreateTenantRequest): Promise<TenantDto> =>
  fetch(`${BASE}/api/tenants`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(req),
  }).then(r => json(r));

export const deleteTenant = (tenantId: string, author: string): Promise<void> =>
  fetch(`${BASE}/api/tenants/${tenantId}?author=${encodeURIComponent(author)}`, {
    method: 'DELETE',
  }).then(r => { if (!r.ok) throw new Error(`HTTP ${r.status}`); });

// ── Secrets ──────────────────────────────────────────────────────────

export const fetchSecrets = (tenantId?: string | null): Promise<SecretDto[]> => {
  const qs = tenantId ? `?tenantId=${tenantId}` : '';
  return fetch(`${BASE}/api/secrets${qs}`).then(r => json(r));
};

export const setSecret = (key: string, req: SetSecretRequest): Promise<SecretValueDto> =>
  fetch(`${BASE}/api/secrets/${encodeURIComponent(key)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(req),
  }).then(r => json(r));

export const getSecretValue = (key: string, tenantId: string | null, author: string): Promise<SecretValueDto> => {
  const qs = new URLSearchParams({ author });
  if (tenantId) qs.set('tenantId', tenantId);
  return fetch(`${BASE}/api/secrets/${encodeURIComponent(key)}/value?${qs}`).then(r => json(r));
};

export const deleteSecret = (key: string, tenantId: string | null, author: string): Promise<void> => {
  const qs = new URLSearchParams({ author });
  if (tenantId) qs.set('tenantId', tenantId);
  return fetch(`${BASE}/api/secrets/${encodeURIComponent(key)}?${qs}`, {
    method: 'DELETE',
  }).then(r => { if (!r.ok) throw new Error(`HTTP ${r.status}`); });
};

export const fetchVersions = (key: string, tenantId?: string | null): Promise<SecretVersionDto[]> => {
  const qs = tenantId ? `?tenantId=${tenantId}` : '';
  return fetch(`${BASE}/api/secrets/${encodeURIComponent(key)}/versions${qs}`).then(r => json(r));
};

// ── Audit ────────────────────────────────────────────────────────────

export const fetchAudit = (tenantId?: string | null, key?: string | null, skip = 0, take = 50): Promise<AuditEntryDto[]> => {
  const qs = new URLSearchParams({ skip: String(skip), take: String(take) });
  if (tenantId) qs.set('tenantId', tenantId);
  if (key) qs.set('key', key);
  return fetch(`${BASE}/api/audit?${qs}`).then(r => json(r));
};


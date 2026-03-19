export interface TenantDto {
  id: string;
  name: string;
  createdAt: string;
  createdBy: string;
}

export interface SecretDto {
  id: string;
  key: string;
  tenantId: string | null;
  tenantName: string | null;
  latestVersion: number;
  createdAt: string;
  createdBy: string;
}

export interface SecretValueDto {
  id: string;
  key: string;
  value: string;
  version: number;
  tenantId: string | null;
  createdAt: string;
}

export interface SecretVersionDto {
  id: string;
  version: number;
  createdAt: string;
  createdBy: string;
}

export interface AuditEntryDto {
  id: string;
  tenantId: string | null;
  secretKey: string | null;
  operation: string;
  author: string;
  timestamp: string;
  details: string | null;
}

export interface CreateTenantRequest {
  name: string;
  author: string;
}

export interface SetSecretRequest {
  value: string;
  author: string;
  tenantId?: string | null;
}


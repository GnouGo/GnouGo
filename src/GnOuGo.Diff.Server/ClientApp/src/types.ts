// Types pour les modèles de données de l'API GnOuGo.Diff

export interface RevisionDto {
  id: number;
  entityType: string;
  entityId: string;
  timestamp: string;
  author: string;
  currentValue: string;
  diffFromPrevious: string | null;
  isFirstRevision: boolean;
}

export interface ComparisonResult {
  fromRevision: RevisionDto;
  toRevision: RevisionDto;
  unifiedDiff: string;
  stats: DiffStats;
}

export interface DiffStats {
  linesAdded: number;
  linesDeleted: number;
  linesModified: number;
  linesUnchanged: number;
}

export interface EntityTypesResponse {
  [entityType: string]: number;
}

export interface CreateRevisionRequest {
  entityType: string;
  entityId: string;
  currentValue: string;
  author: string;
}


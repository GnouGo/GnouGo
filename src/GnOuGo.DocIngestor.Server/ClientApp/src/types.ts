// ── API DTOs ────────────────────────────────────────────────────────

export interface IngestOverrides {
  collection?: string;
  chunkingMode?: string;
  minTokens?: number;
  targetTokens?: number;
  maxTokens?: number;
  overlapTokens?: number;
  embeddingModel?: string;
  embeddingEnabled?: boolean;
  storeEnabled?: boolean;
  imagesEnabled?: boolean;
}

export interface IngestResult {
  fileName: string;
  documentId: string;
  collection: string;
  chunksCount: number;
  imagesCount: number;
  embeddedCount: number;
  metadata: Record<string, string>;
}

export interface SearchHit {
  score: number;
  chunkId: string;
  documentId: string;
  sectionId: string;
  index: number;
  text: string;
  metadata: Record<string, string>;
  embeddingModel: string;
  dimensions: number;
}

export interface DefaultSettings {
  chunking: {
    mode: string;
    minTokens: number;
    targetTokens: number;
    maxTokens: number;
    overlapTokens: number;
  };
  embedding: { enabled: boolean; defaultModel: string };
  store: { enabled: boolean; defaultStore: string; defaultCollection: string };
  images: { enabled: boolean; loadBytes: boolean; ocr: { enabled: boolean; language: string; dpi: number } };
  search: {
    defaultTopK: number;
    reranker: {
      enabled: boolean;
      defaultType: string;
      vectorWeight: number;
      rerankWeight: number;
      availableTypes: string[];
    };
  };
}


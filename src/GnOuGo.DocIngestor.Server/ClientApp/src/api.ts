﻿import type { DefaultSettings, IngestOverrides, IngestResult, SearchHit } from './types';

const BASE = '';

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.text();
    throw new Error(`HTTP ${res.status}: ${body}`);
  }
  return res.json() as Promise<T>;
}

export async function fetchSettings(): Promise<DefaultSettings> {
  return json(await fetch(`${BASE}/api/settings`));
}

export async function ingestFile(file: File, overrides: IngestOverrides): Promise<IngestResult> {
  const fd = new FormData();
  fd.append('file', file);
  fd.append('options', JSON.stringify(overrides));
  return json(await fetch(`${BASE}/api/ingest`, { method: 'POST', body: fd }));
}

export interface SearchParams {
  collection: string;
  query: string;
  topK: number;
  rerankEnabled?: boolean;
  reranker?: string;
  vectorWeight?: number;
  rerankWeight?: number;
}

export async function search(params: SearchParams): Promise<SearchHit[]> {
  const qs = new URLSearchParams({
    collection: params.collection,
    query: params.query,
    topK: String(params.topK),
  });
  if (params.rerankEnabled !== undefined) qs.set('rerankEnabled', String(params.rerankEnabled));
  if (params.reranker) qs.set('reranker', params.reranker);
  if (params.vectorWeight !== undefined) qs.set('vectorWeight', String(params.vectorWeight));
  if (params.rerankWeight !== undefined) qs.set('rerankWeight', String(params.rerankWeight));
  return json(await fetch(`${BASE}/api/search?${qs}`));
}

export interface TsneHit extends SearchHit {
  x: number;
  y: number;
}

export interface TsneResult {
  queryPoint: { x: number; y: number };
  hits: TsneHit[];
}

export async function searchTsne(params: SearchParams): Promise<TsneResult> {
  const qs = new URLSearchParams({
    collection: params.collection,
    query: params.query,
    topK: String(params.topK),
  });
  if (params.rerankEnabled !== undefined) qs.set('rerankEnabled', String(params.rerankEnabled));
  if (params.reranker) qs.set('reranker', params.reranker);
  if (params.vectorWeight !== undefined) qs.set('vectorWeight', String(params.vectorWeight));
  if (params.rerankWeight !== undefined) qs.set('rerankWeight', String(params.rerankWeight));
  return json(await fetch(`${BASE}/api/search/tsne?${qs}`));
}

export async function fetchCollections(): Promise<string[]> {
  return json(await fetch(`${BASE}/api/collections`));
}

export async function fetchDocuments(collection: string): Promise<string[]> {
  return json(await fetch(`${BASE}/api/documents?collection=${encodeURIComponent(collection)}`));
}

export async function deleteDocument(collection: string, documentId: string): Promise<{ deleted: number }> {
  return json(await fetch(`${BASE}/api/documents/${encodeURIComponent(collection)}/${encodeURIComponent(documentId)}`, { method: 'DELETE' }));
}


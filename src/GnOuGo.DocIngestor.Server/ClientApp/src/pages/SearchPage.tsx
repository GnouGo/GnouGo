﻿import { useState, useEffect, useCallback, useMemo } from 'react';
import { fetchCollections, fetchSettings, search, searchTsne, deleteDocument, fetchDocuments } from '../api';
import type { SearchParams, TsneHit } from '../api';
import { ProximityChart } from '../components/ProximityChart';
import { TsneChart } from '../components/TsneChart';
import { MetadataFilters, applyFilters } from '../components/MetadataFilters';
import type { MetadataFilterEntry } from '../components/MetadataFilters';
import type { SearchHit, DefaultSettings } from '../types';

export function SearchPage() {
  const [settings, setSettings] = useState<DefaultSettings | null>(null);
  const [collections, setCollections] = useState<string[]>([]);
  const [collection, setCollection] = useState('');
  const [query, setQuery] = useState('');
  const [topK, setTopK] = useState(10);
  const [status, setStatus] = useState('');
  const [hits, setHits] = useState<SearchHit[]>([]);
  const [lastQuery, setLastQuery] = useState('');
  const [documents, setDocuments] = useState<string[]>([]);
  const [filters, setFilters] = useState<MetadataFilterEntry[]>([]);

  // Reranker state
  const [rerankEnabled, setRerankEnabled] = useState(false);
  const [rerankerType, setRerankerType] = useState('bm25');
  const [vectorWeight, setVectorWeight] = useState(0.5);
  const [rerankWeight, setRerankWeight] = useState(0.5);
  const [availableRerankers, setAvailableRerankers] = useState<string[]>([]);

  // Chart mode state
  const [chartMode, setChartMode] = useState<'proximity' | 'tsne'>('proximity');
  const [tsneHits, setTsneHits] = useState<TsneHit[]>([]);
  const [tsneQueryPoint, setTsneQueryPoint] = useState<{ x: number; y: number }>({ x: 0, y: 0 });
  const [tsneLoading, setTsneLoading] = useState(false);

  // Filtered hits (apply metadata filters client-side)
  const filteredHits = useMemo(() => applyFilters(hits, filters), [hits, filters]);

  // Collect all known metadata keys from current results for autocomplete
  const knownKeys = useMemo(() => {
    const keys = new Set<string>();
    for (const h of hits) {
      for (const k of Object.keys(h.metadata)) keys.add(k);
    }
    return Array.from(keys).sort();
  }, [hits]);

  // Load settings & collections
  useEffect(() => {
    Promise.all([fetchSettings(), fetchCollections()]).then(([s, cols]) => {
      setSettings(s);
      setTopK(s.search.defaultTopK);
      setRerankEnabled(s.search.reranker.enabled);
      setRerankerType(s.search.reranker.defaultType);
      setVectorWeight(s.search.reranker.vectorWeight);
      setRerankWeight(s.search.reranker.rerankWeight);
      setAvailableRerankers(s.search.reranker.availableTypes);
      const available = cols.length > 0 ? cols : [s.store.defaultCollection];
      setCollections(available);
      setCollection(cols.includes(s.store.defaultCollection) ? s.store.defaultCollection : available[0]);
    });
  }, []);

  // Load documents when collection changes
  const loadDocuments = useCallback(async (col: string) => {
    if (!col) return;
    try {
      const docs = await fetchDocuments(col);
      setDocuments(docs);
    } catch {
      setDocuments([]);
    }
  }, []);

  useEffect(() => {
    if (collection) loadDocuments(collection);
  }, [collection, loadDocuments]);

  // Delete document
  const handleDelete = async (docId: string) => {
    if (!confirm(`Delete all vectors for "${docId}" in "${collection}"?`)) return;
    try {
      const res = await deleteDocument(collection, docId);
      setStatus(`🗑 Deleted ${res.deleted} vectors`);
      await loadDocuments(collection);
    } catch (err: unknown) {
      setStatus(`❌ ${err instanceof Error ? err.message : String(err)}`);
    }
  };

  // Search
  const doSearch = async () => {
    const q = query.trim();
    if (!q) { setStatus('Enter a query.'); return; }
    setStatus('Searching…');
    setHits([]);
    setTsneHits([]);
    try {
      const params: SearchParams = {
        collection,
        query: q,
        topK,
        rerankEnabled,
        reranker: rerankEnabled ? rerankerType : undefined,
        vectorWeight: rerankEnabled ? vectorWeight : undefined,
        rerankWeight: rerankEnabled ? rerankWeight : undefined,
      };
      const results = await search(params);
      setStatus(`Found ${results.length} result(s)${rerankEnabled ? ` (reranked with ${rerankerType})` : ''}`);
      setHits(results);
      setLastQuery(q);

      // Fetch t-SNE data in background (non-blocking)
      if (results.length > 1) {
        setTsneLoading(true);
        searchTsne(params).then(tsne => {
          setTsneHits(tsne.hits);
          setTsneQueryPoint(tsne.queryPoint);
          setTsneLoading(false);
        }).catch(() => setTsneLoading(false));
      }
    } catch (err: unknown) {
      setStatus(`❌ ${err instanceof Error ? err.message : String(err)}`);
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') doSearch();
  };

  if (!settings) return <section className="search"><p>Loading…</p></section>;

  return (
    <section className="search">
      <h1 className="search__title">Vector Search</h1>

      {/* Search form */}
      <div className="search-form">
        <label className="search-form__label">Collection
          <select
            className="search-form__input"
            value={collection}
            onChange={e => setCollection(e.target.value)}
          >
            {collections.map(c => (
              <option key={c} value={c}>{c}</option>
            ))}
          </select>
        </label>
        <label className="search-form__label">Query
          <input
            className="search-form__input"
            value={query}
            onChange={e => setQuery(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Enter search text…"
          />
        </label>
        <label className="search-form__label">Top K
          <input
            className="search-form__input search-form__input--small"
            type="number"
            value={topK}
            min={1}
            max={100}
            onChange={e => setTopK(Number(e.target.value) || 10)}
          />
        </label>
        <button className="search-form__btn" onClick={doSearch}>Search</button>
      </div>

      {/* Metadata filters */}
      <MetadataFilters filters={filters} onChange={setFilters} knownKeys={knownKeys} />

      {/* Reranker options */}
      <details className="reranker-panel">
        <summary className="reranker-panel__summary">
          <span>Reranker</span>
          <span className={`reranker-panel__badge ${rerankEnabled ? 'reranker-panel__badge--on' : ''}`}>
            {rerankEnabled ? 'ON' : 'OFF'}
          </span>
        </summary>
        <div className="reranker-panel__body">
          <label className="reranker-panel__label reranker-panel__label--checkbox">
            <input type="checkbox" checked={rerankEnabled} onChange={e => setRerankEnabled(e.target.checked)} />
            Enable reranking
          </label>
          {rerankEnabled && (
            <>
              <label className="reranker-panel__label">Type
                <select className="reranker-panel__input" value={rerankerType} onChange={e => setRerankerType(e.target.value)}>
                  {availableRerankers.map(r => <option key={r} value={r}>{r}</option>)}
                </select>
              </label>
              <label className="reranker-panel__label">Vector weight
                <input className="reranker-panel__input" type="number" min={0} max={1} step={0.05}
                  value={vectorWeight} onChange={e => setVectorWeight(Number(e.target.value) || 0)} />
              </label>
              <label className="reranker-panel__label">Rerank weight
                <input className="reranker-panel__input" type="number" min={0} max={1} step={0.05}
                  value={rerankWeight} onChange={e => setRerankWeight(Number(e.target.value) || 0)} />
              </label>
            </>
          )}
        </div>
      </details>

      {/* Documents in collection */}
      {documents.length > 0 && (
        <div className="search__actions">
          <h3>Documents in collection</h3>
          <ul className="search__doc-list">
            {documents.map(d => (
              <li key={d} className="search__doc-item">
                <span className="search__doc-name">{d}</span>
                <button className="search__doc-delete" onClick={() => handleDelete(d)}>🗑</button>
              </li>
            ))}
          </ul>
        </div>
      )}

      {status && (
        <div className="search__status">
          {status}
          {hits.length > 0 && filteredHits.length < hits.length && (
            <span className="search__filter-count"> — showing {filteredHits.length} of {hits.length} after filters</span>
          )}
        </div>
      )}

      {/* Chart mode selector + visualization */}
      {filteredHits.length > 0 && (
        <div className="chart-switcher">
          <div className="chart-switcher__tabs">
            <button
              className={`chart-switcher__tab ${chartMode === 'proximity' ? 'chart-switcher__tab--active' : ''}`}
              onClick={() => setChartMode('proximity')}
            >
              Proximity Map
            </button>
            <button
              className={`chart-switcher__tab ${chartMode === 'tsne' ? 'chart-switcher__tab--active' : ''}`}
              onClick={() => setChartMode('tsne')}
            >
              t-SNE Map
              {tsneLoading && <span className="chart-switcher__spinner" />}
            </button>
          </div>

          {chartMode === 'proximity' && <ProximityChart hits={filteredHits} query={lastQuery} />}
          {chartMode === 'tsne' && tsneHits.length > 0 && (
            <TsneChart hits={tsneHits} queryPoint={tsneQueryPoint} query={lastQuery} />
          )}
          {chartMode === 'tsne' && tsneLoading && (
            <div className="tsne-chart tsne-chart--loading">
              <p>Computing t-SNE projection…</p>
            </div>
          )}
          {chartMode === 'tsne' && !tsneLoading && tsneHits.length === 0 && filteredHits.length > 0 && (
            <div className="tsne-chart tsne-chart--empty">
              <p>Not enough results for t-SNE projection (need at least 2 results).</p>
            </div>
          )}
        </div>
      )}

      {/* Result cards */}
      <div className="results">
        {filteredHits.map((h, i) => (
          <HitCard key={h.chunkId} hit={h} rank={i + 1} />
        ))}
      </div>
    </section>
  );
}

function HitCard({ hit: h, rank }: { hit: SearchHit; rank: number }) {
  const metaEntries = Object.entries(h.metadata);
  const scorePercent = (h.score * 100).toFixed(2);

  return (
    <div className="result-card">
      <div className="result-card__header">
        <span className="result-card__rank">#{rank}</span>
        <span className="result-card__score" title="Cosine similarity">{scorePercent}%</span>
        <span className="result-card__doc">{h.documentId}</span>
      </div>
      <p className="result-card__text">{h.text}</p>
      <details className="result-card__meta">
        <summary>Metadata &amp; Embedding ({h.embeddingModel}, {h.dimensions}d)</summary>
        <table className="result-card__table">
          <tbody>
            <tr><td className="result-card__key">chunkId</td><td>{h.chunkId}</td></tr>
            <tr><td className="result-card__key">sectionId</td><td>{h.sectionId}</td></tr>
            <tr><td className="result-card__key">index</td><td>{h.index}</td></tr>
            {metaEntries.map(([k, v]) => (
              <tr key={k}><td className="result-card__key">{k}</td><td>{v}</td></tr>
            ))}
          </tbody>
        </table>
      </details>
    </div>
  );
}


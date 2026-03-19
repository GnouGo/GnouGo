import { useCallback } from 'react';

export type FilterOperator = 'equals' | 'contains';

export interface MetadataFilterEntry {
  id: number;
  key: string;
  operator: FilterOperator;
  value: string;
}

interface Props {
  filters: MetadataFilterEntry[];
  onChange: (filters: MetadataFilterEntry[]) => void;
  /** Known metadata keys from search results for autocomplete */
  knownKeys: string[];
}

let nextId = 1;
export function createEmptyFilter(): MetadataFilterEntry {
  return { id: nextId++, key: '', operator: 'contains', value: '' };
}

/**
 * Apply metadata filters to search hits.
 * Returns only hits where ALL active filters match (AND logic).
 */
export function applyFilters<T extends { metadata: Record<string, string> }>(
  items: T[],
  filters: MetadataFilterEntry[],
): T[] {
  const active = filters.filter(f => f.key.trim() && f.value.trim());
  if (active.length === 0) return items;

  return items.filter(item => {
    return active.every(f => {
      const metaValue = item.metadata[f.key] ?? '';
      if (f.operator === 'equals') {
        return metaValue === f.value;
      }
      // contains (case-insensitive)
      return metaValue.toLowerCase().includes(f.value.toLowerCase());
    });
  });
}

export function MetadataFilters({ filters, onChange, knownKeys }: Props) {
  const updateFilter = useCallback((id: number, patch: Partial<MetadataFilterEntry>) => {
    onChange(filters.map(f => f.id === id ? { ...f, ...patch } : f));
  }, [filters, onChange]);

  const removeFilter = useCallback((id: number) => {
    onChange(filters.filter(f => f.id !== id));
  }, [filters, onChange]);

  const addFilter = useCallback(() => {
    onChange([...filters, createEmptyFilter()]);
  }, [filters, onChange]);

  return (
    <div className="meta-filters">
      <div className="meta-filters__header">
        <span className="meta-filters__title">Metadata Filters</span>
        <button className="meta-filters__add" onClick={addFilter} title="Add filter">+ Add filter</button>
      </div>

      {filters.length === 0 && (
        <p className="meta-filters__empty">No filters — all results shown.</p>
      )}

      {filters.map(f => (
        <div key={f.id} className="meta-filters__row">
          {/* Key input with datalist for suggestions */}
          <input
            className="meta-filters__input meta-filters__input--key"
            placeholder="Key (e.g. source)"
            value={f.key}
            onChange={e => updateFilter(f.id, { key: e.target.value })}
            list={`meta-keys-${f.id}`}
          />
          <datalist id={`meta-keys-${f.id}`}>
            {knownKeys.map(k => <option key={k} value={k} />)}
          </datalist>

          {/* Operator select */}
          <select
            className="meta-filters__input meta-filters__input--op"
            value={f.operator}
            onChange={e => updateFilter(f.id, { operator: e.target.value as FilterOperator })}
          >
            <option value="equals">equals</option>
            <option value="contains">contains</option>
          </select>

          {/* Value input */}
          <input
            className="meta-filters__input meta-filters__input--value"
            placeholder="Value"
            value={f.value}
            onChange={e => updateFilter(f.id, { value: e.target.value })}
          />

          {/* Remove button */}
          <button
            className="meta-filters__remove"
            onClick={() => removeFilter(f.id)}
            title="Remove filter"
          >✕</button>
        </div>
      ))}
    </div>
  );
}


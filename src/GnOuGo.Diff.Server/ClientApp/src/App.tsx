import { useState, useEffect } from 'react'
import ReactDiffViewer from 'react-diff-viewer-continued'
import type { 
  RevisionDto, 
  ComparisonResult, 
  EntityTypesResponse 
} from './types'

function App() {
  const [entityTypes, setEntityTypes] = useState<[string, number][]>([])
  const [selectedType, setSelectedType] = useState<string>('')
  const [entities, setEntities] = useState<RevisionDto[]>([])
  const [selectedEntityId, setSelectedEntityId] = useState<string>('')
  const [revisions, setRevisions] = useState<RevisionDto[]>([])
  const [fromRevision, setFromRevision] = useState<RevisionDto | null>(null)
  const [toRevision, setToRevision] = useState<RevisionDto | null>(null)
  const [comparison, setComparison] = useState<ComparisonResult | null>(null)
  const [loading, setLoading] = useState<boolean>(false)
  const [error, setError] = useState<string | null>(null)

  // Charger les types d'entités au démarrage
  useEffect(() => {
    loadEntityTypes()
  }, [])

  // Charger les entités quand un type est sélectionné
  useEffect(() => {
    if (selectedType) {
      loadEntities(selectedType)
    }
  }, [selectedType])

  // Charger les révisions quand une entité est sélectionnée
  useEffect(() => {
    if (selectedType && selectedEntityId) {
      loadRevisions(selectedType, selectedEntityId)
    }
  }, [selectedType, selectedEntityId])

  // Comparer les révisions quand les deux sont sélectionnées
  useEffect(() => {
    if (fromRevision && toRevision) {
      compareRevisions(fromRevision.id, toRevision.id)
    }
  }, [fromRevision, toRevision])

  const loadEntityTypes = async (): Promise<void> => {
    try {
      const response = await fetch('/api/entity-types')
      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`)
      }
      const data: EntityTypesResponse = await response.json()
      setEntityTypes(Object.entries(data))
    } catch (err) {
      setError('Erreur lors du chargement des types d\'entités')
      console.error('Error loading entity types:', err)
    }
  }

  const loadEntities = async (type: string): Promise<void> => {
    try {
      setLoading(true)
      const response = await fetch(`/api/entities/${encodeURIComponent(type)}`)
      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`)
      }
      const data: RevisionDto[] = await response.json()
      setEntities(data)
      setSelectedEntityId('')
      setRevisions([])
      setFromRevision(null)
      setToRevision(null)
      setComparison(null)
    } catch (err) {
      setError('Erreur lors du chargement des entités')
      console.error('Error loading entities:', err)
    } finally {
      setLoading(false)
    }
  }

  const loadRevisions = async (type: string, entityId: string): Promise<void> => {
    try {
      setLoading(true)
      const response = await fetch(`/api/revisions/${encodeURIComponent(type)}/${encodeURIComponent(entityId)}`)
      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`)
      }
      const data: RevisionDto[] = await response.json()
      setRevisions(data)
      setFromRevision(null)
      setToRevision(null)
      setComparison(null)
    } catch (err) {
      setError('Erreur lors du chargement des révisions')
      console.error('Error loading revisions:', err)
    } finally {
      setLoading(false)
    }
  }

  const compareRevisions = async (fromId: number, toId: number): Promise<void> => {
    try {
      setLoading(true)
      const response = await fetch(`/api/revisions/compare/${fromId}/${toId}`)
      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`)
      }
      const data: ComparisonResult = await response.json()
      setComparison(data)
      setError(null)
    } catch (err) {
      setError('Erreur lors de la comparaison des révisions')
      setComparison(null)
      console.error('Error comparing revisions:', err)
    } finally {
      setLoading(false)
    }
  }

  const formatDate = (dateString: string): string => {
    return new Date(dateString).toLocaleString(navigator.language, {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit'
    })
  }

  const formatJSON = (jsonString: string): string => {
    try {
      return JSON.stringify(JSON.parse(jsonString), null, 2)
    } catch {
      return jsonString
    }
  }

  return (
    <div className="app">
      <div className="header">
        <h1 className="header__title">🔍 GnOuGo.Diff - Audit & Diff Viewer</h1>
        <p className="header__description">Visualisez les modifications entre deux versions d'une entité</p>
      </div>

      {error && <div className="error">{error}</div>}

      <div className="controls">
        <div className="control-group">
          <label className="control-group__label">Type d'entité</label>
          <select 
            className="control-group__select" 
            value={selectedType} 
            onChange={(e) => setSelectedType(e.target.value)}
          >
            <option value="">-- Sélectionner un type --</option>
            {entityTypes.map(([type, count]) => (
              <option key={type} value={type}>
                {type} ({count} révision{count > 1 ? 's' : ''})
              </option>
            ))}
          </select>
        </div>

        {selectedType && entities.length > 0 && (
          <div className="control-group">
            <label className="control-group__label">Entité</label>
            <select 
              className="control-group__select" 
              value={selectedEntityId} 
              onChange={(e) => setSelectedEntityId(e.target.value)}
            >
              <option value="">-- Sélectionner une entité --</option>
              {entities.map((entity) => (
                <option key={entity.entityId} value={entity.entityId}>
                  {entity.entityId} (dernière révision : {formatDate(entity.timestamp)})
                </option>
              ))}
            </select>
          </div>
        )}
      </div>

      {revisions.length > 0 && (
        <div className="revisions-grid">
          <div className="revision-selector">
            <h3 className="revision-selector__title">📅 Révision FROM (ancienne)</h3>
            <div className="revision-selector__list">
              {revisions.map((rev) => (
                <div
                  key={rev.id}
                  className={`revision-item ${fromRevision?.id === rev.id ? 'revision-item--selected' : ''}`}
                  onClick={() => setFromRevision(rev)}
                >
                  <div className="revision-item__timestamp">{formatDate(rev.timestamp)}</div>
                  <div className="revision-item__author">👤 {rev.author}</div>
                  <div className="revision-item__id">ID: {rev.id}</div>
                </div>
              ))}
            </div>
          </div>

          <div className="revision-selector">
            <h3 className="revision-selector__title">📅 Révision TO (nouvelle)</h3>
            <div className="revision-selector__list">
              {revisions.map((rev) => (
                <div
                  key={rev.id}
                  className={`revision-item ${toRevision?.id === rev.id ? 'revision-item--selected' : ''}`}
                  onClick={() => setToRevision(rev)}
                >
                  <div className="revision-item__timestamp">{formatDate(rev.timestamp)}</div>
                  <div className="revision-item__author">👤 {rev.author}</div>
                  <div className="revision-item__id">ID: {rev.id}</div>
                </div>
              ))}
            </div>
          </div>
        </div>
      )}

      {loading && <div className="loading">⏳ Chargement...</div>}

      {comparison && (
        <div className="diff-viewer">
          <h2 className="diff-viewer__title">📊 Comparaison des révisions</h2>
          
          <div className="diff-stats">
            <div className="stat stat--added">
              <span className="stat__label">➕ Ajoutées:</span>
              <span className="stat__value">{comparison.stats.linesAdded}</span>
            </div>
            <div className="stat stat--deleted">
              <span className="stat__label">➖ Supprimées:</span>
              <span className="stat__value">{comparison.stats.linesDeleted}</span>
            </div>
            <div className="stat stat--modified">
              <span className="stat__label">✏️ Modifiées:</span>
              <span className="stat__value">{comparison.stats.linesModified}</span>
            </div>
            <div className="stat">
              <span className="stat__label">Inchangées:</span>
              <span className="stat__value">{comparison.stats.linesUnchanged}</span>
            </div>
          </div>

          <ReactDiffViewer
            oldValue={formatJSON(comparison.fromRevision.currentValue)}
            newValue={formatJSON(comparison.toRevision.currentValue)}
            splitView={true}
            leftTitle={`FROM - ${formatDate(comparison.fromRevision.timestamp)} par ${comparison.fromRevision.author}`}
            rightTitle={`TO - ${formatDate(comparison.toRevision.timestamp)} par ${comparison.toRevision.author}`}
            showDiffOnly={false}
          />
        </div>
      )}

      {!loading && revisions.length === 0 && selectedEntityId && (
        <div className="empty-state">
          <div className="empty-state__icon">📭</div>
          <div className="empty-state__message">Aucune révision trouvée pour cette entité</div>
        </div>
      )}

      {!loading && !selectedType && entityTypes.length === 0 && (
        <div className="empty-state">
          <div className="empty-state__icon">🚀</div>
          <div className="empty-state__message">
            Aucune donnée disponible. Utilisez le CLI pour insérer des données de test.
          </div>
        </div>
      )}
    </div>
  )
}

export default App


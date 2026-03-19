

interface TraceFiltersProps {
  serviceName: string;
  startUtc: string;
  endUtc: string;
  traceIdFilter: string;
  attributeContains: string;
  onServiceNameChange: (value: string) => void;
  onStartUtcChange: (value: string) => void;
  onEndUtcChange: (value: string) => void;
  onTraceIdFilterChange: (value: string) => void;
  onAttributeContainsChange: (value: string) => void;
  onClearFilters: () => void;
}

function TraceFilters({ 
  serviceName, 
  startUtc, 
  endUtc, 
  traceIdFilter,
  attributeContains,
  onServiceNameChange, 
  onStartUtcChange, 
  onEndUtcChange,
  onTraceIdFilterChange,
  onAttributeContainsChange,
  onClearFilters 
}: TraceFiltersProps) {
  // Convertir DateTimeOffset UTC vers format datetime-local pour l'input
  const formatForInput = (isoString: string): string => {
    if (!isoString) return '';
    // Format attendu: YYYY-MM-DDTHH:mm
    const date = new Date(isoString);
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    const hours = String(date.getHours()).padStart(2, '0');
    const minutes = String(date.getMinutes()).padStart(2, '0');
    return `${year}-${month}-${day}T${hours}:${minutes}`;
  };

  // Convertir datetime-local vers ISO UTC
  const handleDateChange = (value: string, setter: (value: string) => void): void => {
    if (!value) {
      setter('');
      return;
    }
    // Ajouter les secondes et convertir en UTC
    const date = new Date(value + ':00Z'); // Interpréter comme UTC
    setter(date.toISOString());
  };

  return (
    <div className="log-filters">
      <h3 className="log-filters__title">Filtres</h3>
      
      <div className="log-filters__grid">
        <div className="log-filters__field">
          <label htmlFor="serviceName" className="log-filters__label">
            Service Name
          </label>
          <input
            id="serviceName"
            type="text"
            className="log-filters__input"
            placeholder="ex: rag-test-service"
            value={serviceName}
            onChange={(e) => onServiceNameChange(e.target.value)}
          />
        </div>

        <div className="log-filters__field">
          <label htmlFor="traceIdFilter" className="log-filters__label">
            Trace ID
          </label>
          <input
            id="traceIdFilter"
            type="text"
            className="log-filters__input log-filters__input--mono"
            placeholder="ex: 4bf92f3577b34da6"
            value={traceIdFilter}
            onChange={(e) => onTraceIdFilterChange(e.target.value)}
          />
        </div>

        <div className="log-filters__field">
          <label htmlFor="attributeContains" className="log-filters__label">
            Correlation / Request / Session ID
          </label>
          <input
            id="attributeContains"
            type="text"
            className="log-filters__input"
            placeholder="Recherche dans les attributs…"
            value={attributeContains}
            onChange={(e) => onAttributeContainsChange(e.target.value)}
          />
        </div>

        <div className="log-filters__field">
          <label htmlFor="startUtc" className="log-filters__label">
            Date de début (UTC)
          </label>
          <input
            id="startUtc"
            type="datetime-local"
            className="log-filters__input"
            value={formatForInput(startUtc)}
            onChange={(e) => handleDateChange(e.target.value, onStartUtcChange)}
          />
        </div>

        <div className="log-filters__field">
          <label htmlFor="endUtc" className="log-filters__label">
            Date de fin (UTC)
          </label>
          <input
            id="endUtc"
            type="datetime-local"
            className="log-filters__input"
            value={formatForInput(endUtc)}
            onChange={(e) => handleDateChange(e.target.value, onEndUtcChange)}
          />
        </div>

        <div className="log-filters__field log-filters__field--actions">
          <button 
            className="log-filters__clear-btn"
            onClick={onClearFilters}
            type="button"
          >
            Effacer les filtres
          </button>
        </div>
      </div>

      {(serviceName || startUtc || endUtc || traceIdFilter || attributeContains) && (
        <div className="log-filters__active">
          <span className="log-filters__active-label">Filtres actifs :</span>
          {serviceName && (
            <span className="log-filters__active-item">
              Service: {serviceName}
            </span>
          )}
          {traceIdFilter && (
            <span className="log-filters__active-item">
              Trace ID: {traceIdFilter}
            </span>
          )}
          {attributeContains && (
            <span className="log-filters__active-item">
              Attribut: {attributeContains}
            </span>
          )}
          {startUtc && (
            <span className="log-filters__active-item">
              Depuis: {new Date(startUtc).toLocaleString()}
            </span>
          )}
          {endUtc && (
            <span className="log-filters__active-item">
              Jusqu'à: {new Date(endUtc).toLocaleString()}
            </span>
          )}
        </div>
      )}
    </div>
  );
}

export default TraceFilters;


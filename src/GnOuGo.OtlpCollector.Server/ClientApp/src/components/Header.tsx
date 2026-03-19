interface HeaderProps {
  connected: boolean;
  onToggleConnection: () => void;
  loading: boolean;
}

function Header({ connected, onToggleConnection, loading }: HeaderProps) {
  return (
    <header className="header">
      <div className="header__content">
        <h1 className="header__title">OTLP Tenant Collector — Trace Explorer</h1>
        <div className="header__actions">
          {connected && (
            <span className="header__status header__status--live">
              <span className="header__status-dot" />
              Live
            </span>
          )}
          <button 
            className={`button ${connected ? 'button--connected' : 'button--connect'} ${loading ? 'button--loading' : ''}`}
            onClick={onToggleConnection}
            disabled={loading}
          >
            {loading ? 'Connecting...' : connected ? '⏹ Disconnect' : '⚡ Connect'}
          </button>
        </div>
      </div>
    </header>
  );
}

export default Header;


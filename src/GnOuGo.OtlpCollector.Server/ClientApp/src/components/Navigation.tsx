import { Link, useLocation } from 'react-router-dom';

function Navigation() {
  const location = useLocation();

  return (
    <nav className="navigation">
      <div className="navigation__content">
        <div className="navigation__brand">
          <h1 className="navigation__title">OTLP Tenant Collector</h1>
        </div>
        <div className="navigation__links">
          <Link 
            to="/" 
            className={`navigation__link ${location.pathname === '/' ? 'navigation__link--active' : ''}`}
          >
            📊 Traces
          </Link>
          <Link 
            to="/logs" 
            className={`navigation__link ${location.pathname === '/logs' ? 'navigation__link--active' : ''}`}
          >
            📋 Logs
          </Link>
          <Link 
            to="/tenants" 
            className={`navigation__link ${location.pathname === '/tenants' ? 'navigation__link--active' : ''}`}
          >
            👥 Tenants
          </Link>
        </div>
      </div>
    </nav>
  );
}

export default Navigation;


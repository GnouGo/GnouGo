import { useState } from 'react';
import { TenantsPage } from './pages/TenantsPage';
import { SecretsPage } from './pages/SecretsPage';
import { AuditPage } from './pages/AuditPage';

type Tab = 'tenants' | 'secrets' | 'audit';

export function App() {
  const [tab, setTab] = useState<Tab>('secrets');

  return (
    <div className="app">
      <header className="app__header">
        <h1 className="app__title">🔐 GnOuGo.KeyVault</h1>
        <nav className="nav">
          {(['tenants', 'secrets', 'audit'] as Tab[]).map(t => (
            <button
              key={t}
              className={`nav__item ${tab === t ? 'nav__item--active' : ''}`}
              onClick={() => setTab(t)}
            >
              {t.charAt(0).toUpperCase() + t.slice(1)}
            </button>
          ))}
        </nav>
      </header>
      <main className="app__content">
        {tab === 'tenants' && <TenantsPage />}
        {tab === 'secrets' && <SecretsPage />}
        {tab === 'audit' && <AuditPage />}
      </main>
    </div>
  );
}


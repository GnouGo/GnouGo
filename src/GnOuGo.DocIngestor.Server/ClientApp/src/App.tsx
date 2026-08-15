import { lazy, Suspense, useState, useEffect } from 'react';
import { IngestPage } from './pages/IngestPage';

const SearchPage = lazy(async () => {
  const module = await import('./pages/SearchPage');
  return { default: module.SearchPage };
});

type Page = 'ingest' | 'search';

function getPage(): Page {
  const hash = location.hash.replace('#', '');
  return hash === 'search' ? 'search' : 'ingest';
}

export function App() {
  const [page, setPage] = useState<Page>(getPage);

  useEffect(() => {
    const handler = () => setPage(getPage());
    window.addEventListener('hashchange', handler);
    return () => window.removeEventListener('hashchange', handler);
  }, []);

  return (
    <>
      <nav className="nav">
        <span className="nav__brand">GnOuGoDoc Ingestor</span>
        <a className={`nav__link ${page === 'ingest' ? 'nav__link--active' : ''}`} href="#ingest">Ingest</a>
        <a className={`nav__link ${page === 'search' ? 'nav__link--active' : ''}`} href="#search">Search</a>
      </nav>
      <main className="main">
        {page === 'search' ? (
          <Suspense fallback={<section className="search"><p>Loading search…</p></section>}>
            <SearchPage />
          </Suspense>
        ) : <IngestPage />}
      </main>
    </>
  );
}


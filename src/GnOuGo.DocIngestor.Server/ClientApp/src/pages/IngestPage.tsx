import { useState, useEffect, useRef, useCallback } from 'react';
import { fetchSettings, ingestFile } from '../api';
import type { IngestOverrides, IngestResult, DefaultSettings } from '../types';

// ── Accepted file extensions ────────────────────────────────────────
// Binary formats (dedicated extractors)
// + all extensions from PlainTextExtractor.ExtensionMap
const ACCEPTED_EXTENSIONS = [
  // Binary document formats
  '.pdf', '.docx', '.pptx', '.xlsx',
  // Markdown
  '.md', '.markdown', '.mdx',
  // Plain text
  '.txt', '.text', '.log', '.readme', '.license', '.changelog',
  // Data / config
  '.json', '.jsonl', '.ndjson', '.json5',
  '.yaml', '.yml', '.toml', '.xml', '.xsd', '.xslt',
  '.csv', '.tsv', '.ini', '.cfg', '.conf', '.env',
  '.properties', '.editorconfig',
  // .NET / C#
  '.cs', '.csx', '.csproj', '.sln', '.props', '.targets', '.nuspec',
  '.razor', '.cshtml', '.xaml', '.fsproj', '.fs', '.fsx', '.vb',
  // JavaScript / TypeScript / Web
  '.js', '.mjs', '.cjs', '.jsx', '.ts', '.tsx',
  '.vue', '.svelte', '.html', '.htm',
  '.css', '.scss', '.sass', '.less', '.graphql', '.gql',
  // Python
  '.py', '.pyi', '.pyw', '.ipynb',
  // Java / JVM
  '.java', '.kt', '.kts', '.scala', '.groovy', '.gradle', '.pom',
  // Go
  '.go', '.mod', '.sum',
  // Rust
  '.rs',
  // C / C++
  '.c', '.h', '.cpp', '.cxx', '.cc', '.hpp', '.hxx',
  // Ruby
  '.rb', '.erb', '.rake', '.gemspec',
  // PHP
  '.php',
  // Swift / Objective-C
  '.swift', '.m',
  // Shell / scripting
  '.sh', '.bash', '.zsh', '.fish',
  '.ps1', '.psm1', '.psd1', '.bat', '.cmd',
  // SQL
  '.sql',
  // Lua
  '.lua',
  // R
  '.r', '.rmd',
  // Perl
  '.pl', '.pm',
  // Haskell / Elixir / Erlang
  '.hs', '.ex', '.exs', '.erl',
  // Dart
  '.dart',
  // Zig / Nim / V
  '.zig', '.nim', '.v',
  // Infrastructure / DevOps
  '.tf', '.hcl', '.dockerfile', '.dockerignore',
  '.gitignore', '.gitattributes',
  '.npmrc', '.nvmrc', '.eslintrc', '.prettierrc', '.babelrc',
  // Protobuf / Thrift / IDL
  '.proto', '.thrift', '.avsc',
  // Makefile-like
  '.mk', '.cmake',
].join(',');

interface FileStatus {
  file: File;
  state: 'pending' | 'ingesting' | 'done' | 'error';
  result?: IngestResult;
  error?: string;
}

export function IngestPage() {
  const [settings, setSettings] = useState<DefaultSettings | null>(null);
  const [files, setFiles] = useState<FileStatus[]>([]);
  const [ingesting, setIngesting] = useState(false);
  const [dragActive, setDragActive] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);

  // Form state
  const [collection, setCollection] = useState('');
  const [chunkMode, setChunkMode] = useState('auto');
  const [minTokens, setMinTokens] = useState('');
  const [targetTokens, setTargetTokens] = useState('');
  const [maxTokens, setMaxTokens] = useState('');
  const [overlapTokens, setOverlapTokens] = useState('');
  const [embedModel, setEmbedModel] = useState('');
  const [embedEnabled, setEmbedEnabled] = useState(false);
  const [storeEnabled, setStoreEnabled] = useState(false);
  const [imagesEnabled, setImagesEnabled] = useState(false);

  useEffect(() => {
    fetchSettings().then(s => {
      setSettings(s);
      setCollection(s.store.defaultCollection);
      setChunkMode(s.chunking.mode);
      setMinTokens(String(s.chunking.minTokens));
      setTargetTokens(String(s.chunking.targetTokens));
      setMaxTokens(String(s.chunking.maxTokens));
      setOverlapTokens(String(s.chunking.overlapTokens));
      setEmbedModel(s.embedding.defaultModel);
      setEmbedEnabled(s.embedding.enabled);
      setStoreEnabled(s.store.enabled);
      setImagesEnabled(s.images.enabled);
    });
  }, []);

  const addFiles = useCallback((newFiles: FileList | File[]) => {
    const arr = Array.from(newFiles);
    if (arr.length === 0) return;
    setFiles(prev => {
      // Avoid duplicates by name+size+lastModified
      const existing = new Set(prev.map(f => `${f.file.name}|${f.file.size}|${f.file.lastModified}`));
      const toAdd = arr
        .filter(f => !existing.has(`${f.name}|${f.size}|${f.lastModified}`))
        .map(file => ({ file, state: 'pending' as const }));
      return [...prev, ...toAdd];
    });
  }, []);

  const removeFile = useCallback((index: number) => {
    setFiles(prev => prev.filter((_, i) => i !== index));
  }, []);

  const clearFiles = useCallback(() => {
    setFiles([]);
  }, []);

  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    setDragActive(false);
    if (e.dataTransfer?.files.length) addFiles(e.dataTransfer.files);
  }, [addFiles]);

  const handleIngest = async () => {
    const pending = files.filter(f => f.state === 'pending' || f.state === 'error');
    if (pending.length === 0) return;

    setIngesting(true);

    const overrides: IngestOverrides = {
      collection: collection || undefined,
      chunkingMode: chunkMode || undefined,
      minTokens: minTokens ? Number(minTokens) : undefined,
      targetTokens: targetTokens ? Number(targetTokens) : undefined,
      maxTokens: maxTokens ? Number(maxTokens) : undefined,
      overlapTokens: overlapTokens ? Number(overlapTokens) : undefined,
      embeddingModel: embedModel || undefined,
      embeddingEnabled: embedEnabled,
      storeEnabled: storeEnabled,
      imagesEnabled: imagesEnabled,
    };

    for (let i = 0; i < files.length; i++) {
      const fs = files[i];
      if (fs.state !== 'pending' && fs.state !== 'error') continue;

      // Mark as ingesting
      setFiles(prev => prev.map((f, idx) => idx === i ? { ...f, state: 'ingesting' as const, error: undefined } : f));

      try {
        const res = await ingestFile(fs.file, overrides);
        setFiles(prev => prev.map((f, idx) => idx === i ? { ...f, state: 'done' as const, result: res } : f));
      } catch (err: unknown) {
        const msg = err instanceof Error ? err.message : String(err);
        setFiles(prev => prev.map((f, idx) => idx === i ? { ...f, state: 'error' as const, error: msg } : f));
      }
    }

    setIngesting(false);
  };

  const doneCount = files.filter(f => f.state === 'done').length;
  const errorCount = files.filter(f => f.state === 'error').length;
  const pendingCount = files.filter(f => f.state === 'pending').length;
  const hasPendingOrError = pendingCount > 0 || errorCount > 0;

  if (!settings) return <section className="ingest"><p>Loading settings…</p></section>;

  return (
    <section className="ingest">
      <h1 className="ingest__title">Document Ingestion</h1>

      {/* Dropzone */}
      <div
        className={`dropzone ${dragActive ? 'dropzone--active' : ''}`}
        onDragOver={e => { e.preventDefault(); setDragActive(true); }}
        onDragLeave={() => setDragActive(false)}
        onDrop={handleDrop}
        onClick={() => fileInputRef.current?.click()}
      >
        <p className="dropzone__label">Drag &amp; drop files here, or click to select</p>
        <input
          ref={fileInputRef}
          className="dropzone__input"
          type="file"
          multiple
          accept={ACCEPTED_EXTENSIONS}
          onChange={e => { if (e.target.files?.length) { addFiles(e.target.files); e.target.value = ''; } }}
        />
      </div>

      {/* File list */}
      {files.length > 0 && (
        <div className="ingest__file-list">
          <div className="ingest__file-list-header">
            <span className="ingest__file-count">
              {files.length} file{files.length > 1 ? 's' : ''} selected
              {doneCount > 0 && <> — <strong>{doneCount}</strong> done</>}
              {errorCount > 0 && <> — <strong className="ingest__error-text">{errorCount}</strong> failed</>}
            </span>
            <button className="ingest__btn-clear" onClick={clearFiles} disabled={ingesting} title="Clear all files">✕ Clear</button>
          </div>
          <ul className="ingest__files">
            {files.map((fs, i) => (
              <li key={`${fs.file.name}-${fs.file.size}-${i}`} className={`ingest__file-item ingest__file-item--${fs.state}`}>
                <span className="ingest__file-icon">
                  {fs.state === 'pending' && '○'}
                  {fs.state === 'ingesting' && '⏳'}
                  {fs.state === 'done' && '✅'}
                  {fs.state === 'error' && '❌'}
                </span>
                <span className="ingest__file-name">{fs.file.name}</span>
                <span className="ingest__file-size">({(fs.file.size / 1024).toFixed(1)} KB)</span>
                {fs.state === 'pending' && !ingesting && (
                  <button className="ingest__btn-remove" onClick={() => removeFile(i)} title="Remove file">✕</button>
                )}
                {fs.error && <span className="ingest__file-error">{fs.error}</span>}
              </li>
            ))}
          </ul>
        </div>
      )}

      {/* Config form */}
      <details className="config-form" open>
        <summary className="config-form__summary">Extraction Settings</summary>
        <div className="config-form__grid">
          <label className="config-form__label">Collection
            <input className="config-form__input" value={collection} onChange={e => setCollection(e.target.value)} />
          </label>
          <label className="config-form__label">Chunking mode
            <select className="config-form__input" value={chunkMode} onChange={e => setChunkMode(e.target.value)}>
              <option value="auto">Auto (recommended)</option>
              <option value="recursive">Recursive</option>
              <option value="semantic">Semantic</option>
            </select>
          </label>
          <label className="config-form__label">Min tokens
            <input className="config-form__input" type="number" value={minTokens} onChange={e => setMinTokens(e.target.value)} />
          </label>
          <label className="config-form__label">Target tokens
            <input className="config-form__input" type="number" value={targetTokens} onChange={e => setTargetTokens(e.target.value)} />
          </label>
          <label className="config-form__label">Max tokens
            <input className="config-form__input" type="number" value={maxTokens} onChange={e => setMaxTokens(e.target.value)} />
          </label>
          <label className="config-form__label">Overlap tokens
            <input className="config-form__input" type="number" value={overlapTokens} onChange={e => setOverlapTokens(e.target.value)} />
          </label>
          <label className="config-form__label">Embedding model
            <input className="config-form__input" value={embedModel} onChange={e => setEmbedModel(e.target.value)} />
          </label>
          <label className="config-form__label config-form__label--checkbox">
            <input type="checkbox" checked={embedEnabled} onChange={e => setEmbedEnabled(e.target.checked)} /> Enable embedding
          </label>
          <label className="config-form__label config-form__label--checkbox">
            <input type="checkbox" checked={storeEnabled} onChange={e => setStoreEnabled(e.target.checked)} /> Store in vector DB
          </label>
          <label className="config-form__label config-form__label--checkbox">
            <input type="checkbox" checked={imagesEnabled} onChange={e => setImagesEnabled(e.target.checked)} /> Extract images
          </label>
        </div>
      </details>

      <button className="ingest__btn" disabled={files.length === 0 || !hasPendingOrError || ingesting} onClick={handleIngest}>
        {ingesting
          ? `Ingesting… (${doneCount}/${files.length})`
          : `Ingest ${pendingCount + errorCount} file${(pendingCount + errorCount) > 1 ? 's' : ''}`}
      </button>

      {/* Results */}
      {files.some(f => f.result) && (
        <div className="ingest__results">
          {files.filter(f => f.result).map((fs, i) => (
            <div key={i} className="ingest__result">
              <ResultCard result={fs.result!} />
            </div>
          ))}
        </div>
      )}
    </section>
  );
}

function ResultCard({ result: r }: { result: IngestResult }) {
  const metaEntries = Object.entries(r.metadata);
  return (
    <div className="result-card">
      <h3 className="result-card__title">{r.fileName}</h3>
      <dl className="result-card__stats">
        <dt>Document ID</dt><dd>{r.documentId}</dd>
        <dt>Collection</dt><dd>{r.collection}</dd>
        <dt>Chunks</dt><dd>{r.chunksCount}</dd>
        <dt>Images</dt><dd>{r.imagesCount}</dd>
        <dt>Embedded</dt><dd>{r.embeddedCount}</dd>
      </dl>
      {metaEntries.length > 0 && (
        <details className="result-card__meta">
          <summary>Metadata ({metaEntries.length})</summary>
          <table className="result-card__table">
            <tbody>
              {metaEntries.map(([k, v]) => (
                <tr key={k}>
                  <td className="result-card__key">{k}</td>
                  <td>{v}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </details>
      )}
    </div>
  );
}


import React, { useCallback, useEffect, useMemo, useState } from 'react'
import { createRoot } from 'react-dom/client'
import './styles.scss'

type FileUploadResponse = {
  id: string
  tenantId: string
  fileName: string
  contentType: string
  sizeBytes: number
  createdUtc: string
  expiresUtc: string
  ttlSeconds: number
  downloadUrl: string
}

type FileListItemResponse = {
  id: string
  tenantId: string
  fileName: string
  contentType: string
  sizeBytes: number
  createdUtc: string
  expiresUtc: string
  ttlSecondsRemaining: number
  downloadUrl: string
}

type FileListResponse = {
  files: FileListItemResponse[]
}

type UploadStatus = 'idle' | 'uploading' | 'success' | 'error'

function formatBytes(value: number): string {
  if (value === 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB']
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1)
  return `${(value / 1024 ** index).toFixed(index === 0 ? 0 : 2)} ${units[index]}`
}

function formatUtc(value: string): string {
  return new Date(value).toLocaleString(undefined, { timeZoneName: 'short' })
}

function formatTtl(seconds: number): string {
  const safe = Math.max(0, Math.floor(seconds))
  const hours = Math.floor(safe / 3600)
  const minutes = Math.floor((safe % 3600) / 60)
  const rest = safe % 60
  return `${hours}h ${minutes}m ${rest}s`
}

function App(): React.ReactElement {
  const [selectedFile, setSelectedFile] = useState<File | null>(null)
  const [ttl, setTtl] = useState('12:00:00')
  const [tenantId, setTenantId] = useState('default')
  const [status, setStatus] = useState<UploadStatus>('idle')
  const [message, setMessage] = useState('')
  const [uploaded, setUploaded] = useState<FileUploadResponse | null>(null)
  const [files, setFiles] = useState<FileListItemResponse[]>([])
  const [isListing, setIsListing] = useState(false)

  const canUpload = useMemo(() => selectedFile !== null && status !== 'uploading', [selectedFile, status])

  const refreshFiles = useCallback(async () => {
    setIsListing(true)
    try {
      const response = await fetch('/api/files')
      if (!response.ok) throw new Error(`List failed with HTTP ${response.status}`)
      const body = (await response.json()) as FileListResponse
      setFiles(body.files)
    } catch (error) {
      setMessage(error instanceof Error ? error.message : String(error))
    } finally {
      setIsListing(false)
    }
  }, [])

  useEffect(() => {
    void refreshFiles()
  }, [refreshFiles])

  const upload = async (): Promise<void> => {
    if (!selectedFile) return

    setStatus('uploading')
    setMessage('Uploading with streaming request body...')
    setUploaded(null)

    const query = new URLSearchParams({ fileName: selectedFile.name })
    if (ttl.trim()) query.set('ttl', ttl.trim())
    if (tenantId.trim()) query.set('tenantId', tenantId.trim())

    try {
      const response = await fetch(`/api/files?${query.toString()}`, {
        method: 'POST',
        headers: {
          'Content-Type': selectedFile.type || 'application/octet-stream',
          'X-Tenant-Id': tenantId.trim() || 'default'
        },
        body: selectedFile
      })

      if (!response.ok) {
        const body = await response.text()
        throw new Error(body || `Upload failed with HTTP ${response.status}`)
      }

      const body = (await response.json()) as FileUploadResponse
      setUploaded(body)
      setStatus('success')
      setMessage(`Uploaded ${body.fileName} (${formatBytes(body.sizeBytes)}).`)
      await refreshFiles()
    } catch (error) {
      setStatus('error')
      setMessage(error instanceof Error ? error.message : String(error))
    }
  }

  return (
    <main className="files-app">
      <section className="files-app__hero">
        <div>
          <p className="files-app__eyebrow">GnOuGo.Files.Server</p>
          <h1 className="files-app__title">Temporary streamed file exchange</h1>
          <p className="files-app__subtitle">
            Upload large files with a TTL, then download them by id while metadata is persisted in SQLite.
          </p>
        </div>
        <button className="files-app__button files-app__button--secondary" type="button" onClick={() => void refreshFiles()} disabled={isListing}>
          {isListing ? 'Refreshing...' : 'Refresh list'}
        </button>
      </section>

      <section className="files-panel files-panel--upload">
        <h2 className="files-panel__title">Upload</h2>
        <div className="files-form">
          <label className="files-form__field">
            <span className="files-form__label">File</span>
            <input className="files-form__input" type="file" onChange={(event) => setSelectedFile(event.target.files?.[0] ?? null)} />
          </label>
          <label className="files-form__field">
            <span className="files-form__label">TTL query value</span>
            <input className="files-form__input" value={ttl} onChange={(event) => setTtl(event.target.value)} placeholder="12:00:00 or 1.5" />
          </label>
          <label className="files-form__field">
            <span className="files-form__label">Tenant id</span>
            <input className="files-form__input" value={tenantId} onChange={(event) => setTenantId(event.target.value)} placeholder="default" />
          </label>
          <button className="files-app__button" type="button" onClick={() => void upload()} disabled={!canUpload}>
            {status === 'uploading' ? 'Uploading...' : 'Upload file'}
          </button>
        </div>
        {message && <p className={`files-panel__message files-panel__message--${status}`}>{message}</p>}
        {uploaded && (
          <div className="files-result">
            <span className="files-result__label">Last uploaded id</span>
            <code className="files-result__id">{uploaded.id}</code>
            <a className="files-result__link" href={uploaded.downloadUrl}>Download</a>
          </div>
        )}
      </section>

      <section className="files-panel files-panel--list">
        <h2 className="files-panel__title">Available files</h2>
        <div className="files-table">
          <div className="files-table__row files-table__row--head">
            <span>Name</span>
            <span>Size</span>
            <span>Expires UTC</span>
            <span>TTL remaining</span>
            <span>Action</span>
          </div>
          {files.map((file) => (
            <div className="files-table__row" key={file.id}>
              <span className="files-table__name" title={file.id}>{file.fileName}</span>
              <span>{formatBytes(file.sizeBytes)}</span>
              <span>{formatUtc(file.expiresUtc)}</span>
              <span>{formatTtl(file.ttlSecondsRemaining)}</span>
              <a className="files-table__download" href={file.downloadUrl}>Download</a>
            </div>
          ))}
          {files.length === 0 && <p className="files-table__empty">No available files.</p>}
        </div>
      </section>
    </main>
  )
}

createRoot(document.getElementById('root')!).render(<App />)


import React, { useCallback, useEffect, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { duration, object, output, pretty } from './traffic'
import type { Detail, Summary } from './traffic'
import './styles.scss'

async function get<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, { signal, cache: 'no-store' })
  if (!response.ok) throw new Error(`Request failed (${response.status})`)
  return response.json() as Promise<T>
}

function App() {
  const [calls, setCalls] = useState<Summary[]>([])
  const [selected, setSelected] = useState<string | null>(null)
  const selectedRef = useRef(selected)
  const [detail, setDetail] = useState<Detail | null>(null)
  const [live, setLive] = useState(false)
  const [paused, setPaused] = useState(false)
  const [error, setError] = useState('')
  const [tab, setTab] = useState<'conversation' | 'raw' | 'setup'>('conversation')
  const [query, setQuery] = useState('')
  const [provider, setProvider] = useState('')
  const [status, setStatus] = useState('')
  const [setup, setSetup] = useState('')
  const [copied, setCopied] = useState(false)
  const refreshing = useRef(false)
  const dirty = useRef(false)
  const select = useCallback((id: string | null) => { selectedRef.current = id; setSelected(id) }, [])

  const refresh = useCallback(async () => {
    if (refreshing.current) { dirty.current = true; return }
    refreshing.current = true
    try {
      do {
        dirty.current = false
        const snapshot = await get<{ calls: Summary[] }>('/api/traffic')
        setCalls(snapshot.calls)
        if (!snapshot.calls.some(call => call.id === selectedRef.current)) select(snapshot.calls[0]?.id ?? null)
        const id = selectedRef.current
        if (id) {
          try {
            const result = await get<Detail>(`/api/traffic/${id}`)
            if (selectedRef.current === id) setDetail(result)
          } catch { if (selectedRef.current === id) setDetail(null) }
        } else setDetail(null)
        setError('')
      } while (dirty.current)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to connect') }
    finally { refreshing.current = false }
  }, [select])

  useEffect(() => {
    get<{ configuration: unknown }>('/api/setup').then(value => setSetup(JSON.stringify(value.configuration, null, 2))).catch(() => setError('Unable to load model setup'))
  }, [])
  useEffect(() => {
    if (paused) { setLive(false); return }
    void refresh()
    const events = new EventSource('/api/traffic/events')
    events.onopen = () => { setLive(true); void refresh() }
    events.addEventListener('changed', () => void refresh())
    events.onerror = () => setLive(false)
    return () => events.close()
  }, [paused, refresh])
  useEffect(() => {
    setDetail(null)
    if (!selected) return
    const controller = new AbortController()
    get<Detail>(`/api/traffic/${selected}`, controller.signal).then(setDetail).catch(() => {})
    return () => controller.abort()
  }, [selected])

  const visible = calls.filter(call => (!provider || call.provider === provider) && (!status || call.status === status)
    && `${call.model} ${call.id}`.toLowerCase().includes(query.toLowerCase()))
  const running = calls.filter(call => call.status === 'running').length
  const failed = calls.filter(call => call.status === 'failed').length
  const tokens = calls.reduce((sum, call) => sum + (call.usage?.total_tokens ?? 0), 0)
  const clear = async () => {
    try { const response = await fetch('/api/traffic', { method: 'DELETE' }); if (!response.ok) throw new Error('Unable to clear history'); await refresh() }
    catch (reason) { setError(String(reason)) }
  }

  return <div className="app">
    <aside className="rail"><a href="/ui/" className="brand" aria-label="GnOuGo home">G<span>↗</span></a><div className="rail__label">GnOuGo</div><span className="rail__bottom">LOCAL</span></aside>
    <main>
      <header className="header"><div><div className="eyebrow">GNOU GO / DEVELOPER TOOLS</div><h1>Copilot Proxy<span className="version">01</span></h1></div>
        <span className={`connection ${live ? 'connection--live' : ''}`}><i />{paused ? 'View paused' : live ? 'Live connection' : 'Reconnecting'}</span>
      </header>
      <section className="intro"><div><h2>Your models. Every exchange.</h2><p>Follow the conversation between Copilot and your LLM providers.</p></div><button className="button button--dark" onClick={() => setTab('setup')}>Connect VS Code <span>↗</span></button></section>
      <section className="metrics" aria-label="Traffic statistics">{[['Captured calls', calls.length], ['In progress', running], ['Failed', failed], ['Total tokens', tokens.toLocaleString()]].map(([label, value], index) => <div className="metric" key={label}><span>{label}</span><strong className={index === 1 && running > 0 ? 'active' : ''}>{value}</strong><small>{['Current session', 'Streaming now', 'Upstream or request errors', 'Reported by providers'][index]}</small></div>)}</section>
      {error && <div className="error" role="alert">{error}</div>}
      <section className="workspace">
        <aside className="requests"><div className="panel-heading"><h3>Traffic <span>{visible.length}</span></h3><button className="icon-button" onClick={() => setPaused(!paused)} aria-label={paused ? 'Resume live view' : 'Pause live view'}>{paused ? '▶' : 'Ⅱ'}</button><button className="text-button" onClick={() => void clear()}>Clear</button></div>
          <div className="filters"><input aria-label="Search requests" placeholder="Search model or request ID…" value={query} onChange={event => setQuery(event.target.value)} /><div><select aria-label="Filter provider" value={provider} onChange={event => setProvider(event.target.value)}><option value="">All providers</option>{[...new Set(calls.map(call => call.provider))].map(name => <option key={name}>{name}</option>)}</select><select aria-label="Filter status" value={status} onChange={event => setStatus(event.target.value)}><option value="">All statuses</option>{['running', 'completed', 'failed', 'cancelled'].map(value => <option key={value}>{value}</option>)}</select></div></div>
          <div className="request-list">{visible.map(call => <button className={`call ${selected === call.id ? 'call--selected' : ''}`} key={call.id} onClick={() => { select(call.id); if (tab === 'setup') setTab('conversation') }} aria-pressed={selected === call.id}>
            <div className="call__top"><span className={`status-dot status-dot--${call.status}`} /><strong>{call.model}</strong><span>↗</span></div><div className="call__meta"><span>{new Date(call.startedAt).toLocaleTimeString()}</span><span>{duration(call.durationMs)}</span></div><div className="call__bottom"><span>{call.protocol}</span><span>{call.status}</span>{call.truncated && <span>truncated</span>}</div>
          </button>)}{visible.length === 0 && <div className="empty-list">{calls.length ? 'No matching requests.' : 'Your next LLM call will appear here.'}</div>}</div>
          <div className="requests__footer"><span className="status-dot" /> History is kept in memory</div>
        </aside>
        <div className="inspector"><nav className="tabs" aria-label="Inspector view">{(['conversation', 'raw', 'setup'] as const).map(value => <button key={value} className={tab === value ? 'selected' : ''} onClick={() => setTab(value)}>{value === 'raw' ? 'Raw payloads' : value === 'setup' ? 'VS Code setup' : 'Conversation'}</button>)}</nav>
          {tab === 'setup' ? <div className="setup"><div className="eyebrow">GET CONNECTED</div><h2>Bring your models into Copilot.</h2><ol><li>For local development, configure providers and models in the ignored <code>appsettings.Development.json</code> and run the Development profile. Supply credentials through environment overrides, then restart. Keep <code>appsettings.json</code> free of local settings.</li><li>In VS Code, run <strong>Chat: Manage Language Models</strong>, choose <strong>Add Models → Custom Endpoint</strong>, and select <strong>Chat Completions</strong>.</li><li>Use the configuration below in <code>chatLanguageModels.json</code>, then select <strong>Agent</strong>, <strong>Local</strong>, and your model in Chat. This loopback proxy does not require a client API key; leave it empty, or use a placeholder if the editor requires one.</li></ol><div className="code-heading"><strong>chatLanguageModels.json</strong><button className="text-button" onClick={async () => { try { await navigator.clipboard.writeText(setup); setCopied(true); setTimeout(() => setCopied(false), 2000) } catch { setError('Copy failed. Select and copy the configuration below.') } }}>{copied ? 'Copied' : 'Copy configuration'}</button></div><pre>{setup || 'Loading configuration…'}</pre><p className="note">Agent mode requires tool calling and enabled built-in tools. VS Code executes file and terminal actions; approve its prompts in the editor. An empty models list means no providers have been configured yet.</p></div>
          : detail && detail.summary.id === selected ? <><div className="detail-heading"><div className="eyebrow">{detail.summary.provider} / {detail.summary.protocol}</div><h2>{detail.summary.model}</h2><div className="detail-meta"><span className={`badge badge--${detail.summary.status}`}>{detail.summary.status}</span><span>{duration(detail.summary.durationMs)}</span><span>First token {duration(detail.summary.firstTokenMs)}</span><span>{detail.summary.usage?.total_tokens ?? '—'} tokens</span></div><code className="request-id">{detail.summary.id}</code>{detail.summary.error && <div className="error">{detail.summary.error}</div>}{detail.summary.truncated && <p className="note">Capture limit reached. Displayed content is truncated; forwarding continues in full.</p>}</div>
            <div className="detail-content">{tab === 'raw' ? Object.entries(detail.bodies).map(([name, body]) => <section className="raw" key={name}><div className="code-heading"><strong>{{ clientRequest: 'Client → Proxy', upstreamRequest: 'Proxy → Provider', upstreamResponse: 'Provider → Proxy', clientResponse: 'Proxy → Client' }[name] ?? name}</strong>{body.truncated && <span>Truncated</span>}</div><pre>{pretty(body.text)}</pre></section>) : <Conversation detail={detail} />}</div></>
          : <div className="empty"><div className="empty__symbol">↔</div><div className="eyebrow">A CLEAR VIEW OF EVERY CALL</div><h2>Waiting for a conversation.</h2><p>Select a request to inspect its messages, tool calls, and live response.</p><button className="button" onClick={() => setTab('setup')}>Set up your connection ↗</button></div>}
        </div>
      </section><footer className="footer"><span>GnOuGo ProxyCopilot</span><span>Local workspace · Text + tools · Chat Completions</span></footer>
    </main>
  </div>
}

function Conversation({ detail }: { detail: Detail }) {
  const request = object(detail.bodies.clientRequest?.text ?? '')
  const messages = Array.isArray(request?.messages) ? request.messages : []
  const response = output(detail.bodies.clientResponse?.text ?? '')
  return <div className="conversation">{messages.map((message, index) => <article className="message" key={index}><div className="message__label">{message.role}{message.tool_call_id && <code>{message.tool_call_id}</code>}</div>{message.content && <pre>{typeof message.content === 'string' ? message.content : JSON.stringify(message.content, null, 2)}</pre>}{Array.isArray(message.tool_calls) && message.tool_calls.map((tool: { id: string; function: { name: string; arguments: string } }) => <div className="tool" key={tool.id}><strong>↳ {tool.function.name}</strong><code>{tool.id}</code><pre>{pretty(tool.function.arguments)}</pre></div>)}</article>)}
    {Array.isArray(request?.tools) && <details className="tools-list"><summary>Available tools ({request.tools.length})</summary><pre>{JSON.stringify(request.tools, null, 2)}</pre></details>}
    <article className="message message--response"><div className="message__label"><span className="status-dot" /> Assistant response {detail.summary.status === 'running' && <span className="streaming">streaming</span>}</div><pre>{response.content || (response.tools.length ? '' : detail.summary.status === 'running' ? 'Waiting for the first token…' : 'No text response captured.')}</pre>{response.tools.map((tool, index) => <div className="tool" key={tool.id || index}><strong>↳ {tool.name || 'Tool call'}</strong><code>{tool.id}</code><pre>{pretty(tool.arguments)}</pre></div>)}</article>
  </div>
}

createRoot(document.getElementById('root')!).render(<React.StrictMode><App /></React.StrictMode>)

import 'github-markdown-css/github-markdown.css';
import './styles/app.scss';
import mermaid from 'mermaid';

declare global {
  interface Window {
	GnOuGo?: {
	  Agent?: any;
	};
  }
}

const log = (...args: unknown[]) => console.info('[GnOuGo.Agent]', ...args);

const notifyDesktopClientReady = () => {
  try {
    const current = new URL(window.location.href);
    const token = current.searchParams.get('desktopToken');
    if (!token) return;

    fetch(`/desktop/client-ready/${encodeURIComponent(token)}`, {
      method: 'GET',
      cache: 'no-store',
      keepalive: true,
    }).catch((error) => {
      console.warn('[GnOuGo.Agent] client-ready ping failed', error);
    });
  } catch (error) {
    console.warn('[GnOuGo.Agent] unable to send client-ready ping', error);
  }
};

window.addEventListener('error', (event) => {
  console.error('[GnOuGo.Agent] window.error', event.error ?? event.message, event);
});

window.addEventListener('unhandledrejection', (event) => {
  console.error('[GnOuGo.Agent] window.unhandledrejection', event.reason);
});

mermaid.initialize({
  startOnLoad: false,
  securityLevel: 'strict',
  theme: 'base',
  themeVariables: {
	primaryColor: '#0000ff',
	primaryBorderColor: '#0000ff',
  },
});

const el = (id: string) => document.getElementById(id);

function renderMermaid(id: string) {
  const c = el(id);
  if (!c) {
	log('renderMermaid: container not found', id);
	return;
  }

  const codes = [...c.querySelectorAll('pre>code.language-mermaid,pre>code.language-c4')] as HTMLElement[];
  const nodes: HTMLElement[] = [];

  for (const code of codes) {
	const ds = (code as any).dataset;
	if (ds && ds.saProcessed) continue;
	if (ds) ds.saProcessed = '1';

	const pre = code.parentElement as HTMLElement | null;
	if (!pre) continue;

	const div = document.createElement('div');
	div.className = 'mermaid';
	div.textContent = code.textContent || '';
	pre.replaceWith(div);
	nodes.push(div);
  }

  if (nodes.length) {
	try {
	  mermaid.run({ nodes });
	  log('renderMermaid: rendered nodes', nodes.length);
	} catch (e) {
	  console.warn('[GnOuGo.Agent] mermaid render failed', e);
	}
  }
}

const storage = {
  load: (k: string) => localStorage.getItem(k),
  save: (k: string, v: string) => localStorage.setItem(k, v),
  clear: (k: string) => localStorage.removeItem(k),
};

const scrollToBottom = (id: string) => {
  const c = el(id);
  if (!c) {
	log('scrollToBottom: container not found', id);
	return;
  }

  c.scrollTop = c.scrollHeight;
};

window.GnOuGo ??= {};
window.GnOuGo.Agent = {
  storage,
  scrollToBottom,
  markdown: {
	enhance: renderMermaid,
  },
};

notifyDesktopClientReady();
log('client bootstrap complete');

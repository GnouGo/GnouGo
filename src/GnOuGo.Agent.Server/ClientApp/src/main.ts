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

const scrollToBottom = (id: string) => {
  const c = el(id);
  if (!c) {
	log('scrollToBottom: container not found', id);
	return;
  }

  c.scrollTop = c.scrollHeight;
};

type DotNetUploadReceiver = {
  invokeMethodAsync: (method: string, ...args: unknown[]) => Promise<unknown>;
};

type UploadRegistration = {
  dropZone: HTMLElement;
  input: HTMLInputElement;
  handlers: {
  dragEnter: (event: DragEvent) => void;
  dragOver: (event: DragEvent) => void;
  dragLeave: (event: DragEvent) => void;
  drop: (event: DragEvent) => void;
  change: () => void;
  };
};

const uploadRegistrations = new Map<string, UploadRegistration>();

const canContainFiles = (event: DragEvent) =>
  Array.from(event.dataTransfer?.types ?? []).some((type) => type === 'Files');

const uploadFile = (file: File, dotNet: DotNetUploadReceiver) => {
  const cryptoApi = window.crypto || (window as any).msCrypto;
  const random = cryptoApi?.getRandomValues
  ? Array.from(cryptoApi.getRandomValues(new Uint32Array(4))).map((part) => part.toString(16)).join('')
  : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const clientId = `${Date.now()}-${random}`;

  dotNet.invokeMethodAsync('NotifyFileUploadStarted', clientId, file.name, file.size).catch(console.warn);

  const xhr = new XMLHttpRequest();
  xhr.open('POST', `/api/files?fileName=${encodeURIComponent(file.name)}`, true);
  xhr.responseType = 'json';
  xhr.setRequestHeader('Content-Type', file.type || 'application/octet-stream');

  xhr.upload.onprogress = (event) => {
  if (!event.lengthComputable || event.total <= 0) return;
  const progress = Math.max(1, Math.min(99, Math.round((event.loaded / event.total) * 100)));
  dotNet.invokeMethodAsync('NotifyFileUploadProgress', clientId, progress).catch(console.warn);
  };

  xhr.onload = () => {
  if (xhr.status >= 200 && xhr.status < 300) {
    const payload = xhr.response;
    const fileId = payload?.id ?? payload?.Id;
    if (typeof fileId === 'string' && fileId.length > 0) {
    dotNet.invokeMethodAsync('NotifyFileUploadCompleted', clientId, fileId).catch(console.warn);
    return;
    }
  }

  const message = xhr.response?.message ?? xhr.response?.Message ?? xhr.statusText ?? 'Upload failed.';
  dotNet.invokeMethodAsync('NotifyFileUploadFailed', clientId, String(message)).catch(console.warn);
  };

  xhr.onerror = () => {
  dotNet.invokeMethodAsync('NotifyFileUploadFailed', clientId, 'Network error during upload.').catch(console.warn);
  };

  xhr.onabort = () => {
  dotNet.invokeMethodAsync('NotifyFileUploadFailed', clientId, 'Upload cancelled.').catch(console.warn);
  };

  xhr.send(file);
};

const uploadFiles = (files: FileList | File[] | null | undefined, dotNet: DotNetUploadReceiver) => {
  if (!files) return;
  Array.from(files).forEach((file) => uploadFile(file, dotNet));
};

const fileUploads = {
  register: (dropZoneId: string, inputId: string, dotNet: DotNetUploadReceiver) => {
  fileUploads.unregister(dropZoneId);

  const dropZone = el(dropZoneId);
  const input = el(inputId) as HTMLInputElement | null;
  if (!dropZone || !input) return;

  const dragEnter = (event: DragEvent) => {
    if (!canContainFiles(event)) return;
    event.preventDefault();
    dropZone.classList.add('gnougo-composer--drag-over');
  };

  const dragOver = (event: DragEvent) => {
    if (!canContainFiles(event)) return;
    event.preventDefault();
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy';
    dropZone.classList.add('gnougo-composer--drag-over');
  };

  const dragLeave = (event: DragEvent) => {
    if (event.relatedTarget && dropZone.contains(event.relatedTarget as Node)) return;
    dropZone.classList.remove('gnougo-composer--drag-over');
  };

  const drop = (event: DragEvent) => {
    if (!canContainFiles(event)) return;
    event.preventDefault();
    dropZone.classList.remove('gnougo-composer--drag-over');
    uploadFiles(event.dataTransfer?.files, dotNet);
  };

  const change = () => {
    uploadFiles(input.files, dotNet);
    input.value = '';
  };

  dropZone.addEventListener('dragenter', dragEnter);
  dropZone.addEventListener('dragover', dragOver);
  dropZone.addEventListener('dragleave', dragLeave);
  dropZone.addEventListener('drop', drop);
  input.addEventListener('change', change);

  uploadRegistrations.set(dropZoneId, {
    dropZone,
    input,
    handlers: { dragEnter, dragOver, dragLeave, drop, change },
  });
  },

  unregister: (dropZoneId: string) => {
  const registration = uploadRegistrations.get(dropZoneId);
  if (!registration) return;

  const { dropZone, input, handlers } = registration;
  dropZone.removeEventListener('dragenter', handlers.dragEnter);
  dropZone.removeEventListener('dragover', handlers.dragOver);
  dropZone.removeEventListener('dragleave', handlers.dragLeave);
  dropZone.removeEventListener('drop', handlers.drop);
  input.removeEventListener('change', handlers.change);
  dropZone.classList.remove('gnougo-composer--drag-over');
  uploadRegistrations.delete(dropZoneId);
  },

  open: (inputId: string) => {
  const input = el(inputId) as HTMLInputElement | null;
  input?.click();
  },
};

window.GnOuGo ??= {};
window.GnOuGo.Agent = {
  scrollToBottom,
  fileUploads,
  markdown: {
	enhance: renderMermaid,
  },
};

notifyDesktopClientReady();
log('client bootstrap complete');

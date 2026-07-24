import 'github-markdown-css/github-markdown.css';
import './styles/app.scss';
import mermaid from 'mermaid';
import {
  GnouGnouWorkflowAnimationController,
  type WorkflowAnimationPrepared,
  type WorkflowAnimationScenePatch,
  type WorkflowSimulationEvent,
} from '../../../GnOuGo.Assets.Animation/Runtime/gnougnou-workflow-animation-controller';
import {
  GnouGnouAnimationController,
} from '../../../GnOuGo.Assets.Bears/Runtime/gnougnou-animation-controller';

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
  flowchart: {
    htmlLabels: false,
  },
  themeVariables: {
    background: '#ffffff',
    mainBkg: '#f8fafc',
    nodeBkg: '#f8fafc',
    primaryColor: '#e0f2fe',
    primaryTextColor: '#0f172a',
    primaryBorderColor: '#0284c7',
    secondaryColor: '#ecfdf5',
    secondaryTextColor: '#064e3b',
    secondaryBorderColor: '#16a34a',
    tertiaryColor: '#fff7ed',
    tertiaryTextColor: '#7c2d12',
    tertiaryBorderColor: '#f97316',
    lineColor: '#334155',
    defaultLinkColor: '#334155',
    arrowheadColor: '#334155',
    textColor: '#0f172a',
    nodeTextColor: '#0f172a',
    edgeLabelBackground: '#ffffff',
    clusterBkg: '#f8fafc',
    clusterBorder: '#cbd5e1',
    fontFamily: 'Inter, Segoe UI, Arial, sans-serif',
  },
});

const el = (id: string) => document.getElementById(id);
let mermaidRenderIndex = 0;

function isMermaidCodeBlock(code: HTMLElement) {
  const pre = code.parentElement as HTMLElement | null;
  const hints = [
    code.className,
    pre?.className ?? '',
    code.getAttribute('data-lang') ?? '',
    code.getAttribute('data-language') ?? '',
    pre?.getAttribute('data-lang') ?? '',
    pre?.getAttribute('data-language') ?? '',
  ]
    .join(' ')
    .split(/\s+/)
    .map((hint) => hint.trim().toLowerCase())
    .filter(Boolean);

  return hints.some((hint) =>
    hint === 'mermaid' ||
    hint === 'c4' ||
    hint === 'language-mermaid' ||
    hint === 'language-c4' ||
    hint === 'lang-mermaid' ||
    hint === 'lang-c4');
}

async function renderMermaid(id: string) {
  const c = el(id);
  if (!c) {
	log('renderMermaid: container not found', id);
	return;
  }

  const codes = [...c.querySelectorAll('pre > code')] as HTMLElement[];
  const nodes: HTMLElement[] = [];

  for (const code of codes) {
    if (!isMermaidCodeBlock(code)) continue;

	const pre = code.parentElement as HTMLElement | null;
	if (!pre) continue;

    const source = code.textContent || '';
	const div = document.createElement('div');
	div.className = 'mermaid';
    div.dataset.mermaidSource = source;
	div.textContent = source;
	pre.replaceWith(div);
	nodes.push(div);
  }

  const existing = [...c.querySelectorAll('.mermaid')] as HTMLElement[];
  for (const node of existing) {
    if (node.querySelector('svg')) continue;
    if (!nodes.includes(node)) nodes.push(node);
  }

  if (nodes.length) {
    let rendered = 0;
    for (const node of nodes) {
      const source = (node.dataset.mermaidSource || node.textContent || '').trim();
      if (!source) continue;

      node.dataset.mermaidSource = source;
      node.removeAttribute('data-processed');

      try {
        const renderId = `gnougo-mermaid-${Date.now()}-${mermaidRenderIndex++}`;
        const result = await mermaid.render(renderId, source, node);
        node.innerHTML = result.svg;
        result.bindFunctions?.(node);
        node.classList.remove('mermaid--error');
        rendered++;
      } catch (e) {
        node.textContent = source;
        node.classList.add('mermaid--error');
        console.warn('[GnOuGo.Agent] mermaid render failed', e);
      }
    }

    if (rendered) log('renderMermaid: rendered nodes', rendered);
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

interface WorkflowAnimationHandle {
  controller: GnouGnouWorkflowAnimationController
  resizeObserver: ResizeObserver
  resize: () => void
  zoom: number
}

const workflowAnimationControllers = new Map<string, WorkflowAnimationHandle>();
const nextAnimationFrame = () => new Promise<void>(resolve => requestAnimationFrame(() => resolve()));

const workflowAnimation = {
  mount: async (hostId: string, prepared: WorkflowAnimationPrepared): Promise<boolean> => {
    workflowAnimation.dispose(hostId);
    let host = el(hostId);
    for (let attempt = 0; !host && attempt < 12; attempt += 1) {
      await nextAnimationFrame();
      host = el(hostId);
    }
    if (!host) return false;

    host.innerHTML = prepared.svg;
    host.dataset.status = 'Running';
    const svg = host.querySelector<SVGSVGElement>('svg');
    if (!svg) return false;

    svg.setAttribute('preserveAspectRatio', 'xMidYMin meet');
    const handle = {} as WorkflowAnimationHandle;
    const resize = () => {
      const logicalWidth = svg.viewBox.baseVal.width || Number(svg.getAttribute('width')) || prepared.width;
      const logicalHeight = svg.viewBox.baseVal.height || Number(svg.getAttribute('height')) || prepared.height;
      const availableWidth = Math.max(1, host!.clientWidth);
      const renderedWidth = availableWidth * handle.zoom;
      const renderedHeight = renderedWidth * logicalHeight / Math.max(1, logicalWidth);
      svg.style.width = `${renderedWidth}px`;
      svg.style.height = `${renderedHeight}px`;
      svg.style.maxWidth = 'none';
    };
    const characters = new GnouGnouAnimationController(() => host);
    const controller = new GnouGnouWorkflowAnimationController(
      () => host,
      characters,
      {
        onFocus: id => controller.focus(id),
        onStatus: (status, message) => {
          host.dataset.status = status;
          if (message) host.dataset.message = message;
        },
      },
    );
    const resizeObserver = new ResizeObserver(resize);
    Object.assign(handle, { controller, resizeObserver, resize, zoom: 1 });
    workflowAnimationControllers.set(hostId, handle);
    resizeObserver.observe(host);
    resize();
    controller.attach();
    return true;
  },

  applyPatch: (hostId: string, patch: WorkflowAnimationScenePatch): boolean => {
    const handle = workflowAnimationControllers.get(hostId);
    if (!handle) return false;
    handle.controller.applyScenePatch(patch);
    handle.resize();
    return true;
  },

  applyEvent: (hostId: string, event: WorkflowSimulationEvent): boolean => {
    const handle = workflowAnimationControllers.get(hostId);
    if (!handle) return false;
    handle.controller.enqueueEvent(event);
    return true;
  },

  focus: (hostId: string, elementId: string) => {
    workflowAnimationControllers.get(hostId)?.controller.focus(elementId);
  },

  setZoom: (hostId: string, zoom: number) => {
    const handle = workflowAnimationControllers.get(hostId);
    if (!handle) return;
    handle.zoom = Math.max(.25, Math.min(zoom, 4));
    handle.resize();
  },

  dispose: (hostId: string) => {
    const handle = workflowAnimationControllers.get(hostId);
    handle?.resizeObserver.disconnect();
    handle?.controller.dispose();
    workflowAnimationControllers.delete(hostId);
  },
};

window.GnOuGo ??= {};
window.GnOuGo.Agent = {
  scrollToBottom,
  fileUploads,
  workflowAnimation,
  markdown: {
	enhance: renderMermaid,
  },
};

notifyDesktopClientReady();
log('client bootstrap complete');

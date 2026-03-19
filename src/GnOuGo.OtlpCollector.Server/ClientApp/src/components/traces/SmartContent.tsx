import { useMemo, useState } from 'react';
import Prism from 'prismjs';
import 'prismjs/components/prism-json';
import 'prismjs/components/prism-yaml';
import 'prismjs/components/prism-markup';
import { marked } from 'marked';

type ContentType = 'json' | 'yaml' | 'xml' | 'markdown' | 'text';

/**
 * Prism.js uses regex-based tokenisation that can cause catastrophic
 * backtracking (page freeze) on large or adversarial inputs — especially
 * the markdown grammar (bold / italic patterns) and yaml (scalar / multiline).
 * Skip highlighting above this threshold.
 */
const MAX_PRISM_CHARS = 50_000;

/** marked.parse safety cap — extremely large strings can still be slow. */
const MAX_MARKED_CHARS = 200_000;

/**
 * isMarkdown / detectType only need the first few KB to find indicators.
 * Sampling avoids running 9+ regex patterns on megabyte-sized strings.
 */
const DETECT_SAMPLE_CHARS = 5_000;

interface SmartContentProps {
  value: string;
  /** Max collapsed height in px. 0 = no collapse */
  maxHeight?: number;
  /** Force rendering as a specific type */
  forceType?: ContentType;
  /** If true, render markdown as HTML by default (instead of source) */
  renderMarkdown?: boolean;
}

/* ---- detection ---- */

function detectType(raw: string): ContentType {
  const trimmed = raw.trim();
  // JSON detection: starts with { or [
  if (/^[\[{]/.test(trimmed)) {
    try { JSON.parse(trimmed); return 'json'; } catch { /* not valid json */ }
  }
  // XML/HTML detection
  if (/^<[a-zA-Z?!]/.test(trimmed)) return 'xml';
  // Markdown detection BEFORE yaml — LLM outputs are overwhelmingly markdown
  // and markdown lists (`- item`) / prose colons (`Note: …`) very often
  // trigger false-positive YAML detection.
  if (isMarkdown(trimmed)) return 'markdown';
  // YAML detection: require at least one real key:value pair (list items
  // alone are ambiguous with markdown/plain-text lists).
  const yamlSample = trimmed.length > DETECT_SAMPLE_CHARS ? trimmed.slice(0, DETECT_SAMPLE_CHARS) : trimmed;
  if (
    /^---/.test(yamlSample) ||
    /^[\w][^\n]*:\s/m.test(yamlSample) ||
    /^- [\w]/m.test(yamlSample)
  ) {
    const lines = yamlSample.split('\n');
    let kvCount = 0;
    let listCount = 0;
    for (const l of lines) {
      if (/^\s*[\w][\w.\-]*:\s/.test(l)) kvCount++;
      if (/^\s*- /.test(l)) listCount++;
    }
    if ((kvCount + listCount >= 2 && kvCount >= 1) || /^---/.test(yamlSample)) return 'yaml';
  }
  return 'text';
}

function isMarkdown(s: string): boolean {
  // Sample the beginning — markdown indicators (headings, bold, fences…)
  // appear early; avoids running 9 regex on megabyte strings.
  const sample = s.length > DETECT_SAMPLE_CHARS ? s.slice(0, DETECT_SAMPLE_CHARS) : s;
  let score = 0;
  if (/^#{1,6}\s/m.test(sample)) score += 2;         // headings
  if (/\*\*[^*]+\*\*/m.test(sample)) score++;          // bold
  if (/\*[^*]+\*/m.test(sample)) score++;              // italic
  if (/\[.+?\]\(.+?\)/m.test(sample)) score += 2;     // links
  if (/^```/m.test(sample)) score += 2;                // code fences
  if (/^[-*+] /m.test(sample)) score++;                // unordered list
  if (/^\d+\. /m.test(sample)) score++;                // ordered list
  if (/^>/m.test(sample)) score++;                     // blockquote
  if (/`[^`]+`/m.test(sample)) score++;                // inline code
  return score >= 2;
}

function formatContent(raw: string, type: ContentType): string {
  if (type === 'json') {
    try {
      return JSON.stringify(JSON.parse(raw.trim()), null, 2);
    } catch {
      return raw;
    }
  }
  return raw;
}

function renderMarkdownHtml(raw: string): string {
  if (raw.length > MAX_MARKED_CHARS) return '';
  try {
    return marked.parse(raw, { async: false, gfm: true, breaks: true }) as string;
  } catch {
    return raw;
  }
}

const TYPE_LABELS: Record<ContentType, { icon: string; label: string; color: string }> = {
  json:     { icon: '{ }', label: 'JSON',     color: '#f59e0b' },
  yaml:     { icon: '📄',  label: 'YAML',     color: '#8b5cf6' },
  xml:      { icon: '🏷️', label: 'XML',      color: '#3b82f6' },
  markdown: { icon: '📝',  label: 'Markdown', color: '#10b981' },
  text:     { icon: '📝',  label: 'Text',     color: '#6b7280' },
};

const PRISM_GRAMMAR: Record<ContentType, string> = {
  json: 'json',
  yaml: 'yaml',
  xml: 'markup',
  markdown: 'markdown',
  text: '',
};

/* ---- component ---- */

function SmartContent({ value, maxHeight = 300, forceType, renderMarkdown = true }: SmartContentProps) {
  const detected = useMemo(() => forceType ?? detectType(value), [value, forceType]);
  const isMd = detected === 'markdown';
  const [showSource, setShowSource] = useState(!renderMarkdown || !isMd);
  const [expanded, setExpanded] = useState(false);

  const formatted = useMemo(() => formatContent(value, detected), [value, detected]);
  const highlighted = useMemo(() => {
    // Skip Prism for markdown — its bold/italic regex patterns cause
    // catastrophic backtracking on typical LLM outputs (unmatched ** / nested *).
    // Markdown is best consumed via the rendered view; source is readable as-is.
    if (detected === 'markdown') return null;
    const grammar = PRISM_GRAMMAR[detected];
    if (!grammar || !Prism.languages[grammar]) return null;
    // Safety cap — even JSON/YAML/XML grammars can freeze on very large inputs.
    if (formatted.length > MAX_PRISM_CHARS) return null;
    try {
      return Prism.highlight(formatted, Prism.languages[grammar], grammar);
    } catch {
      return null;
    }
  }, [formatted, detected]);
  const mdHtml = useMemo(() => isMd ? renderMarkdownHtml(value) : '', [value, isMd]);

  const meta = TYPE_LABELS[detected];
  const isLong = formatted.length > 500 || formatted.split('\n').length > 15;
  const shouldCollapse = isLong && !expanded && maxHeight > 0 && showSource;

  // Fall back to source view when markdown rendering is unavailable (too large / error)
  const showRenderedMd = isMd && !showSource && !!mdHtml;

  return (
    <div className="smart-content">
      <div className="smart-content__badge" style={{ borderColor: meta.color }}>
        <span className="smart-content__badge-icon">{meta.icon}</span>
        <span className="smart-content__badge-label" style={{ color: meta.color }}>{meta.label}</span>
        {isMd && (
          <button
            className="smart-content__view-toggle"
            onClick={() => setShowSource(!showSource)}
          >
            {showSource ? '👁 Rendu' : '</> Source'}
          </button>
        )}
        <span className="smart-content__badge-size">{formatted.length.toLocaleString()} chars</span>
      </div>

      {/* Markdown rendered view */}
      {showRenderedMd ? (
        <div
          className="smart-content__markdown"
          dangerouslySetInnerHTML={{ __html: mdHtml }}
        />
      ) : (
        <>
          <div
            className={`smart-content__body ${shouldCollapse ? 'smart-content__body--collapsed' : ''}`}
            style={shouldCollapse ? { maxHeight: `${maxHeight}px` } : undefined}
          >
            {highlighted ? (
              <pre
                className={`smart-content__pre smart-content__pre--${detected}`}
                dangerouslySetInnerHTML={{ __html: highlighted }}
              />
            ) : (
              <pre className="smart-content__pre smart-content__pre--text">{formatted}</pre>
            )}
            {shouldCollapse && <div className="smart-content__fade" />}
          </div>
          {isLong && maxHeight > 0 && (
            <button className="smart-content__toggle" onClick={() => setExpanded(!expanded)}>
              {expanded ? '▲ Réduire' : `▼ Tout afficher (${formatted.split('\n').length} lignes)`}
            </button>
          )}
        </>
      )}
    </div>
  );
}

export default SmartContent;
export { detectType, isMarkdown, formatContent, type ContentType };

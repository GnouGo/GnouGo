// ── Utility functions for GnOuGo.Flow Client ──

import yaml from 'js-yaml'

/** Parse a YAML string of inputs into a JSON string for the API. */
export function parseInputsYamlToJsonString(inputText: string): string | undefined {
  const trimmed = inputText.trim()
  if (!trimmed) return undefined
  const parsed = yaml.load(trimmed)
  return JSON.stringify(parsed ?? null)
}

/** Format any value as a pretty YAML string. */
export function formatValueAsYaml(value: unknown): string {
  if (typeof value === 'string') return value
  return yaml.dump(value, {
    noRefs: true,
    lineWidth: -1,
    sortKeys: false,
  })
}

/** Format a USD amount with appropriate precision. */
export function formatCurrencyUsd(value?: number | null): string {
  if (value === null || value === undefined) return '—'
  if (value === 0) return '$0.000000'
  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: value < 0.01 ? 6 : 2,
    maximumFractionDigits: value < 0.01 ? 6 : 4,
  }).format(value)
}

/** Build a unique key for a step from its id and call depth. */
export function makeStepKey(stepId: string, callDepth: number): string {
  return `${callDepth}:${stepId}`
}

/** Split a raw chunk into individual NDJSON lines. */
export function readNdjsonLines(chunk: string): string[] {
  return chunk.split(/\r?\n/).filter(line => line.trim().length > 0)
}


/* ─── YAML ↔ Flow Graph conversion ───────────────────────────
   Parses GnOuGo.Flow YAML into React Flow nodes/edges and back.
   ────────────────────────────────────────────────────────────── */
import yaml from 'js-yaml'
import type { Node, Edge } from '@xyflow/react'
import { STEP_TYPE_MAP } from './stepTypes'

// ── Types for parsed YAML ──
export interface ParsedStep {
  id: string
  type: string
  if?: string
  input?: Record<string, unknown>
  output?: string
  retry?: { max?: number; backoff_ms?: number; backoff_mult?: number; jitter_ms?: number }
  on_error?: unknown
  steps?: ParsedStep[]
  branches?: { steps: ParsedStep[] }[]
  cases?: { when?: string; value?: string; steps: ParsedStep[] }[]
  default?: ParsedStep[]
  [key: string]: unknown
}

export interface ParsedWorkflow {
  inputs?: Record<string, unknown>
  steps: ParsedStep[]
  outputs?: Record<string, string>
}

export interface ParsedDocument {
  dsl: number
  name?: string
  meta?: Record<string, string>
  functions?: string
  workflows: Record<string, ParsedWorkflow>
}

export interface ValidationError {
  stepId: string
  field: string
  message: string
  severity: 'error' | 'warning'
}

// ── Step node data ──
export interface StepNodeData {
  step: ParsedStep
  label: string
  stepType: string
  icon: string
  color: string
  errors: ValidationError[]
  [key: string]: unknown
}

// ── Parse YAML ──
export function parseYaml(yamlStr: string): { doc: ParsedDocument | null; error: string | null } {
  try {
    const raw = yaml.load(yamlStr) as ParsedDocument
    if (!raw || typeof raw !== 'object') return { doc: null, error: 'Invalid YAML: not an object' }
    if (!raw.workflows || typeof raw.workflows !== 'object')
      return { doc: null, error: 'Missing "workflows" section' }
    return { doc: normalizeDocument(raw), error: null }
  } catch (e) {
    return { doc: null, error: e instanceof Error ? e.message : 'YAML parse error' }
  }
}

// ── Serialize back to YAML ──
export function serializeYaml(doc: ParsedDocument): string {
  return yaml.dump(normalizeDocument(doc), {
    indent: 2,
    lineWidth: 120,
    noRefs: true,
    sortKeys: false,
    quotingType: '"',
    forceQuotes: false,
  })
}

// ── Validate a single step ──
export function validateStep(step: ParsedStep): ValidationError[] {
  const errors: ValidationError[] = []

  if (!step.id || step.id.trim() === '') {
    errors.push({ stepId: step.id || '?', field: 'id', message: 'Step ID is required', severity: 'error' })
  } else if (!/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(step.id)) {
    errors.push({ stepId: step.id, field: 'id', message: 'ID must be alphanumeric (a-z, 0-9, _)', severity: 'error' })
  }

  if (!step.type || step.type.trim() === '') {
    errors.push({ stepId: step.id || '?', field: 'type', message: 'Step type is required', severity: 'error' })
  } else if (!STEP_TYPE_MAP.has(step.type)) {
    errors.push({ stepId: step.id, field: 'type', message: `Unknown step type: "${step.type}"`, severity: 'warning' })
  }

  const typeDef = STEP_TYPE_MAP.get(step.type)
  if (typeDef && step.input) {
    const input = step.input as Record<string, unknown>
    for (const field of typeDef.fields) {
      if (field.required && (input[field.name] === undefined || input[field.name] === '')) {
        errors.push({
          stepId: step.id,
          field: field.name,
          message: `"${field.label}" is required`,
          severity: 'error',
        })
      }
    }

    if (step.type === 'mcp.list') {
      const servers = input.servers
      if (!Array.isArray(servers) || servers.length === 0) {
        errors.push({
          stepId: step.id,
          field: 'servers',
          message: '"Servers" must be a non-empty JSON array of MCP server names',
          severity: 'error',
        })
      } else {
        const normalizedServers = servers
          .map(item => typeof item === 'string' ? item.trim() : '')
          .filter(item => item.length > 0)

        if (normalizedServers.length !== servers.length) {
          errors.push({
            stepId: step.id,
            field: 'servers',
            message: 'Each MCP server entry must be a non-empty string',
            severity: 'error',
          })
        }

        if (normalizedServers.includes('*') && normalizedServers.length > 1) {
          errors.push({
            stepId: step.id,
            field: 'servers',
            message: 'Wildcard "*" cannot be combined with explicit MCP server names',
            severity: 'error',
          })
        }
      }
    }
  } else if (typeDef && typeDef.fields.some(f => f.required) && !step.input) {
    errors.push({ stepId: step.id, field: 'input', message: 'Input section is required', severity: 'error' })
  }

  // Validate duplicate IDs will be done at workflow level
  return errors
}

function normalizeDocument(doc: ParsedDocument): ParsedDocument {
  const clone = JSON.parse(JSON.stringify(doc)) as ParsedDocument

  for (const workflow of Object.values(clone.workflows ?? {})) {
    workflow.steps = normalizeSteps(workflow.steps ?? [])
  }

  return clone
}

function normalizeSteps(steps: ParsedStep[]): ParsedStep[] {
  for (const step of steps) {
    normalizeStep(step)
  }

  return steps
}

function normalizeStep(step: ParsedStep): void {
  if (step.type === 'mcp.list') {
    if (!step.input) {
      step.input = {}
    }

    const input = step.input as Record<string, unknown>
    if (input.servers === undefined && typeof input.server === 'string' && input.server.trim() !== '') {
      input.servers = [input.server.trim()]
    }

    delete input.server
  }

  if (step.steps) normalizeSteps(step.steps)
  if (step.branches) step.branches.forEach(branch => normalizeSteps(branch.steps))
  if (step.cases) step.cases.forEach(item => normalizeSteps(item.steps))
  if (step.default) normalizeSteps(step.default)
}

// ── Validate entire workflow (cross-step checks) ──
export function validateWorkflow(steps: ParsedStep[]): ValidationError[] {
  const errors: ValidationError[] = []
  const ids = new Set<string>()

  function walk(list: ParsedStep[]) {
    for (const s of list) {
      const stepErrors = validateStep(s)
      errors.push(...stepErrors)

      if (s.id && ids.has(s.id)) {
        errors.push({ stepId: s.id, field: 'id', message: `Duplicate step ID: "${s.id}"`, severity: 'error' })
      }
      if (s.id) ids.add(s.id)

      if (s.steps) walk(s.steps)
      if (s.branches) s.branches.forEach(b => walk(b.steps))
      if (s.cases) s.cases.forEach(c => walk(c.steps))
      if (s.default) walk(s.default)
    }
  }
  walk(steps)
  return errors
}

// ── Convert steps → React Flow nodes + edges ──
const NODE_WIDTH = 260
const NODE_HEIGHT = 80
const H_GAP = 40
const V_GAP = 30

export function stepsToFlow(
  steps: ParsedStep[],
  allErrors: ValidationError[],
): { nodes: Node[]; edges: Edge[] } {
  const nodes: Node[] = []
  const edges: Edge[] = []
  const errMap = new Map<string, ValidationError[]>()
  for (const e of allErrors) {
    const arr = errMap.get(e.stepId) ?? []
    arr.push(e)
    errMap.set(e.stepId, arr)
  }

  let y = 0

  function addStep(step: ParsedStep, x: number, parentId?: string): string {
    const typeDef = STEP_TYPE_MAP.get(step.type)
    const nodeId = step.id || `unknown-${Math.random().toString(36).slice(2, 8)}`

    nodes.push({
      id: nodeId,
      type: 'stepNode',
      position: { x, y },
      data: {
        step,
        label: step.id,
        stepType: step.type,
        icon: typeDef?.icon ?? '❓',
        color: typeDef?.color ?? '#666',
        errors: errMap.get(step.id) ?? [],
      },
    })

    y += NODE_HEIGHT + V_GAP

    // Sub-steps for loops / sequences
    if (step.steps && step.steps.length > 0) {
      let prevChild: string | null = null
      for (const child of step.steps) {
        const childId = addStep(child, x + H_GAP)
        edges.push({
          id: `${nodeId}-${childId}`,
          source: prevChild ?? nodeId,
          target: childId,
          type: 'smoothstep',
          animated: true,
          style: { stroke: typeDef?.color ?? '#666', strokeWidth: 2 },
        })
        prevChild = childId
      }
    }

    // Branches for parallel
    if (step.branches && step.branches.length > 0) {
      const savedY = y
      let maxY = y
      for (let bi = 0; bi < step.branches.length; bi++) {
        y = savedY
        const bx = x + (bi + 1) * (NODE_WIDTH + H_GAP)
        let prevChild: string | null = null
        for (const child of step.branches[bi].steps) {
          const childId = addStep(child, bx)
          edges.push({
            id: `${nodeId}-b${bi}-${childId}`,
            source: prevChild ?? nodeId,
            target: childId,
            type: 'smoothstep',
            animated: true,
            style: { stroke: '#8b5cf6', strokeWidth: 2 },
          })
          prevChild = childId
        }
        if (y > maxY) maxY = y
      }
      y = maxY
    }

    // Switch cases
    if (step.cases && step.cases.length > 0) {
      const savedY = y
      let maxY = y
      const allCases = [...step.cases, ...(step.default ? [{ steps: step.default, when: 'default' } as ParsedStep & { when: string; steps: ParsedStep[] }] : [])]
      for (let ci = 0; ci < allCases.length; ci++) {
        y = savedY
        const cx = x + (ci + 1) * (NODE_WIDTH + H_GAP)
        let prevChild: string | null = null
        const caseSteps = allCases[ci].steps ?? []
        for (const child of caseSteps) {
          const childId = addStep(child, cx)
          edges.push({
            id: `${nodeId}-c${ci}-${childId}`,
            source: prevChild ?? nodeId,
            target: childId,
            type: 'smoothstep',
            animated: true,
            style: { stroke: '#f59e0b', strokeWidth: 2 },
          })
          prevChild = childId
        }
        if (y > maxY) maxY = y
      }
      y = maxY
    }

    return nodeId
  }

  let prevId: string | null = null
  for (const step of steps) {
    const nodeId = addStep(step, 0)
    if (prevId) {
      edges.push({
        id: `main-${prevId}-${nodeId}`,
        source: prevId,
        target: nodeId,
        type: 'smoothstep',
        style: { stroke: '#4f8ff7', strokeWidth: 2 },
      })
    }
    prevId = nodeId
  }

  return { nodes, edges }
}

// ── Update a step field in the document ──
export function updateStepField(
  doc: ParsedDocument,
  workflowName: string,
  stepId: string,
  field: string,
  value: unknown,
): ParsedDocument {
  const clone = JSON.parse(JSON.stringify(doc)) as ParsedDocument
  const wf = clone.workflows[workflowName]
  if (!wf) return clone

  function walk(steps: ParsedStep[]): boolean {
    for (const s of steps) {
      if (s.id === stepId) {
        if (field === 'id' || field === 'type' || field === 'if' || field === 'output') {
          (s as Record<string, unknown>)[field] = value
        } else if (field === '__raw_input__') {
          // Replace entire input (used by raw JSON editor)
          if (value === undefined || value === null) {
            delete s.input
          } else {
            s.input = value as Record<string, unknown>
          }
        } else {
          if (!s.input) s.input = {}
          if (value === '' || value === undefined) {
            delete s.input[field]
          } else {
            s.input[field] = value
          }
        }
        return true
      }
      if (s.steps && walk(s.steps)) return true
      if (s.branches) for (const b of s.branches) if (walk(b.steps)) return true
      if (s.cases) for (const c of s.cases) if (walk(c.steps)) return true
      if (s.default && walk(s.default)) return true
    }
    return false
  }
  walk(wf.steps)
  return clone
}

// ── Add a step to the workflow ──
export function addStep(
  doc: ParsedDocument,
  workflowName: string,
  stepType: string,
  afterStepId?: string,
): { doc: ParsedDocument; newStepId: string } {
  const clone = JSON.parse(JSON.stringify(doc)) as ParsedDocument
  const wf = clone.workflows[workflowName]
  if (!wf) return { doc: clone, newStepId: '' }

  // Generate unique ID
  const existingIds = new Set<string>()
  function collectIds(steps: ParsedStep[]) {
    for (const s of steps) {
      existingIds.add(s.id)
      if (s.steps) collectIds(s.steps)
      if (s.branches) s.branches.forEach(b => collectIds(b.steps))
      if (s.cases) s.cases.forEach(c => collectIds(c.steps))
      if (s.default) collectIds(s.default)
    }
  }
  collectIds(wf.steps)

  const base = stepType.replace(/\./g, '_')
  let idx = 1
  let newId = base
  while (existingIds.has(newId)) {
    newId = `${base}_${idx++}`
  }

  const typeDef = STEP_TYPE_MAP.get(stepType)
  const newStep: ParsedStep = { id: newId, type: stepType }

  // Pre-fill required fields with defaults
  if (typeDef && typeDef.fields.length > 0) {
    newStep.input = {}
    for (const f of typeDef.fields) {
      if (f.defaultValue !== undefined) {
        newStep.input[f.name] = f.defaultValue
      }
    }
  }

  if (afterStepId) {
    const idx2 = wf.steps.findIndex(s => s.id === afterStepId)
    if (idx2 >= 0) {
      wf.steps.splice(idx2 + 1, 0, newStep)
    } else {
      wf.steps.push(newStep)
    }
  } else {
    wf.steps.push(newStep)
  }

  return { doc: clone, newStepId: newId }
}

// ── Remove a step from the workflow ──
export function removeStep(
  doc: ParsedDocument,
  workflowName: string,
  stepId: string,
): ParsedDocument {
  const clone = JSON.parse(JSON.stringify(doc)) as ParsedDocument
  const wf = clone.workflows[workflowName]
  if (!wf) return clone

  function walk(steps: ParsedStep[]): boolean {
    const idx = steps.findIndex(s => s.id === stepId)
    if (idx >= 0) {
      steps.splice(idx, 1)
      return true
    }
    for (const s of steps) {
      if (s.steps && walk(s.steps)) return true
      if (s.branches) for (const b of s.branches) if (walk(b.steps)) return true
      if (s.cases) for (const c of s.cases) if (walk(c.steps)) return true
      if (s.default && walk(s.default)) return true
    }
    return false
  }
  walk(wf.steps)
  return clone
}


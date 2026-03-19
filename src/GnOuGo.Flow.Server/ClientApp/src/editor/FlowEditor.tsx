/* ─── GnOuGo.Flow Visual Editor ─────────────────────────────
   Main editor component: YAML ↔ visual graph with live sync.
   Uses @xyflow/react (MIT) for node rendering.
   ──────────────────────────────────────────────────────── */
import { useState, useCallback, useMemo, useEffect, useRef } from 'react'
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  useNodesState,
  useEdgesState,
  type Node,
  type Edge,
  type OnSelectionChangeParams,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'

import { StepNode } from './StepNode'
import { StepProperties } from './StepProperties'
import { StepPalette } from './StepPalette'
import {
  parseYaml,
  serializeYaml,
  validateWorkflow,
  stepsToFlow,
  updateStepField,
  addStep as addStepToDoc,
  removeStep,
  type ParsedDocument,
  type StepNodeData,
  type ValidationError,
} from './yamlGraph'

const nodeTypes = { stepNode: StepNode }

interface Props {
  yamlValue: string
  onYamlChange: (yaml: string) => void
}

export function FlowEditor({ yamlValue, onYamlChange }: Props) {
  const [doc, setDoc] = useState<ParsedDocument | null>(null)
  const [parseError, setParseError] = useState<string | null>(null)
  const [validationErrors, setValidationErrors] = useState<ValidationError[]>([])
  const [selectedStepId, setSelectedStepId] = useState<string | null>(null)
  const [workflowName, setWorkflowName] = useState('main')
  const [showPalette, setShowPalette] = useState(false)
  const [isEdge, setIsEdge] = useState(false)

  const [nodes, setNodes, onNodesChange] = useNodesState([] as Node[])
  const [edges, setEdges, onEdgesChange] = useEdgesState([] as Edge[])

  useEffect(() => {
    const ua = navigator.userAgent
    setIsEdge(/Edg\//.test(ua))
  }, [])

  // Track whether the YAML change came from inside the editor (to avoid loops)
  const internalUpdate = useRef(false)
  // Track whether we're rebuilding the graph internally (to preserve selection)
  const rebuildingGraph = useRef(false)
  // Keep a ref of the selected step id for use in the graph rebuild effect
  const selectedStepIdRef = useRef<string | null>(null)
  selectedStepIdRef.current = selectedStepId

  // ── Parse YAML when it changes externally ──
  useEffect(() => {
    if (internalUpdate.current) {
      internalUpdate.current = false
      return
    }
    const { doc: parsed, error } = parseYaml(yamlValue)
    if (error) {
      setParseError(error)
      setDoc(null)
      return
    }
    setParseError(null)
    setDoc(parsed)

    // Pick first workflow if current doesn't exist
    if (parsed && !parsed.workflows[workflowName]) {
      const names = Object.keys(parsed.workflows)
      if (names.length > 0) setWorkflowName(names[0])
    }
  }, [yamlValue])

  // ── Rebuild graph when doc or workflow changes ──
  useEffect(() => {
    if (!doc) {
      setNodes([])
      setEdges([])
      setValidationErrors([])
      return
    }
    const wf = doc.workflows[workflowName]
    if (!wf) {
      setNodes([])
      setEdges([])
      return
    }

    const errs = validateWorkflow(wf.steps)
    setValidationErrors(errs)

    const { nodes: newNodes, edges: newEdges } = stepsToFlow(wf.steps, errs)

    // Mark selected node so ReactFlow preserves the selection state
    if (selectedStepIdRef.current) {
      for (const n of newNodes) {
        if (n.id === selectedStepIdRef.current) {
          n.selected = true
        }
      }
    }

    rebuildingGraph.current = true
    setNodes(newNodes)
    setEdges(newEdges)
    // Reset the flag after React processes the state update
    requestAnimationFrame(() => {
      rebuildingGraph.current = false
    })
  }, [doc, workflowName])

  // ── Selection ──
  const onSelectionChange = useCallback(({ nodes: sel }: OnSelectionChangeParams) => {
    // Skip selection resets triggered by internal graph rebuilds
    if (rebuildingGraph.current) return
    if (sel.length === 1) {
      setSelectedStepId(sel[0].id)
    } else {
      setSelectedStepId(null)
    }
  }, [])

  // ── Propagate doc changes → YAML ──
  const updateDoc = useCallback((newDoc: ParsedDocument) => {
    setDoc(newDoc)
    internalUpdate.current = true
    onYamlChange(serializeYaml(newDoc))
  }, [onYamlChange])

  // ── Step field change ──
  const handleFieldChange = useCallback((field: string, value: unknown) => {
    if (!doc || !selectedStepId) return
    const newDoc = updateStepField(doc, workflowName, selectedStepId, field, value)
    // If ID changed, update selection
    if (field === 'id' && typeof value === 'string') {
      setSelectedStepId(value)
    }
    updateDoc(newDoc)
  }, [doc, selectedStepId, workflowName, updateDoc])

  // ── Change step type ──
  const handleChangeType = useCallback((newType: string) => {
    if (!doc || !selectedStepId) return
    const newDoc = updateStepField(doc, workflowName, selectedStepId, 'type', newType)
    updateDoc(newDoc)
  }, [doc, selectedStepId, workflowName, updateDoc])

  // ── Delete step ──
  const handleDelete = useCallback(() => {
    if (!doc || !selectedStepId) return
    const newDoc = removeStep(doc, workflowName, selectedStepId)
    setSelectedStepId(null)
    updateDoc(newDoc)
  }, [doc, selectedStepId, workflowName, updateDoc])

  // ── Add step ──
  const handleAddStep = useCallback((stepType: string) => {
    if (!doc) return
    const { doc: newDoc, newStepId } = addStepToDoc(doc, workflowName, stepType, selectedStepId ?? undefined)
    setSelectedStepId(newStepId)
    setShowPalette(false)
    updateDoc(newDoc)
  }, [doc, workflowName, selectedStepId, updateDoc])

  // ── Selected step data ──
  const selectedStep = useMemo(() => {
    if (!doc || !selectedStepId) return null
    const wf = doc.workflows[workflowName]
    if (!wf) return null

    function find(steps: { id: string; [k: string]: unknown }[]): typeof steps[0] | null {
      for (const s of steps) {
        if (s.id === selectedStepId) return s
        if (Array.isArray(s.steps)) { const r = find(s.steps as typeof steps); if (r) return r }
        if (Array.isArray(s.branches)) for (const b of s.branches as { steps: typeof steps }[]) { const r = find(b.steps); if (r) return r }
        if (Array.isArray(s.cases)) for (const c of s.cases as { steps: typeof steps }[]) { const r = find(c.steps); if (r) return r }
        if (Array.isArray(s.default)) { const r = find(s.default as typeof steps); if (r) return r }
      }
      return null
    }
    return find(wf.steps as { id: string }[])
  }, [doc, selectedStepId, workflowName])

  const selectedErrors = useMemo(
    () => validationErrors.filter(e => e.stepId === selectedStepId),
    [validationErrors, selectedStepId],
  )

  const workflowNames = doc ? Object.keys(doc.workflows) : []

  return (
    <div className={`flow-editor${isEdge ? ' flow-editor--edge' : ''}`}>
      {/* ── Toolbar ── */}
      <div className="flow-editor__toolbar">
        <div className="flow-editor__toolbar-left">
          <label>Workflow:</label>
          <select value={workflowName} onChange={e => setWorkflowName(e.target.value)}>
            {workflowNames.map(n => <option key={n} value={n}>{n}</option>)}
          </select>
          <button className="btn-add" onClick={() => setShowPalette(!showPalette)}>
            {showPalette ? '✕ Close' : '➕ Add Step'}
          </button>
        </div>
        <div className="flow-editor__toolbar-right">
          <span className={`validation-badge ${validationErrors.filter(e => e.severity === 'error').length > 0 ? 'validation-badge--has-errors' : validationErrors.length > 0 ? 'validation-badge--has-warnings' : 'validation-badge--valid'}`}>
            {validationErrors.filter(e => e.severity === 'error').length > 0
              ? `❌ ${validationErrors.filter(e => e.severity === 'error').length} errors`
              : validationErrors.length > 0
                ? `⚠️ ${validationErrors.length} warnings`
                : '✅ Valid'}
          </span>
        </div>
      </div>

      {parseError && (
        <div className="flow-editor__parse-error">
          ⚠️ YAML parse error: {parseError}
        </div>
      )}

      <div className="flow-editor__content">
        {/* ── Palette (left) ── */}
        {showPalette && (
          <div className="flow-editor__palette">
            <StepPalette onAdd={handleAddStep} />
          </div>
        )}

        {/* ── Graph (center) ── */}
        <div className="flow-editor__graph">
          <ReactFlow
            nodes={nodes}
            edges={edges}
            onNodesChange={onNodesChange}
            onEdgesChange={onEdgesChange}
            onSelectionChange={onSelectionChange}
            nodeTypes={nodeTypes}
            fitView
            fitViewOptions={{ padding: 0.2 }}
            minZoom={0.2}
            maxZoom={2}
            proOptions={{ hideAttribution: true }}
          >
            <Background color="#2a2d3a" gap={20} />
            <Controls />
            <MiniMap
              nodeColor={(n) => {
                const d = n.data as Record<string, unknown> | undefined
                return (d?.color as string) ?? '#666'
              }}
              style={{ background: '#12141c' }}
            />
          </ReactFlow>
        </div>

        {/* ── Properties (right) ── */}
        {selectedStep && (
          <div className="flow-editor__props">
            <StepProperties
              step={selectedStep as import('./yamlGraph').ParsedStep}
              errors={selectedErrors}
              onChange={handleFieldChange}
              onDelete={handleDelete}
              onChangeType={handleChangeType}
            />
          </div>
        )}
      </div>
    </div>
  )
}


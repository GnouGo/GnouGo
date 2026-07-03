/* ─── GnOuGo.Flow Step Type Registry ──────────────────────────────
   Defines all known step types, their required/optional input
   fields, and which composite children they support.
   Used by the visual editor for validation and rendering.
   ────────────────────────────────────────────────────────────── */

export interface FieldDef {
  name: string
  label: string
  type: 'string' | 'text' | 'number' | 'boolean' | 'json' | 'expression' | 'select'
  required?: boolean
  placeholder?: string
  options?: string[]  // for select
  defaultValue?: string | number | boolean | Record<string, unknown> | unknown[]
  description?: string
}

export interface StepTypeDef {
  type: string
  label: string
  icon: string
  category: 'control' | 'template' | 'ai' | 'mcp' | 'workflow' | 'chat'
  color: string
  fields: FieldDef[]
  /** Whether this step type has sub-steps (sequence, loop, switch default, etc.) */
  hasSteps?: boolean
  /** Whether this step type has branches (parallel) */
  hasBranches?: boolean
  /** Whether this step type has switch cases */
  hasCases?: boolean
  description: string
}

export const STEP_TYPES: StepTypeDef[] = [
  // ── Control flow ──
  {
    type: 'sequence',
    label: 'Sequence',
    icon: '📋',
    category: 'control',
    color: '#6366f1',
    hasSteps: true,
    description: 'Execute sub-steps sequentially',
    fields: [],
  },
  {
    type: 'parallel',
    label: 'Parallel',
    icon: '⚡',
    category: 'control',
    color: '#8b5cf6',
    hasBranches: true,
    description: 'Execute branches in parallel',
    fields: [
      { name: 'max_concurrency', label: 'Max concurrency', type: 'number', placeholder: '3', description: 'Optional — limit parallel branches' },
    ],
  },
  {
    type: 'loop.sequential',
    label: 'Loop (Sequential)',
    icon: '🔁',
    category: 'control',
    color: '#0ea5e9',
    hasSteps: true,
    description: 'Loop with a fixed count or while condition',
    fields: [
      { name: 'times', label: 'Times', type: 'number', placeholder: '5', description: 'Fixed iteration count' },
      { name: 'while', label: 'While', type: 'expression', placeholder: '${data.steps.loop.count < 10}', description: 'Loop while true (alternative to times)' },
      { name: 'max_times', label: 'Max iterations', type: 'number', placeholder: '100', description: 'Safety limit' },
    ],
  },
  {
    type: 'loop.parallel',
    label: 'Loop (Parallel)',
    icon: '🔄',
    category: 'control',
    color: '#06b6d4',
    hasSteps: true,
    description: 'Iterate items in parallel',
    fields: [
      { name: 'items', label: 'Items', type: 'expression', required: true, placeholder: '${data.inputs.list}' },
      { name: 'max_concurrency', label: 'Max concurrency', type: 'number', placeholder: '5' },
    ],
  },
  {
    type: 'switch',
    label: 'Switch',
    icon: '🔀',
    category: 'control',
    color: '#f59e0b',
    hasCases: true,
    description: 'Branch based on conditions',
    fields: [
      { name: 'expr', label: 'Expression (form A)', type: 'expression', placeholder: '${data.inputs.mode}' },
    ],
  },
  {
    type: 'set',
    label: 'Set Variables',
    icon: '📌',
    category: 'control',
    color: '#64748b',
    description: 'Set or compute variables (each key in input becomes a variable)',
    fields: [],
  },
  {
    type: 'emit',
    label: 'Emit',
    icon: '💬',
    category: 'control',
    color: '#a3e635',
    description: 'Emit a thinking / info / progress / response message',
    fields: [
      { name: 'message', label: 'Message', type: 'text', required: true, placeholder: 'Processing ${len(data.inputs.items)} items…' },
      { name: 'level', label: 'Level', type: 'select', options: ['thinking', 'info', 'progress', 'response'], defaultValue: 'thinking', description: 'Message level' },
    ],
  },

  // ── Template ──
  {
    type: 'template.render',
    label: 'Template Render',
    icon: '📝',
    category: 'template',
    color: '#10b981',
    description: 'Render a Mustache template',
    fields: [
      { name: 'engine', label: 'Engine', type: 'select', options: ['mustache'], defaultValue: 'mustache' },
      { name: 'template', label: 'Template', type: 'text', required: true, placeholder: 'Hello {{name}}' },
      { name: 'data', label: 'Data', type: 'json', placeholder: '{ "name": "${data.inputs.name}" }' },
      { name: 'mode', label: 'Mode', type: 'select', options: ['text', 'json'], defaultValue: 'text' },
    ],
  },

  // ── AI / LLM ──
  {
    type: 'llm.call',
    label: 'LLM Call',
    icon: '🤖',
    category: 'ai',
    color: '#ec4899',
    description: 'Call a language model',
    fields: [
      { name: 'model', label: 'Model', type: 'string', placeholder: 'gpt-4' },
      { name: 'prompt', label: 'Prompt', type: 'text', required: true, placeholder: 'Summarize: ${data.steps.prev.text}' },
      { name: 'system', label: 'System prompt', type: 'text', placeholder: 'You are a helpful assistant.' },
      { name: 'temperature', label: 'Temperature', type: 'number', defaultValue: 0.7, placeholder: '0.7' },
      { name: 'max_tokens', label: 'Max tokens', type: 'number', placeholder: '2048' },
    ],
  },

  // ── MCP ──
  {
    type: 'mcp.list',
    label: 'MCP List',
    icon: '📡',
    category: 'mcp',
    color: '#f97316',
    description: 'List tools/resources/prompts from one or more MCP servers',
    fields: [
      { name: 'servers', label: 'Servers', type: 'json', required: true, placeholder: '["demo"]', defaultValue: ['*'], description: 'Array of MCP server names, or ["*"] to inspect all configured servers' },
      { name: 'include', label: 'Include', type: 'json', placeholder: '["tools", "prompts"]', description: 'Optional capabilities to include: tools, resources, prompts' },
    ],
  },
  {
    type: 'mcp.call',
    label: 'MCP Call',
    icon: '📞',
    category: 'mcp',
    color: '#f97316',
    description: 'Call MCP tool or prompt',
    fields: [
      { name: 'server', label: 'Server', type: 'string', required: true, placeholder: 'demo' },
      { name: 'kind', label: 'Kind', type: 'select', options: ['tool', 'prompt'], defaultValue: 'tool' },
      { name: 'method', label: 'Method', type: 'string', placeholder: 'get_weather' },
      { name: 'methods', label: 'Methods (batch)', type: 'json', placeholder: '["method1", "method2"]' },
      { name: 'request', label: 'Request', type: 'json', placeholder: '{ "location": "Paris" }' },
    ],
  },

  // ── Workflow ──
  {
    type: 'workflow.call',
    label: 'Workflow Call',
    icon: '🔗',
    category: 'workflow',
    color: '#a855f7',
    description: 'Call a local or remote sub-workflow',
    fields: [
      { name: 'ref', label: 'Ref (JSON)', type: 'json', required: true, placeholder: '{ "kind": "local", "name": "helper" }', description: 'kind: "local" or "url", name: workflow name or URL' },
      { name: 'args', label: 'Arguments', type: 'json', placeholder: '{ "key": "${data.inputs.value}" }' },
    ],
  },
  {
    type: 'workflow.plan',
    label: 'Workflow Plan',
    icon: '🗂️',
    category: 'workflow',
    color: '#a855f7',
    description: 'Generate a workflow dynamically via LLM',
    fields: [
      { name: 'mode', label: 'Mode', type: 'select', options: ['auto', 'basic', 'pipeline'], defaultValue: 'auto', description: 'auto classifies complexity first; basic generates one workflow; pipeline decomposes a raw prompt' },
      { name: 'raw_prompt', label: 'Raw prompt', type: 'string', placeholder: '${data.inputs.prompt}', description: 'Raw user automation prompt used by auto or pipeline mode' },
      { name: 'generator', label: 'Generator (JSON)', type: 'json', required: true, placeholder: '{ "model": "gpt-4", "instruction": "Build a ...", "context": "" }', description: 'LLM generator config: model, instruction, context' },
      { name: 'policy', label: 'Policy (JSON)', type: 'json', placeholder: '{ "allowed_step_types": ["llm.call", "template.render"] }', description: 'Step type allowlist/denylist, allow_remote_workflow_refs' },
      { name: 'limits', label: 'Limits (JSON)', type: 'json', placeholder: '{ "max_steps_total": 10 }' },
      { name: 'validate', label: 'Validate (JSON)', type: 'json', placeholder: '{ "compile": true }', description: 'compile: validate generated YAML' },
      { name: 'on_invalid', label: 'On Invalid (JSON)', type: 'json', placeholder: '{ "action": "reprompt", "max_attempts": 3 }', description: 'action: "fail" or "reprompt"' },
    ],
  },
  {
    type: 'workflow.execute',
    label: 'Workflow Execute',
    icon: '🚀',
    category: 'workflow',
    color: '#a855f7',
    description: 'Execute a workflow generated by workflow.plan',
    fields: [
      { name: 'from_step', label: 'From step', type: 'string', required: true, placeholder: 'plan_step_id', description: 'ID of the workflow.plan step whose YAML to execute' },
      { name: 'args', label: 'Arguments', type: 'json', placeholder: '{ "key": "value" }', description: 'Input arguments for the generated workflow' },
    ],
  },

  // ── Chat History ──
  {
    type: 'chat_history.get',
    label: 'Chat History Get',
    icon: '💬',
    category: 'chat',
    color: '#14b8a6',
    description: 'Retrieve chat history messages',
    fields: [
      { name: 'conversation_id', label: 'Conversation ID', type: 'expression', required: true, placeholder: '${data.inputs.session}' },
      { name: 'top_k', label: 'Max messages', type: 'number', placeholder: '50', description: 'Maximum messages to retrieve (default: 50)' },
    ],
  },
  {
    type: 'chat_history.append',
    label: 'Chat History Append',
    icon: '📨',
    category: 'chat',
    color: '#14b8a6',
    description: 'Append messages to chat history',
    fields: [
      { name: 'conversation_id', label: 'Conversation ID', type: 'expression', placeholder: '${data.inputs.session}', description: 'Optional — null creates a new conversation' },
      { name: 'messages', label: 'Messages (JSON)', type: 'json', required: true, placeholder: '[{ "role": "assistant", "content": "${data.steps.llm.text}" }]', description: 'Array of { role, content, meta? }' },
    ],
  },

  // ── Human-in-the-Loop ──
  {
    type: 'human.input',
    label: 'Human Input',
    icon: '🙋',
    category: 'control',
    color: '#ff9800',
    description: 'Pause the workflow and wait for human input',
    fields: [
      { name: 'prompt', label: 'Prompt', type: 'text', required: true, placeholder: 'Please review the plan and approve or reject.' },
      { name: 'mode', label: 'Mode', type: 'select', options: ['text', 'choice', 'form', 'confirm'], placeholder: 'choice', description: 'Interaction mode; old choices/fields shape is still inferred when omitted' },
      { name: 'choices', label: 'Choices (JSON)', type: 'json', placeholder: '["approve", "reject", "modify"]', description: 'Quick-reply buttons shown to the user' },
      { name: 'fields', label: 'Form fields (JSON)', type: 'json', placeholder: '[{ "name": "due_date", "type": "date", "required": true }]', description: 'Structured form fields. Known types include string, text, number, integer, boolean, select, date, password, json, yaml.' },
      { name: 'context', label: 'Context', type: 'expression', placeholder: '${json(data.steps.plan)}', description: 'Structured data shown alongside the prompt' },
      { name: 'timeout_ms', label: 'Timeout (ms)', type: 'number', placeholder: '36000000', description: 'Timeout in milliseconds (default 10 hours)' },
    ],
  },
]

export const STEP_TYPE_MAP = new Map(STEP_TYPES.map(t => [t.type, t]))

export const CATEGORIES = [
  { key: 'control', label: 'Control Flow', icon: '⚙️' },
  { key: 'template', label: 'Template', icon: '📝' },
  { key: 'ai', label: 'AI / LLM', icon: '🤖' },
  { key: 'mcp', label: 'MCP', icon: '📡' },
  { key: 'workflow', label: 'Workflow', icon: '🔗' },
  { key: 'chat', label: 'Chat', icon: '💬' },
] as const

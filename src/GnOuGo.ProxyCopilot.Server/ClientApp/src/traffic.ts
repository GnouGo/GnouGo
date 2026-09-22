export interface Summary {
  id: string; tenantId: string; provider: string; model: string; protocol: string; startedAt: string
  status: string; statusCode: number | null; durationMs: number; firstTokenMs: number | null
  usage: { prompt_tokens: number; completion_tokens: number; total_tokens: number } | null
  error: string | null; truncated: boolean
}
export interface Detail { summary: Summary; bodies: Record<string, { text: string; truncated: boolean }> }
export interface Tool { id: string; name: string; arguments: string }
export interface Output { content: string; tools: Tool[] }

export function object(text: string): Record<string, unknown> | null {
  try { const value: unknown = JSON.parse(text); return typeof value === 'object' && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : null }
  catch { return null }
}

export function pretty(text: string): string {
  try { return JSON.stringify(JSON.parse(text), null, 2) }
  catch { return text }
}

export function output(text: string): Output {
  const result: Output = { content: '', tools: [] }
  const tools = new Map<number, Tool>()
  const complete = object(text)
  const events = complete ? [complete] : text.split(/\r?\n\r?\n/).flatMap((frame, index, frames) => {
    // The last partial frame is withheld until a later live snapshot completes it.
    if (index === frames.length - 1) return []
    const data = frame.split(/\r?\n/).filter(line => line.startsWith('data:')).map(line => line.slice(5).trimStart()).join('\n')
    const parsed = object(data)
    return parsed ? [parsed] : []
  })
  for (const event of events) {
    if (!Array.isArray(event.choices)) continue
    for (const choice of event.choices) {
      const message = choice?.delta ?? choice?.message
      if (!message) continue
      if (typeof message.content === 'string') result.content += message.content
      if (!Array.isArray(message.tool_calls)) continue
      for (const [position, call] of message.tool_calls.entries()) {
        const index: number = typeof call.index === 'number' ? call.index : position
        const tool = tools.get(index) ?? { id: '', name: '', arguments: '' }
        if (typeof call.id === 'string') tool.id = call.id
        if (typeof call.function?.name === 'string') tool.name += call.function.name
        if (typeof call.function?.arguments === 'string') tool.arguments += call.function.arguments
        tools.set(index, tool)
      }
    }
  }
  result.tools = [...tools.entries()].sort(([a], [b]) => a - b).map(([, tool]) => tool)
  return result
}

export function duration(milliseconds: number | null): string {
  return milliseconds === null ? '—' : milliseconds < 1000 ? `${Math.round(milliseconds)} ms` : `${(milliseconds / 1000).toFixed(2)} s`
}

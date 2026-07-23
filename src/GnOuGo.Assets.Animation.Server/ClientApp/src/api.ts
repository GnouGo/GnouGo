import type { ApiError, SimulationRequest, StreamEnvelope, ValidationResponse } from './types'

async function readApiError(response: Response): Promise<string> {
  try {
    const body = await response.json() as ApiError
    return body.message || `${response.status} ${response.statusText}`
  } catch {
    return `${response.status} ${response.statusText}`
  }
}

export async function validatePreview(request: SimulationRequest, signal?: AbortSignal): Promise<ValidationResponse> {
  const response = await fetch('/api/simulations/validate', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  })
  if (!response.ok) throw new Error(await readApiError(response))
  return await response.json() as ValidationResponse
}

export async function streamSimulation(
  request: SimulationRequest,
  onEnvelope: (envelope: StreamEnvelope) => void | Promise<void>,
  signal: AbortSignal,
): Promise<void> {
  const response = await fetch('/api/simulations/stream', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  })
  if (!response.ok) throw new Error(await readApiError(response))
  if (!response.body) throw new Error('The server returned no simulation stream.')

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  while (true) {
    const { done, value } = await reader.read()
    buffer += decoder.decode(value ?? new Uint8Array(), { stream: !done })
    const lines = buffer.split('\n')
    buffer = lines.pop() ?? ''
    for (const line of lines) {
      if (line.trim()) await onEnvelope(JSON.parse(line) as StreamEnvelope)
    }
    if (done) break
  }
  if (buffer.trim()) await onEnvelope(JSON.parse(buffer) as StreamEnvelope)
}

import test from 'node:test'
import assert from 'node:assert/strict'
import { output, upstreamError } from './traffic.ts'
import type { Detail } from './traffic.ts'

test('live output reconstructs parallel tool arguments and withholds incomplete frames', () => {
  const frame = (delta: unknown) => `data: ${JSON.stringify({ choices: [{ delta }] })}\n\n`
  const first = frame({ content: 'Hello 🌍', tool_calls: [{ index: 1, id: 'b', function: { name: 'second', arguments: '{"n":' } }, { index: 0, id: 'a', function: { name: 'first', arguments: '{}' } }] })
  const second = frame({ tool_calls: [{ index: 1, function: { arguments: '2}' } }] })
  assert.deepEqual(output(first + second + 'data: {"choices":'), { content: 'Hello 🌍', tools: [{ id: 'a', name: 'first', arguments: '{}' }, { id: 'b', name: 'second', arguments: '{"n":2}' }] })
})
test('completed JSON and usage-only SSE records are supported', () => {
  assert.equal(output('{"choices":[{"message":{"content":"Ready"}}]}').content, 'Ready')
  assert.deepEqual(output('data: {"choices":[],"usage":{"total_tokens":3}}\n\ndata: [DONE]\n\n'), { content: '', tools: [] })
})

test('failed calls expose the already-redacted provider message without parsing HTML', () => {
  const detail = (status: string, text: string) => ({ summary: { status }, bodies: { upstreamResponse: { text, truncated: false } } }) as Detail
  const message = 'Tools require reasoning_effort=none. Credential: [REDACTED]. <b>plain text</b>'
  assert.equal(upstreamError(detail('failed', JSON.stringify({ error: { message } }))), message)
  assert.equal(upstreamError(detail('failed', JSON.stringify({ error: message }))), message)
  assert.equal(upstreamError(detail('completed', JSON.stringify({ error: { message } }))), null)
  assert.equal(upstreamError(detail('failed', '<html>Gateway error</html>')), null)
  assert.equal(upstreamError(detail('failed', '{"error":{"message":')), null)
  assert.equal(upstreamError(detail('failed', '{"error":{"message":42}}')), null)
})

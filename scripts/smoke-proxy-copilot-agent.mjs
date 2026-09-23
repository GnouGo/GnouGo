// Real desktop VS Code + real configured provider. This script never implements
// tools, supplies model responses, or changes the files under test.
// Approve the scoped file/terminal actions in the isolated VS Code window.
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { createHash } from 'node:crypto'
import { mkdir, mkdtemp, open, readFile, realpath, writeFile } from 'node:fs/promises'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { setTimeout as delay } from 'node:timers/promises'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE_PATH || 'playwright')
const base = new URL(process.env.PROXY_SMOKE_URL || 'http://127.0.0.1:5087')
assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(base.hostname), 'Use a local GnOuGo proxy')
const codeBinary = process.env.VSCODE_EXECUTABLE || '/Applications/Visual Studio Code.app/Contents/MacOS/Code'
const selectedId = process.env.PROXY_AGENT_MODEL_ID
const label = 'GnOuGo smoke model'
const timeoutMs = Number(process.env.PROXY_AGENT_TIMEOUT_MS || 600000)
// macOS's per-user tmpdir can exceed Electron's Unix socket path limit.
const root = await realpath(await mkdtemp(join(process.platform === 'darwin' ? '/tmp' : tmpdir(), 'gnougo-agent-smoke-')))
const workspace = join(root, 'workspace')
const userData = join(root, 'user-data')
await mkdir(workspace)
await mkdir(join(userData, 'User'), { recursive: true })
const json = async path => {
  const response = await fetch(new URL(path, base))
  assert.equal(response.status, 200, `Proxy ${path.split('/').slice(0, 3).join('/')} failed`)
  return response.json()
}
const setup = await json('/api/setup')
const available = setup.configuration.flatMap(group => group.models)
const model = available.find(item => item.toolCalling && (!selectedId || item.id === selectedId))
assert.ok(model, 'Configure a real provider with SupportsTools=true; optionally set PROXY_AGENT_MODEL_ID')
const configuration = [{ name: 'GnOuGo Smoke', vendor: 'customendpoint', apiType: 'chat-completions', models: [{ ...model, name: label }] }]
const save = (path, value) => writeFile(path, JSON.stringify(value, null, 2) + '\n', { mode: 0o600 })
await save(join(userData, 'User', 'chatLanguageModels.json'), configuration)
await save(join(userData, 'User', 'settings.json'), {
  'telemetry.telemetryLevel': 'off', 'update.mode': 'none', 'extensions.autoUpdate': false,
  'extensions.autoCheckUpdates': false, 'workbench.startupEditor': 'none',
  'security.workspace.trust.enabled': false, 'chat.disableAIFeatures': false,
  'editor.accessibilitySupport': 'on', 'editor.experimentalEditContextEnabled': false,
  'terminal.integrated.profiles.osx': { 'Smoke shell': { path: '/bin/zsh', args: ['-f'] } },
  'terminal.integrated.defaultProfile.osx': 'Smoke shell',
  'terminal.integrated.env.osx': { PROMPT: 'smoke% ' },
})
const prompt = `Perform an end-to-end Agent tool smoke test in this empty workspace. Execute the steps with the built-in VS Code tools. Do not give me instructions to run myself. Stay inside the workspace and do not access external services. Follow the dependencies in order:
1. Inspect the workspace using the directory or search tools.
2. Use create_file to create agent-smoke.txt containing two lines: stage=created and value=6. Use real newline characters.
3. Read the file with read_file, then use a file editing tool to change the same file to stage=edited and value=7. Do not use shell commands to create or edit files.
4. Only after the edit succeeds, use run_in_terminal to start a python3 command in the background (async). The command must read agent-smoke.txt, multiply its stored value by 6, generate a fresh random nonce with secrets.token_hex(8), wait 3 seconds, and print RESULT=<computed value> and NONCE=<nonce>.
5. After starting that command, call get_terminal_output with its terminal ID and read the successful RESULT and NONCE. This explicit output-tool call is required even if run_in_terminal already returned the output. Wait and poll again if needed. Recover from any errors using the actual tool results.
6. From the observed get_terminal_output result, create agent-result.json with numeric observedResult, string observedNonce, and a short explanation of how the edited file value produced that result.
7. Read both files with two parallel read_file calls to verify them. Finish with the observed result and nonce and state what you executed. Never invent output.`
await writeFile(join(root, 'prompt.txt'), prompt, { mode: 0o600 })
const baseline = new Set((await json('/api/traffic')).calls.map(call => call.id))
const portServer = createServer()
await new Promise(resolve => portServer.listen(0, '127.0.0.1', resolve))
const port = portServer.address().port
await new Promise(resolve => portServer.close(resolve))
const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => key !== 'ELECTRON_RUN_AS_NODE' && !key.startsWith('VSCODE_')))
const log = await open(join(root, 'vscode.log'), 'w', 0o600)
const code = spawn(codeBinary, [
  '--user-data-dir', userData, '--extensions-dir', process.env.VSCODE_SMOKE_EXTENSIONS_DIR || join(root, 'extensions'),
  `--remote-debugging-port=${port}`, '--remote-debugging-address=127.0.0.1',
  '--skip-welcome', '--skip-release-notes', '--disable-workspace-trust', '--new-window', workspace,
], { env, stdio: ['ignore', log.fd, log.fd] })
let launchError
code.on('error', error => { launchError = error })
let browser
let page
console.log(`Real Agent smoke workspace: ${workspace}`)
console.log('Using the real configured provider. Approve only this test’s file/terminal actions in the isolated VS Code window.')
try {
  const deadline = Date.now() + timeoutMs
  while (!browser && Date.now() < deadline) {
    if (launchError || code.exitCode !== null) throw new Error('VS Code could not start; check VSCODE_EXECUTABLE')
    try { browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`) } catch { await delay(500) }
  }
  assert.ok(browser, 'VS Code debugging endpoint did not start')
  while (!page && Date.now() < deadline) {
    page = browser.contexts().flatMap(context => context.pages()).find(p => p.url().includes('workbench'))
    if (!page) await delay(500)
  }
  assert.ok(page, 'VS Code workbench did not start')
  page.setDefaultTimeout(30000)
  const modelPicker = page.getByRole('button', { name: /^Models,/ }).filter({ visible: true })
  if (!(await modelPicker.count())) await page.keyboard.press(process.platform === 'darwin' ? 'Control+Meta+i' : 'Control+Alt+i')
  await modelPicker.waitFor()
  await modelPicker.click()
  let entry = page.getByRole('option', { name: /^GnOuGo smoke model,/ }).filter({ visible: true })
  if (!(await entry.count())) {
    const manage = page.getByText(/Manage (Language )?Models/).filter({ visible: true })
    await manage.first().click()
    await delay(2000)
    await page.keyboard.press('Escape')
    await modelPicker.click()
  }
  entry = page.getByRole('option', { name: /^GnOuGo smoke model,/ }).filter({ visible: true })
  await entry.first().click()
  const input = page.getByRole('textbox', { name: /^Chat Input \(Agent\)/ }).filter({ visible: true })
  await input.waitFor()
  await input.focus()
  await page.keyboard.insertText(prompt)
  await page.waitForFunction(() => document.body.innerText.includes('Perform an end-to-end Agent tool smoke test'))
  await page.keyboard.press('Enter')
  console.log('Submitted the smoke prompt through the real Agent chat UI.')

  let lastProgress = ''
  let evidence
  while (Date.now() < deadline) {
    const snapshot = await json('/api/traffic')
    const calls = snapshot.calls.filter(call => !baseline.has(call.id) && call.model === model.id).reverse()
    const records = []
    for (const call of calls) {
      if (call.status === 'running') continue
      assert.equal(call.status, 'completed', 'A real proxy request failed; inspect the local dashboard')
      const detail = await json(`/api/traffic/${call.id}`)
      assert.ok(!detail.bodies.clientRequest.truncated && !detail.bodies.clientResponse.truncated,
        'Capture was truncated; set ProxyCopilot__Capture__MaxBodyBytes=4194304 for this test')
      records.push({ call, request: JSON.parse(detail.bodies.clientRequest.text), response: response(detail.bodies.clientResponse.text) })
    }
    const tools = records.flatMap(record => record.response.calls.map(call => call.function.name))
    const progress = `${records.length} completed model turns; tools: ${[...new Set(tools)].join(', ')}`
    if (progress !== lastProgress) { console.log(progress); lastProgress = progress }
    if (records.length && !records.at(-1).response.calls.length && records.at(-1).response.text) {
      evidence = await verify(records)
      break
    }
    await delay(1000)
  }
  assert.ok(evidence, 'Agent did not complete before the timeout; check approvals in the isolated VS Code window')
  const keep = page.getByText('Keep', { exact: true }).filter({ visible: true })
  if (await keep.count()) await keep.first().click()
  const maximize = page.getByRole('button', { name: 'Maximize Secondary Side Bar', exact: true })
  if (await maximize.count()) await maximize.click()
  await page.mouse.move(1000, 450)
  await page.mouse.wheel(0, 4000)
  await delay(300)
  // Capture the actual editor, not a recreated dashboard or mocked transcript.
  await page.screenshot({ path: join(root, 'vscode-agent-success.png'), fullPage: true })
  const app = resolve(dirname(codeBinary), '../Resources/app')
  try {
    const codePackage = JSON.parse(await readFile(join(app, 'package.json'), 'utf8'))
    const copilotPackage = JSON.parse(await readFile(join(app, 'extensions/copilot/package.json'), 'utf8'))
    const source = await readFile(join(app, 'extensions/copilot/dist/extension.js'))
    evidence.versions = { vscode: codePackage.version, copilot: copilotPackage.version, copilotSourceSha256: createHash('sha256').update(source).digest('hex') }
  } catch { evidence.versions = { note: 'Record the installed VS Code and Copilot versions manually on this platform.' } }
  evidence.configuration = [{ ...configuration[0], models: [{ ...configuration[0].models[0], id: '<provider>/<model-alias>' }] }]
  await save(join(root, 'evidence.json'), evidence)
  console.log(`PASS: VS Code inspected, created, read, edited, ran a shell command, retrieved its output and continued from it. RESULT=${evidence.result}, NONCE=${evidence.nonce}.`)
  console.log(`Reviewed evidence and screenshot are local: ${root}`)
} catch (error) {
  if (page) await page.screenshot({ path: join(root, 'vscode-agent-failure.png'), fullPage: true }).catch(() => {})
  console.error(`FAIL: ${error.message}`)
  process.exitCode = 1
} finally {
  if (browser) await browser.close()
  if (process.env.KEEP_VSCODE_SMOKE_OPEN !== '1') code.kill('SIGTERM')
  await log.close()
}

function response(text) {
  const calls = new Map()
  let content = ''
  assert.ok(text.includes('data: [DONE]'), 'Expected a completed SSE response')
  for (const line of text.split('\n')) {
    if (!line.startsWith('data: {')) continue
    const chunk = JSON.parse(line.slice(6))
    for (const choice of chunk.choices) {
      content += choice.delta?.content || ''
      for (const fragment of choice.delta?.tool_calls || []) {
        const call = calls.get(fragment.index) || { id: '', type: 'function', function: { name: '', arguments: '' } }
        call.id += fragment.id || ''
        call.function.name += fragment.function?.name || ''
        call.function.arguments += fragment.function?.arguments || ''
        calls.set(fragment.index, call)
      }
    }
  }
  return { text: content, calls: [...calls.values()] }
}

async function verify(records) {
  const issued = new Map()
  const results = new Map()
  let terminalProof
  let resultCreationTurn = -1
  const trace = []
  for (const [index, record] of records.entries()) {
    assert.equal(record.request.stream, true, 'VS Code must use streaming')
    for (const message of record.request.messages) {
      for (const call of message.tool_calls || []) {
        assert.ok(issued.has(call.id), 'Tool identity was not preserved across turns')
        assert.equal(call.function.name, issued.get(call.id).function.name)
        assert.deepEqual(JSON.parse(call.function.arguments), JSON.parse(issued.get(call.id).function.arguments))
      }
      if (message.role === 'tool') {
        assert.ok(issued.has(message.tool_call_id), 'Received an unmatched tool result')
        const name = issued.get(message.tool_call_id).function.name
        const text = typeof message.content === 'string' ? message.content : message.content.map(part => part.text || '').join('')
        results.set(message.tool_call_id, { name, text })
        if (name === 'get_terminal_output') {
          const result = /RESULT=(\d+)/.exec(text)
          const nonce = /NONCE=([a-f0-9]{16})/.exec(text)
          if (result && nonce && !terminalProof) terminalProof = { result: Number(result[1]), nonce: nonce[1], turn: index }
        }
      }
    }
    for (const call of record.response.calls) {
      assert.ok(!issued.has(call.id), 'A tool call ID was reused')
      issued.set(call.id, call)
      const args = JSON.parse(call.function.arguments)
      if (call.function.name === 'create_file' && args.filePath?.endsWith('/agent-result.json')) resultCreationTurn = index
    }
    trace.push({ turn: index + 1, status: record.call.status, streaming: record.request.stream,
      reasoningEffort: record.request.reasoning_effort ?? null,
      roles: record.request.messages.map(message => message.role),
      offeredTools: (record.request.tools || []).map(tool => tool.function.name),
      returnedTools: record.response.calls.map(call => call.function.name),
      durationMs: record.call.durationMs, usage: record.call.usage })
  }
  const executed = [...new Set([...results.values()].map(result => result.name))]
  for (const required of ['create_file', 'read_file', 'run_in_terminal', 'get_terminal_output'])
    assert.ok(executed.includes(required), `No VS Code execution result for ${required}`)
  assert.ok(executed.some(name => ['list_dir', 'file_search', 'grep_search', 'semantic_search'].includes(name)), 'Workspace inspection was not executed')
  assert.ok(executed.some(name => ['replace_string_in_file', 'multi_replace_string_in_file', 'apply_patch', 'insert_edit_into_file'].includes(name)), 'A built-in file edit was not executed')
  assert.ok(terminalProof && resultCreationTurn >= terminalProof.turn, 'The model did not create its result after reading successful get_terminal_output')
  assert.ok(records.some(record => record.response.calls.length > 1), 'No parallel tool calls were observed')
  const text = await readFile(join(workspace, 'agent-smoke.txt'), 'utf8')
  assert.equal(text.replaceAll('\r\n', '\n').trim(), 'stage=edited\nvalue=7', 'VS Code did not produce the edited file')
  const result = JSON.parse(await readFile(join(workspace, 'agent-result.json'), 'utf8'))
  assert.equal(result.observedResult, 42)
  assert.equal(terminalProof.result, 42)
  assert.equal(result.observedNonce, terminalProof.nonce, 'The model did not use the actual random terminal nonce')
  assert.ok(typeof result.explanation === 'string' && /7/.test(result.explanation) && /6/.test(result.explanation))
  assert.ok(records.at(-1).response.text.includes(terminalProof.nonce), 'The final answer did not use observed output')
  assert.ok([...issued.keys()].every(id => results.has(id)), 'Not every tool call completed its result round trip')
  return { passed: true, recordedAt: new Date().toISOString(), backend: 'real configured provider; no fixture',
    executor: 'VS Code built-in Agent tools', result: result.observedResult, nonce: result.observedNonce,
    explanation: result.explanation, executedTools: executed, modelTurns: records.length,
    parallelToolTurns: records.filter(record => record.response.calls.length > 1).length, trace }
}

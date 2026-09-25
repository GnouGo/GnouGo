// Real installed VS Code, with a private copy of an existing profile's model
// configuration. Never closes or restarts the user's existing VS Code process.
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdir, mkdtemp, open, readFile, realpath, writeFile } from 'node:fs/promises'
import { createServer } from 'node:net'
import { join } from 'node:path'
import { setTimeout as delay } from 'node:timers/promises'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE_PATH || 'playwright')
const source = process.env.PROXY_REASONING_PROFILE
assert.ok(source, 'Set PROXY_REASONING_PROFILE to the profile directory containing chatLanguageModels.json and settings.json')
const profileSettings = JSON.parse(await readFile(join(source, 'settings.json'), 'utf8'))
assert.equal(profileSettings['chat.experimentalModelPicker'], false)
const groups = JSON.parse(await readFile(join(source, 'chatLanguageModels.json'), 'utf8'))
const selectedIds = process.env.PROXY_REASONING_MODEL_IDS?.split(',')
const models = groups.filter(g => g.vendor === 'customendpoint').flatMap(g => g.models)
  .filter(m => selectedIds ? selectedIds.includes(m.id) : m.supportsReasoningEffort?.includes('xhigh'))
assert.equal(models.length, 1, 'Select one model per run with PROXY_REASONING_MODEL_IDS')
const base = process.env.PROXY_SMOKE_URL || 'http://127.0.0.1:5087'
const root = await realpath(await mkdtemp('/tmp/gnougo-reasoning-smoke-'))
const workspace = join(root, 'workspace'), userData = join(root, 'user-data')
await mkdir(workspace, { recursive: true })
await writeFile(join(workspace, 'calculation.py'), 'result = 19 * 23\n')
await mkdir(join(userData, 'User'), { recursive: true })
const save = (path, value) => writeFile(path, JSON.stringify(value, null, 2) + '\n', { mode: 0o600 })
await save(join(userData, 'User', 'chatLanguageModels.json'), groups.filter(g => g.vendor === 'customendpoint')
  .map(({ apiKey, ...g }) => ({ ...g, models: g.models.filter(m => models.includes(m)) })).filter(g => g.models.length))
await save(join(userData, 'User', 'settings.json'), {
  'chat.experimentalModelPicker': profileSettings['chat.experimentalModelPicker'],
  'telemetry.telemetryLevel': 'off', 'update.mode': 'none', 'extensions.autoUpdate': false,
  'extensions.ignoreRecommendations': true,
  'extensions.autoCheckUpdates': false, 'workbench.startupEditor': 'none',
  'security.workspace.trust.enabled': false, 'chat.disableAIFeatures': false,
  'editor.accessibilitySupport': 'on', 'editor.editContext': false,
})
const listener = createServer()
await new Promise(resolve => listener.listen(0, '127.0.0.1', resolve))
const port = listener.address().port
await new Promise(resolve => listener.close(resolve))
const log = await open(join(root, 'vscode.log'), 'w', 0o600)
const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => key !== 'ELECTRON_RUN_AS_NODE' && !key.startsWith('VSCODE_')))
const code = spawn(process.env.VSCODE_EXECUTABLE || '/Applications/Visual Studio Code.app/Contents/MacOS/Code', [
  '--user-data-dir', userData, '--extensions-dir', join(root, 'extensions'),
  `--remote-debugging-port=${port}`, '--remote-debugging-address=127.0.0.1',
  '--skip-welcome', '--skip-release-notes', '--disable-workspace-trust', '--new-window', workspace, join(workspace, 'calculation.py'),
], { env, stdio: ['ignore', log.fd, log.fd] })
console.log(`Reasoning smoke evidence: ${root}; CDP port: ${port}`)
let browser, page
const evidence = []
const json = async path => { const r = await fetch(base + path); assert.equal(r.status, 200); return r.json() }
try {
  const deadline = Date.now() + 120000
  while (!browser && Date.now() < deadline) {
    try { browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`) } catch { await delay(500) }
  }
  assert.ok(browser, 'VS Code debugging endpoint did not start')
  while (!page && Date.now() < deadline) {
    page = browser.contexts().flatMap(c => c.pages()).find(p => p.url().includes('workbench'))
    if (!page) await delay(500)
  }
  assert.ok(page); page.setDefaultTimeout(30000)
  const picker = page.getByRole('button', { name: /^Models,/ }).filter({ visible: true })
  if (!await picker.count()) await page.keyboard.press('Control+Meta+i')
  await picker.waitFor()
  await page.bringToFront()
  await page.getByRole('button', { name: 'Maximize Secondary Side Bar', exact: true }).click()
  for (const [index, model] of models.entries()) {
    if (index) await page.getByRole('button', { name: /^New Chat \(/ }).click()
    await picker.click()
    const entry = page.getByRole('option').filter({ hasText: model.name }).filter({ visible: true })
    const other = page.getByRole('option', { name: 'Other Models', exact: true })
    if (!await entry.count() && await other.count()) await other.click()
    if (!await entry.count()) {
      await page.getByText(/Manage (Language )?Models/).filter({ visible: true }).first().click()
      await page.getByText(model.name, { exact: true }).filter({ visible: true }).first().waitFor()
      await page.keyboard.press('Escape'); await picker.click()
      if (!await entry.count() && await other.count()) await other.click()
    }
    await entry.first().click()
    if (!await page.getByRole('button', { name: 'Ask', exact: true }).count()) {
      await page.getByRole('button', { name: 'Agent', exact: true }).click()
      await page.getByRole('menuitemcheckbox', { name: /^Ask,/ }).click({ force: true })
      await page.keyboard.press('Escape')
    }
    await page.getByRole('button', { name: 'Configure Tools...', exact: true }).click()
    // Ask also offers read-only tools in current VS Code. Explicitly disable
    // every tool for this test session; tools: [] in a custom agent is insufficient.
    const allTools = page.getByRole('checkbox', { name: 'Toggle all checkboxes', exact: true })
    await allTools.check(); await allTools.uncheck()
    await page.getByRole('button', { name: 'OK', exact: true }).click()
    const effort = page.getByRole('button', { name: /^Thinking Effort:/ })
    assert.equal(await effort.getAttribute('aria-label'), 'Thinking Effort: None', 'A fresh session must use the configured default')
    await effort.click()
    const menu = await page.locator('body').ariaSnapshot()
    for (const label of ['None', 'Low', 'Medium', 'High', 'Extra High']) assert.ok(menu.includes(label), `Missing ${label}`)
    await page.screenshot({ path: join(root, `model-${index + 1}-thinking-effort.png`), fullPage: true })
    await page.getByRole('menuitemradio', { name: /^High,/ }).click()
    await page.keyboard.press('Escape')
    await page.getByRole('menu', { name: 'Action Widget', exact: true }).waitFor({ state: 'hidden' })
    await page.getByRole('button', { name: 'Thinking Effort: High', exact: true }).waitFor()
    const baseline = new Set((await json('/api/traffic')).calls.map(c => c.id))
    // The built-in Explain route is text-only. Ordinary Ask currently injects
    // session_store_sql even with no tools selected, which requires None here.
    const prompt = '/explain What is the result of the Python expression 19 * 23? Answer with the number and one short verification.'
    const input = page.getByRole('textbox', { name: /^Chat Input/ }).filter({ visible: true })
    await page.bringToFront()
    await input.focus(); await input.pressSequentially(prompt, { delay: 2 })
    await page.getByRole('button', { name: /^Send \[/ }).click()
    let detail
    const end = Date.now() + 180000
    while (Date.now() < end) {
      const call = (await json('/api/traffic')).calls.find(c => !baseline.has(c.id) && c.model === model.id)
      if (call && call.status !== 'running') { detail = await json(`/api/traffic/${call.id}`); break }
      await delay(500)
    }
    assert.ok(detail, 'No completed model request')
    assert.equal(detail.summary.status, 'completed')
    const request = JSON.parse(detail.bodies.clientRequest.text)
    assert.equal(request.reasoning_effort, 'high')
    assert.equal(request.tools?.length || 0, 0, 'Plain chat must not send function tools')
    const answer = detail.bodies.clientResponse.text.split('\n').filter(line => line.startsWith('data: {'))
      .flatMap(line => JSON.parse(line.slice(6)).choices || []).map(choice => choice.delta?.content || '').join('')
    assert.match(answer, /437/)
    assert.equal(detail.summary.cost.status, 'estimated')
    evidence.push({ model: index + 1, route: 'VS Code Ask /explain', levels: model.supportsReasoningEffort, selected: request.reasoning_effort, toolCount: 0, status: detail.summary.status, cost: detail.summary.cost })
    // Restore the safe Agent default in this test session as well.
    await effort.click(); await page.getByRole('menuitemradio', { name: /^None,/ }).click()
    await page.keyboard.press('Escape')
    console.log(`PASS: model ${index + 1} shows all levels; High reaches the real provider without tools and returns 437; estimated cost recorded.`)
  }
  await save(join(root, 'evidence.json'), evidence)
} catch (error) {
  if (page) await page.screenshot({ path: join(root, 'failure.png'), fullPage: true }).catch(() => {})
  throw error
} finally {
  if (browser) await browser.close()
  code.kill('SIGTERM')
  await log.close()
}

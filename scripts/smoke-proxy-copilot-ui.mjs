// Run against the synthetic environment started by smoke-proxy-copilot.py --serve.
// PLAYWRIGHT_MODULE_PATH may point to an external Playwright installation.
import assert from 'node:assert/strict'
import { mkdir } from 'node:fs/promises'
import { resolve } from 'node:path'

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE_PATH || 'playwright')
const base = process.env.PROXY_SMOKE_URL || 'http://127.0.0.1:5087'
const screenshotDirectory = resolve(process.env.PROXY_SCREENSHOTS || 'artifacts/proxy-copilot/screenshots')
await mkdir(screenshotDirectory, { recursive: true })
const browser = await chromium.launch({ headless: true })
const page = await browser.newPage({ viewport: { width: 1440, height: 1080 }, deviceScaleFactor: 1 })
const errors = []
page.on('pageerror', error => errors.push(error.message))

try {
  await page.goto(base + '/ui/')
  await page.getByRole('heading', { name: 'Copilot Proxy' }).waitFor()
  await page.waitForFunction(() => document.querySelectorAll('.call').length >= 8)
  await page.getByRole('combobox', { name: 'Filter provider' }).selectOption('anthropic')
  await page.waitForFunction(() => document.querySelectorAll('.call').length === 2)
  await page.locator('.call').first().click()
  await page.getByRole('button', { name: 'Raw payloads', exact: true }).click()
  await page.getByText('Proxy → Provider', { exact: true }).waitFor()
  assert.match(await page.locator('.detail-content').innerText(), /tool_result/)
  await page.screenshot({ path: resolve(screenshotDirectory, 'native-payloads.png'), fullPage: true })
  await page.getByRole('combobox', { name: 'Filter provider' }).selectOption('')
  await page.getByRole('button', { name: 'VS Code setup', exact: true }).click()
  await page.waitForFunction(() => document.querySelector('.setup pre')?.textContent.includes('customendpoint'))
  assert.doesNotMatch(await page.locator('.setup pre').innerText(), /synthetic-/)
  await page.getByRole('button', { name: 'Conversation', exact: true }).click()

  const rejected = await fetch(base + '/v1/chat/completions', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ model: 'anthropic/code', messages: [{ role: 'user', content: 'Unsupported option example' }], reasoning_effort: 'high' }) })
  assert.equal(rejected.status, 400)
  const streamed = fetch(base + '/v1/chat/completions', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ model: 'axa/code', stream: true, stream_options: { include_usage: true }, messages: [{ role: 'user', content: 'Watch the stream and explain how a response moves through the proxy.' }] }) }).then(async response => {
    assert.equal(response.status, 200)
    const body = await response.text()
    assert.match(body, /\[DONE\]/)
    return body
  })
  await page.waitForFunction(() => document.querySelector('.call')?.textContent.includes('running'))
  await page.locator('.call').first().click()
  await page.getByText('streaming', { exact: true }).waitFor()
  await page.waitForFunction(() => document.querySelector('.message--response pre')?.textContent.includes('response arrives'))
  await page.screenshot({ path: resolve(screenshotDirectory, 'live-traffic.png'), fullPage: true })
  await streamed
  await page.waitForFunction(() => document.querySelector('.detail-heading .badge')?.textContent === 'completed')
  assert.match(await page.locator('.message--response').innerText(), /background/)
  await page.getByRole('button', { name: 'Pause live view' }).click()
  await page.getByText('View paused', { exact: true }).waitFor()
  await page.getByRole('button', { name: 'Resume live view' }).click()
  await page.getByText('Live connection', { exact: true }).waitFor()
  await page.reload()
  await page.waitForFunction(() => document.querySelectorAll('.call').length === 10)
  await page.setViewportSize({ width: 430, height: 900 })
  await page.screenshot({ path: resolve(screenshotDirectory, 'mobile.png'), fullPage: true })
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true)
  await page.getByRole('button', { name: 'Clear', exact: true }).click()
  await page.waitForFunction(() => document.querySelectorAll('.call').length === 0)
  assert.deepEqual(errors, [])
  console.log('PASS: live incremental output, native payloads, filters, setup, pause/resume, reconnect, mobile, clear; no browser errors.')
} finally {
  await browser.close()
}

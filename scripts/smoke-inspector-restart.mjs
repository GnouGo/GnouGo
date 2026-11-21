// Actual Agent.Server restarted with its original journals and an empty controlled telemetry store.
import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
const { chromium, request } = await import(process.env.PLAYWRIGHT_MODULE_PATH);
const [url, token, session, output] = process.argv.slice(2);
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1080 } });
  await page.goto(`${url}/local-operator#${token}`);
  await page.waitForURL(`${url}/planning`);
  await page.goto(`${url}/planning/${session}`);
  await page.locator('#planningReview[data-interactive="true"]').waitFor();
  await page.getByRole('button', { name: 'Inspect LLM exchange', exact: true }).first().click();
  const panel = page.locator('.gnougo-trace-panel--open');
  const rows = panel.locator('.llm-call');
  await rows.nth(4).waitFor();
  assert.equal(await rows.count(), 5);
  assert.equal(await panel.getByText(/Encrypted journal — telemetry absent/).count(), 5);
  assert.ok(!(await panel.innerText()).includes('Observed span:'));
  await panel.getByRole('heading', { name: /Actual serialized HTTP request/ }).waitFor();
  assert.ok(!(await panel.innerText()).includes('CONTROLLED_PROVIDER_KEY'));
  const inspection = await (await page.request.get(`${url}/api/planning/${session}/inspection`)).json();
  const delta = await (await page.request.get(`${url}/api/planning/${session}/inspection?after=${inspection.nextEvent}&callsAfter=${encodeURIComponent(JSON.stringify(inspection.nextCallCursor))}`)).json();
  assert.equal(delta.calls.length, 0);
  const key = inspection.calls[0].key;
  const exported = await (await page.request.get(`${url}/api/planning/${session}/calls/export?key=${encodeURIComponent(key)}`)).json();
  const original = JSON.parse(await readFile(`${output}/call-redacted.json`, 'utf8'));
  assert.deepEqual(exported.metadata.costBreakdown, original.metadata.costBreakdown);
  assert.deepEqual(exported.metadata.accounting, original.metadata.accounting);
  assert.equal(exported.metadata.transport.Attempts.length, 2);
  await page.screenshot({ path: `${output}/journal-fallback-after-restart.png` });
  const anonymous = await request.newContext();
  assert.equal((await anonymous.get(`${url}/api/planning/${session}/calls/content?key=${encodeURIComponent(key)}`)).status(), 403);
  assert.equal((await anonymous.get(`${url}/api/planning/${session}/calls/export?key=${encodeURIComponent(key)}`)).status(), 403);
  await anonymous.dispose();
  await writeFile(`${output}/restart-result.json`, JSON.stringify({ provider: 'controlled HTTP evidence; no new inference', session, calls: 5, spans: 0, accountingUnchanged: true, deltaCalls: delta.calls.length }, null, 2));
} finally { await browser.close(); }

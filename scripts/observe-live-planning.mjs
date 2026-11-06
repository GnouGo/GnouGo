// Read-only observation of an actual live Agent.Server session. Bootstrap arrives on stdin, never argv/logs.
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
let input = ''; for await (const chunk of process.stdin) input += chunk;
const { launch, url, session, output } = JSON.parse(input);
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE_PATH);
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 1080 } });
const errors = []; page.on('pageerror', e => errors.push(e.message));
try {
  await page.goto(launch); await page.waitForURL('**/planning');
  await page.goto(`${url}/planning/${session}`);
  await page.locator('#planningReview[data-interactive="true"]').waitFor();
  let active = false;
  let state;
  for (;;) {
    state = await (await page.request.get(`${url}/api/planning/${session}`)).json();
    if (!active && await page.locator('.phase-active').count()) {
      await page.screenshot({ path: `${output}/live-active-phase.png`, fullPage: true }); active = true;
    }
    if (['final_review', 'clarification', 'stopped', 'failed', 'cancelled'].includes(state.status)) break;
    await page.waitForTimeout(2000);
  }
  await page.waitForTimeout(2500);
  await page.screenshot({ path: `${output}/live-final-state.png`, fullPage: true });
  const exchanges = page.getByRole('button', { name: 'Inspect LLM exchange', exact: true });
  const inspected = await exchanges.count() > 0;
  let identities = [];
  if (inspected) {
    await exchanges.last().click();
    const panel = page.locator('.gnougo-trace-panel--open');
    await panel.getByRole('heading', { name: 'Planning traces', exact: true }).waitFor();
    await panel.locator('.llm-call').first().waitFor();
    identities = await panel.locator('.llm-call').evaluateAll(rows => rows.map(r => r.dataset.requestId));
    assert.equal(new Set(identities).size, identities.length, 'One logical call per exact request identity');
    const expand = panel.locator('.llm-call').last().locator('.llm-call__expand');
    if ((await expand.innerText()).startsWith('Inspect')) await expand.click();
    await panel.getByRole('heading', { name: /Logical request and response schema/ }).waitFor();
    await page.screenshot({ path: `${output}/live-common-inspector.png`, fullPage: true });
  }
  assert.deepEqual(errors, []);
  await writeFile(`${output}/live-browser-result.json`, JSON.stringify({ evidence: 'live-provider', session, status: state.status,
    activePhaseObserved: active, sharedInspectorOpened: inspected, logicalRequestIds: identities, duplicateCalls: false, errors }, null, 2));
} finally { await browser.close(); }

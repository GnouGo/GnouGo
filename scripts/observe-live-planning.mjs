// Observe planning and submit typed inputs to a saved workflow on the local Agent.Server. Bootstrap arrives on stdin, never argv/logs.
import assert from 'node:assert/strict';
import { mkdir, rename, writeFile } from 'node:fs/promises';
import { isAbsolute } from 'node:path';
import { pathToFileURL } from 'node:url';
let input = ''; for await (const chunk of process.stdin) input += chunk;
const { launch, url, session, output, mode, agent, inputs, awaitPlanningClarification } = JSON.parse(input);
const modulePath = process.env.PLAYWRIGHT_MODULE_PATH;
const { chromium } = await import(isAbsolute(modulePath) ? pathToFileURL(modulePath).href : modulePath);
await mkdir(output, { recursive: true });
let browser;
let stage = 'browser startup';
const timedOut = error => error?.name === 'TimeoutError' || /^(?:apiRequestContext\.get: )?Timeout \d+ms exceeded\./.test(error?.message ?? '');
try {
  browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1080 } });
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error' && /unhandled exception/i.test(m.text())) errors.push(m.text()); });
  stage = 'operator sign-in';
  await page.goto(launch); await page.waitForURL('**/planning');
  stage = 'frontend readiness';
  for (const [asset, contentType] of [['/ui/app.js', /javascript/], ['/ui/app.css', /css/]]) {
    const response = await page.request.get(new URL(asset, url).href);
    assert.equal(response.status(), 200, `Required frontend asset unavailable: ${asset}; build Agent.Server/ClientApp`);
    assert.match(response.headers()['content-type'] ?? '', contentType);
  }
  await page.waitForFunction(() => document.documentElement.dataset.gnougoReady === 'true', { timeout: 15000 });
  assert.equal(await page.evaluate(() => typeof window.GnOuGo?.Agent?.markdown?.enhance), 'function');
  await page.locator('#planningReview[data-interactive="true"]').waitFor({ timeout: 15000 });
  assert.equal(await page.locator('#app-startup-error:visible').count(), 0);
  if (mode === 'readiness') {
    assert.deepEqual(errors, []);
    await writeFile(`${output}/frontend-readiness.json`, JSON.stringify({ evidence: 'actual Agent.Server; browser readiness only; no inference',
      assetsDelivered: true, helperRegistered: true, interactiveCircuit: true, errors }, null, 2));
  } else if (mode === 'execute') {
    stage = 'saved workflow form';
    await page.goto(url);
    await page.getByTitle('Select a GnOuGo agent', { exact: true }).click();
    await page.getByRole('menuitemradio', { name: agent, exact: true }).click();
    await page.waitForFunction(name => document.querySelector('form.gnougo-composer')?.dataset.workflowAgent === name, agent);
    const composer = page.locator('form.gnougo-composer');
    const form = composer.getByLabel('Workflow inputs', { exact: true });
    const structured = await form.isVisible();
    if (structured) {
      assert.equal(await form.locator('[id^="workflowInput_"]').count(), Object.keys(inputs).length);
      for (const [name, value] of Object.entries(inputs)) {
        await form.locator('[id="workflowInput_' + name + '"]').fill(value);
      }
    } else {
      const values = Object.values(inputs);
      assert.equal(values.length, 1, 'The message composer accepts one prompt value');
      assert.equal(typeof values[0], 'string');
      await composer.locator('textarea.gnougo-composer__input').fill(values[0]);
    }
    const submit = composer.getByRole('button', { name: structured ? 'Run workflow' : 'Send', exact: true });
    const previous = await page.getByLabel('Your message', { exact: true }).count();
    stage = 'workflow submission';
    await submit.click();
    const message = page.getByLabel('Your message', { exact: true }).nth(previous);
    await message.waitFor();
    const run = await message.getAttribute('data-correlation-id');
    assert.match(run ?? '', /^[0-9a-f]{32}$/);
    await writeFile(output + '/form-invocation.tmp', JSON.stringify({ agent, run }), { flag: 'wx' });
    await rename(output + '/form-invocation.tmp', output + '/form-invocation.json');
    stage = 'workflow response';
    // Keep the Blazor circuit alive through all runtime confirmations.
    await submit.waitFor({ timeout: 0 });
    assert.deepEqual(errors, []);
    const response = page.locator('article[aria-label="GnOuGo.Agent response"][data-correlation-id="' + run + '"]');
    const answer = response.locator('.gnougo-chat__bubble > .gnougo-chat__bubble-text');
    const text = await answer.isVisible() ? await answer.innerText() : null;
    await writeFile(output + '/form-response.json', JSON.stringify({ agent, run, text }), { flag: 'wx' });
  } else {
  stage = 'planning page';
  await page.goto(`${url}/planning/${session}`);
  await page.locator('#planningReview[data-interactive="true"]').waitFor();
  let active = false;
  let state;
  for (;;) {
    stage = 'planning status';
    try {
      state = await (await page.request.get(`${url}/api/planning/${session}`)).json();
    } catch (error) {
      if (!timedOut(error)) throw error;
      console.warn('Warning: the local planning status read timed out. Retrying the read.');
      await page.waitForTimeout(2000);
      continue;
    }
    if (!active && await page.locator('.phase-active').count()) {
      await page.screenshot({ path: `${output}/live-active-phase.png`, fullPage: true }); active = true;
    }
    const awaitingAnswer = awaitPlanningClarification === true && state.status === 'clarification' && state.questions?.length > 0;
    if (!awaitingAnswer && ['final_review', 'clarification', 'stopped', 'failed', 'cancelled'].includes(state.status)) break;
    await page.waitForTimeout(2000);
  }
  stage = 'planning inspection';
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
  }
} catch (error) {
  // Playwright transport errors can contain the authenticated request headers and full launch URL.
  const reason = timedOut(error) ? 'timeout' : error?.code === 'ERR_ASSERTION' ? 'assertion failed' : 'browser operation failed';
  console.error(`Observer stopped during ${stage} (${reason}).`);
  process.exitCode = 1;
} finally {
  try { await browser?.close(); }
  catch {
    console.error('Observer could not close the browser.');
    process.exitCode = 1;
  }
}

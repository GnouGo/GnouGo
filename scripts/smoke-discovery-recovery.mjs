import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
const { chromium, request } = await import(process.env.PLAYWRIGHT_MODULE_PATH);
let input = ''; for await (const chunk of process.stdin) input += chunk;
const { url, launch, session, otherSession, output, resumeReleasePath } = JSON.parse(input);
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 1080 } });
const page = await context.newPage(); const errors = [];
page.on('pageerror', error => errors.push(error.message));
try {
  const anonymous = await request.newContext();
  const anti = await (await anonymous.get(`${url}/api/planning/antiforgery`)).json();
  const before = await (await anonymous.get(`${url}/api/planning/${session}`)).json();
  const failure = before.discoveryFailures[0].id;
  const recovery = { kind: 'recover_discovery', expectedRevision: before.revision,
    discoveryRecovery: { failureId: failure, resolution: 'continue_available' } };
  assert.equal((await anonymous.post(`${url}/api/planning/${session}/commands`, {
    headers: { [anti.headerName]: anti.requestToken }, data: recovery })).status(), 403);
  assert.equal((await anonymous.get(`${url}/api/planning/${session}/inspection`)).status(), 403);
  await anonymous.dispose();
  await page.goto(launch); await page.waitForURL(`${url}/planning`);
  await page.goto(`${url}/planning/${session}`);
  await page.locator('#planningReview[data-interactive="true"]').waitFor();
  assert.equal((await page.request.post(`${url}/api/planning/${session}/commands`, { data: recovery })).status(), 400);
  const csrf = await (await page.request.get(`${url}/api/planning/antiforgery`)).json();
  const headers = { [csrf.headerName]: csrf.requestToken };
  assert.equal((await page.request.post(`${url}/api/planning/${session}/commands`, {
    headers, data: { ...recovery, expectedRevision: before.revision + 100 } })).status(), 409);
  let other = await (await page.request.get(`${url}/api/planning/${otherSession}`)).json();
  assert.ok(other.diagnostics.some(d => d.code === 'DISCOVERY_REQUIRED_CONTRACT_CHANGED'));
  assert.equal(other.discoveryFailures[0].category, 'catalog_changed');
  assert.equal((await page.request.post(`${url}/api/planning/${otherSession}/commands`, {
    headers, data: { ...recovery, expectedRevision: other.revision } })).status(), 409);
  await page.getByRole('button', { name: 'Refresh source metadata', exact: true }).click();
  await page.getByRole('button', { name: 'Resume planning after discovery recovery', exact: true }).waitFor();
  let recovered = await (await page.request.get(`${url}/api/planning/${session}`)).json();
  assert.equal(recovered.status, 'discovery_recovery'); assert.equal(recovered.calls, before.calls);
  assert.equal(recovered.thresholdMode, 'diagnostic'); assert.deepEqual(recovered.effectivePolicy.budget, before.effectivePolicy.budget);
  assert.deepEqual(recovered.taskPlan, before.taskPlan);
  assert.ok(recovered.events.some(e => e.state === 'warning' && e.discovery?.outcome === 'transport_unavailable'));
  assert.ok(!(await page.locator('body').innerText()).includes('PRIVATE_MCP_CREDENTIAL'));
  await page.screenshot({ path: `${output}/metadata-warning-recovery.png`, fullPage: true });
  await page.reload();
  await page.locator('#planningReview[data-interactive="true"]').waitFor();
  await page.getByRole('button', { name: 'Resume planning after discovery recovery', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Traces', exact: true }).click();
  await page.getByRole('heading', { name: 'Planning traces', exact: true }).waitFor();
  assert.equal(await page.locator('.llm-call').count(), 0, 'Metadata-only recovery must not invent LLM exchanges');
  await page.screenshot({ path: `${output}/shared-inspector-no-invented-calls.png`, fullPage: true });
  await page.locator('.gnougo-trace-panel--open').locator('.gnougo-trace-panel__close').click();
  await page.getByRole('button', { name: 'Resume planning after discovery recovery', exact: true }).click();
  await page.waitForFunction(async ({ url, session }) =>
    (await (await fetch(`${url}/api/planning/${session}`)).json()).status === 'generating', { url, session });
  // A persisted intermediate checkpoint does not mean the UI command returned.
  // Release the fixture's owner, then observe completed UI recovery before taking
  // the revision for a separate command. No stale command is retried.
  assert.equal((await page.request.post(`${url}${resumeReleasePath}`)).status(), 204);
  await page.getByRole('button', { name: 'Resume planning after discovery recovery', exact: true }).waitFor({ state: 'hidden' });
  recovered = await (await page.request.get(`${url}/api/planning/${session}`)).json();
  const continued = await page.request.post(`${url}/api/planning/${session}/commands`, { headers,
    data: { kind: 'advance', expectedRevision: recovered.revision } });
  assert.equal(continued.status(), 200, await continued.text());
  const stopped = await continued.json();
  assert.equal(stopped.status, 'stopped');
  assert.ok(stopped.diagnostics.some(d => ['LLM_BUDGET_EXCEEDED', 'CONSTRUCTION_REVIEW_ALLOWANCE'].includes(d.code)), JSON.stringify(stopped.diagnostics.map(d => ({ code: d.code, message: d.message }))));
  assert.equal(stopped.calls, before.calls); assert.equal(errors.length, 0, errors.join('\n'));
  await page.goto(`${url}/planning/${otherSession}`);
  await page.locator('#planningReview[data-interactive="true"]').waitFor();
  await page.getByRole('button', { name: 'Continue with available sources', exact: true }).click();
  await page.getByRole('button', { name: 'Resume planning after discovery recovery', exact: true }).waitFor();
  other = await (await page.request.get(`${url}/api/planning/${otherSession}`)).json();
  assert.equal(other.status, 'discovery_recovery'); assert.equal(other.calls, before.calls);
  await page.reload(); await page.locator('#planningReview[data-interactive="true"]').waitFor();
  await page.getByRole('button', { name: 'Resume planning after discovery recovery', exact: true }).waitFor();
  await page.screenshot({ path: `${output}/required-contract-recovery.png`, fullPage: true });
  const cancelled = await (await page.request.post(`${url}/api/planning/${otherSession}/commands`, { headers,
    data: { kind: 'cancel', expectedRevision: other.revision } })).json();
  assert.equal(cancelled.status, 'cancelled');
  assert.equal((await page.request.post(`${url}/api/planning/${otherSession}/commands`, { headers,
    data: { kind: 'recover_discovery', expectedRevision: cancelled.revision,
      discoveryRecovery: { failureId: other.discoveryFailures[0].id, resolution: 'continue_available' } } })).status(), 409);
  await writeFile(`${output}/result.json`, JSON.stringify({ evidence: 'controlled-provider', session,
    protectedRecovery: true, crossSessionRejected: true, metadataWarning: true, reload: true,
    sharedInspector: true, requiredContractRecovery: true, cancellation: true, modelCallsDuringRecovery: 0, finiteAdmissionPreserved: true, errors }, null, 2));
} catch (error) {
  await page.screenshot({ path: `${output}/failure.png`, fullPage: true });
  await writeFile(`${output}/failure.json`, JSON.stringify({ errors, message: error.message,
    headings: await page.getByRole('heading').allTextContents(),
    panels: await page.locator('.gnougo-trace-panel').count() }, null, 2));
  throw error;
} finally { await browser.close(); }

// Actual Server startup regression. Operator bootstrap is received through stdin.
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
let input = ''; for await (const chunk of process.stdin) input += chunk;
const { launch, url, session, scenario, output } = JSON.parse(input);
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE_PATH);
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
const errors = [];
page.on('pageerror', error => errors.push(error.message));
page.on('console', message => {
  if (message.type() === 'error' && /unhandled exception|GnOuGo.*undefined|Could not find/i.test(message.text())) errors.push(message.text());
});
try {
  await page.goto(launch); await page.waitForURL('**/planning');
  if (scenario === 'delayed') {
    await page.route('**/ui/app.js*', route => route.continue());
    await page.goto(`${url}/planning/${session}`, { waitUntil: 'commit' });
    await page.locator('script[data-app-module]').waitFor({ state: 'attached' });
    assert.equal(await page.locator('#planningReview[data-interactive="true"]').count(), 0, 'Circuit must wait for the delayed UI module');
  } else await page.goto(`${url}/planning/${session}`);
  const failed = scenario.startsWith('missing_');
  if (failed) {
    await page.locator('#app-startup-error:visible').waitFor({ timeout: 10000 });
    assert.equal(await page.locator('#planningReview[data-interactive="true"]').count(), 0);
  } else {
    await page.locator('#planningReview[data-interactive="true"]').waitFor({ timeout: 15000 });
    assert.equal(await page.evaluate(() => typeof window.GnOuGo?.Agent?.markdown?.enhance), 'function');
    await page.reload();
    await page.locator('#planningReview[data-interactive="true"]').waitFor({ timeout: 15000 });
  }
  assert.deepEqual(errors, []);
  await page.screenshot({ path: `${output}/startup.png`, fullPage: true });
  await writeFile(`${output}/result.json`, JSON.stringify({ evidence: 'controlled assets; actual Agent.Server; no inference', scenario,
    startupFailureVisible: failed, interactive: !failed, reload: !failed, errors }, null, 2));
} finally { await browser.close(); }

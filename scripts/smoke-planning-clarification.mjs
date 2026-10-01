// Invoked by PlanningClarificationBrowserTests against a disposable host and deterministic model.
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE_PATH);
const [url, encodedCases] = process.argv.slice(2);
const cases = JSON.parse(encodedCases);
const output = process.env.GNOUGO_CLARIFICATION_BROWSER_OUTPUT || 'artifacts/planning-clarification/browser';
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 1080 } });
const errors = [];
page.on('pageerror', e => errors.push(e.message));
page.on('console', e => { if (e.type() === 'error') errors.push(e.text()); });
page.on('response', async response => { if (response.status() >= 500) console.error(response.url(), await response.text()); });
try {
  for (const [name, id] of Object.entries(cases)) {
    await page.setViewportSize(name === 'text' ? { width: 390, height: 844 } : { width: 1440, height: 1080 });
    await page.goto(`${url}/planning/${id}`);
    const confirm = page.getByRole('button', { name: 'Confirm selections', exact: true });
    await confirm.waitFor();
    // Reload a pending question; no answer or model continuation has been submitted.
    await page.reload(); await confirm.waitFor();
    await page.waitForFunction(() => typeof window.Blazor !== 'undefined');
    // SSR can precede the interactive circuit. Wait for its connection before input.
    await page.waitForFunction(() => window.Blazor._internal?.navigationManager != null);
    await page.waitForTimeout(300);
    if (name === 'cancel') await page.locator('form').filter({ has: confirm }).getByRole('button', { name: 'Cancel planning', exact: true }).click();
    else {
      if (name === 'custom') await page.locator('.planner-question select').selectOption('');
      if (name === 'custom' || name === 'text') {
        const input = page.locator('.planner-question textarea');
        await input.fill(name === 'text' ? 'Use the existing resource' : 'Only a reference and checklist; derive the rest');
        await input.press('Tab');
        assert.equal(await page.evaluate(() => document.activeElement?.textContent?.trim()), 'Confirm selections');
      } else assert.match(await page.locator('.planner-question select option:checked').innerText(), /Recommended/);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'No horizontal overflow');
      await page.screenshot({ path: `${output}/${name}.png`, fullPage: true });
      await confirm.focus(); await page.keyboard.press('Enter');
    }
    await page.getByText(name === 'cancel' ? 'Cancelled' : 'Ready for your review', { exact: true }).waitFor({ timeout: 30000 });
    if (name !== 'cancel') {
      const support = page.locator('details').filter({ has: page.locator('summary', { hasText: 'Outcome implementation' }) });
      await support.waitFor();
      assert.match(await support.innerText(), /success has not been observed/);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'Outcome review fits mobile');
      await support.locator('summary').focus(); await page.keyboard.press('Enter');
      assert.equal(await support.getAttribute('open'), null);
      await page.keyboard.press('Enter');
      await page.reload(); await support.waitFor();
      await page.getByText('Ready for your review', { exact: true }).waitFor();
      await page.screenshot({ path: `${output}/${name}-outcomes.png`, fullPage: true });
    }
  }
  assert.deepEqual(errors, []);
  await writeFile(`${output}/results.json`, JSON.stringify({ recommendation: true, custom: true, textOnly: true, reload: true, keyboard: true, mobile: true, cancellation: true, outcomeSupport: true, separateApproval: true, errors }, null, 2));
} catch (error) {
  await page.screenshot({ path: `${output}/failure.png`, fullPage: true });
  console.error((await page.locator('body').innerText()).slice(-6000)); console.error(errors); throw error;
} finally { await browser.close(); }

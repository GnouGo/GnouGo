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
    await page.setViewportSize(['text', 'legacy'].includes(name) ? { width: 390, height: 844 } : { width: 1440, height: 1080 });
    await page.goto(`${url}/planning/${id}`);
    if (name === 'legacy') {
      const revision = page.getByRole('button', { name: 'Revise workflow', exact: true });
      await revision.waitFor(); await page.reload(); await revision.waitFor();
      await page.waitForFunction(() => window.Blazor?._internal?.navigationManager != null);
      await page.waitForTimeout(300);
      assert.match(await page.locator('body').innerText(), /retired planning contract/);
      assert.equal(await page.getByRole('button', { name: 'Approve this revision and save', exact: true }).count(), 0);
      assert.equal(await page.getByRole('button', { name: 'Retry with retained usage', exact: true }).count(), 0);
      assert.equal(await revision.isEnabled(), false);
      await page.locator('#plan-revision').fill('Retain the business requirements');
      await page.locator('#plan-revision').press('Tab');
      await page.waitForFunction(() => [...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Revise workflow' && !b.disabled));
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
      await page.screenshot({ path: `${output}/${name}.png`, fullPage: true });
      continue; // No revision or model continuation is submitted implicitly.
    }
    if (name === 'recovery') {
      const resume = page.getByRole('button', { name: 'Resume from saved response', exact: true });
      await resume.waitFor(); await page.reload(); await resume.waitFor();
      await page.waitForFunction(() => window.Blazor?._internal?.navigationManager != null);
      await page.waitForTimeout(300);
      assert.equal(await page.getByRole('button', { name: 'Retry with retained usage', exact: true }).count(), 0);
      await resume.focus(); await page.keyboard.press('Enter');
    }
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
        await input.fill(['text', 'legacy'].includes(name) ? 'Use the existing resource' : 'Only a reference and checklist; derive the rest');
        await input.press('Tab');
        assert.equal(await page.evaluate(() => document.activeElement?.textContent?.trim()), 'Confirm selections');
      } else assert.match(await page.locator('.planner-question select option:checked').innerText(), /Recommended/);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'No horizontal overflow');
      await page.screenshot({ path: `${output}/${name}.png`, fullPage: true });
      await confirm.focus(); await page.keyboard.press('Enter');
    }
    await page.getByText(name === 'cancel' ? 'Cancelled' : 'Ready for your review', { exact: true }).waitFor({ timeout: 30000 });
    if (name !== 'cancel') {
      const support = page.locator('details').filter({ has: page.locator('summary', { hasText: 'Business requirements' }) });
      await support.waitFor();
      assert.match(await support.innerText(), /does not prove business completeness or execution success/);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'Requirements review fits mobile');
      await support.locator('summary').focus(); await page.keyboard.press('Enter');
      assert.equal(await support.getAttribute('open'), null);
      await page.keyboard.press('Enter');
      await page.reload(); await support.waitFor();
      await page.getByText('Ready for your review', { exact: true }).waitFor();
      await page.screenshot({ path: `${output}/${name}-outcomes.png`, fullPage: true });
    }
  }
  assert.deepEqual(errors, []);
  await writeFile(`${output}/results.json`, JSON.stringify({ recommendation: true, custom: true, textOnly: true, reload: true, keyboard: true, mobile: true, cancellation: true, businessRequirementsReview: true, legacyRevisionRequired: true, separateApproval: true, savedResponseRecovery: true, errors }, null, 2));
} catch (error) {
  await page.screenshot({ path: `${output}/failure.png`, fullPage: true });
  console.error((await page.locator('body').innerText()).slice(-6000)); console.error(errors); throw error;
} finally { await browser.close(); }

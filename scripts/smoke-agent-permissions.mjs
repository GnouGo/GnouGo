// Uses the real Agent.Server editor and MCP confirmation in an isolated host. No provider calls.
import assert from 'node:assert/strict';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE_PATH || 'playwright');
import {mkdir,writeFile} from 'node:fs/promises';
const info = { url: process.env.AGENT_SMOKE_URL, root: process.env.AGENT_SMOKE_OUTPUT };
const agentName = process.env.AGENT_SMOKE_NAME;
assert.ok(info.url && info.root && agentName, 'Set AGENT_SMOKE_URL, AGENT_SMOKE_OUTPUT and AGENT_SMOKE_NAME for an isolated host with a disposable, unapproved agent.');
await mkdir(info.root, { recursive: true });
const browser=await chromium.launch({headless:true});const page=await browser.newPage({viewport:{width:1440,height:1080}});
const errors=[];page.on('pageerror',e=>errors.push(e.message));
page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
const panel=page.locator('.gnougo-workflow-hitl');
const command=async()=>{
  await page.locator('.gnougo-composer textarea').fill('/mcp edit GnOuGo.GithubCopilot.Mcp');
  await page.locator('.gnougo-composer textarea').press('Enter');
  await panel.getByText('Persistent Copilot permission approval for a selected agent.',{exact:true}).waitFor();
  await panel.locator('.gnougo-hitl-field').filter({hasText:'agent_permissions'}).locator('select').selectOption('Configure agent permissions');
  await panel.locator('.gnougo-hitl-actions button.gnougo-composer__send').click();
  await panel.getByRole('button',{name: new RegExp('^' + agentName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + ' [(]')}).click();
  await panel.getByRole('button',{name:'Allow All including sandbox bypass',exact:true}).waitFor();
};
try {
  await page.goto(info.url);await page.waitForTimeout(1000);await page.getByText('New conversation',{exact:true}).click();
  await command();
  assert.match(await panel.innerText(),/No persistent approval/);
  await panel.getByRole('button',{name:'Allow All including sandbox bypass',exact:true}).click();
  await panel.getByRole('button',{name:'Confirm persistent sandbox-bypass approval',exact:true}).waitFor({timeout:30000});
  await page.screenshot({path:info.root+'/persistent-confirmation.png',fullPage:true});
  await panel.getByRole('button',{name:'Cancel',exact:true}).click();
  await page.getByText(/Copilot host gates were enabled, but persistent approval was not saved/).waitFor({timeout:30000});
  console.log('PASS cancellation');
  await command();
  assert.match(await panel.innerText(),/No persistent approval/);
  await panel.getByRole('button',{name:'Allow All including sandbox bypass',exact:true}).click();
  await panel.getByRole('button',{name:'Confirm persistent sandbox-bypass approval',exact:true}).click();
  await page.getByText(new RegExp('Allow All including sandbox bypass is saved for')).waitFor({timeout:30000});
  console.log('PASS grant creation');
  await command();
  assert.match(await panel.innerText(),/Current: Allow All including sandbox bypass/);
  await page.setViewportSize({width:430,height:900});
  await page.screenshot({path:info.root+'/persistent-grant-mobile.png',fullPage:true});
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth));
  await panel.getByRole('button',{name:'Remove persistent approval',exact:true}).click();
  await page.getByText(new RegExp('Persistent Copilot approval removed for')).waitFor({timeout:30000});
  console.log('PASS reread and revocation');
  assert.deepEqual(errors,[]);
  await writeFile(info.root+'/browser-permissions.json',JSON.stringify({cancel:true,confirmed:true,reread:true,revoked:true,mobile:true,errors},null,2));
} catch(e){console.log((await page.locator('body').innerText()).slice(-7000));console.log(errors);await page.screenshot({path:info.root+'/browser-failure.png',fullPage:true});throw e;}finally{await browser.close();}

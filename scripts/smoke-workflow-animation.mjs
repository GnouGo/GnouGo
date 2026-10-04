// Replays telemetry captured by real Flow execution tests through the shipped browser controller.
import assert from 'node:assert/strict';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE_PATH || 'playwright');
import {mkdir,readFile,readdir,writeFile} from 'node:fs/promises';
const info = { url: process.env.AGENT_SMOKE_URL, root: process.env.AGENT_SMOKE_OUTPUT };
const captures = process.env.GNOUGO_ANIMATION_CAPTURE;
assert.ok(info.url && info.root && captures, 'Set AGENT_SMOKE_URL, AGENT_SMOKE_OUTPUT and GNOUGO_ANIMATION_CAPTURE.');
await mkdir(info.root, { recursive: true });
const b=await chromium.launch({headless:true});const p=await b.newPage({viewport:{width:1440,height:1080}});const errors=[];p.on('pageerror',e=>errors.push(e.message));
p.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
const results=[];
try {
 await p.goto(info.url);await p.waitForFunction(()=>window.GnOuGo?.Agent?.workflowAnimation);await p.clock.install();
 for(const file of (await readdir(captures)).filter(f=>f.endsWith('.json'))) {
   const capture=JSON.parse(await readFile(captures+'/'+file,'utf8'));
   const expected=capture.updates.filter(u=>u.event).length;
   assert.equal(await p.evaluate(async c=>{
     window.GnOuGo.Agent.workflowAnimation.dispose('smoke-animation');document.getElementById('smoke-animation')?.remove();
     const host=document.createElement('div');host.id='smoke-animation';host.style.cssText='position:fixed;inset:0;z-index:9999;overflow:auto;background:white';document.body.append(host);
     const api=window.GnOuGo.Agent.workflowAnimation;const mounted=await api.mount(host.id,c.prepared.prepared);
     for(const update of c.updates){if(update.scenePatch)api.applyPatch(host.id,update.scenePatch);if(update.event)api.applyEvent(host.id,update.event);}
     return mounted;
   },capture),true);
   let state;
   for(let i=0;i<45;i++){
     await p.clock.runFor(2000);
     state=await p.locator('#smoke-animation').evaluate(e=>({count:Number(e.getAttribute('data-animation-event-count')),queued:e.getAttribute('data-animation-queued-events'),last:e.getAttribute('data-animation-last-event'),error:e.getAttribute('data-animation-error'),state:e.getAttribute('data-animation-state')}));
     if(state.count===expected&&state.queued==='0')break;
   }
   assert.equal(state.error,'');assert.equal(state.count,expected);assert.equal(state.last,'simulation.completed');assert.equal(state.queued,'0');
   await p.clock.runFor(4000);
   await p.screenshot({path:info.root+'/'+file.replace('.json','.png')});results.push({file,expected,...state});console.log('PASS',file,expected,state.state);
 }
 assert.deepEqual(errors,[]);await writeFile(info.root+'/browser-animation.json',JSON.stringify({results,errors},null,2));
}finally{await b.close();}

// Browser-level acceptance test. Uses an existing local Playwright installation and Edge;
// no downloads, Gateway connection or external requests.
const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const {pathToFileURL}=require('node:url');
const path=require('node:path');
const fs=require('node:fs');
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try {
  const page=await browser.newPage({viewport:{width:1600,height:1100}});
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/*',route=>route.request().url().startsWith('file:')?route.continue():route.abort());
  await page.goto(pathToFileURL(path.resolve(process.argv[2])).href,{waitUntil:'load',timeout:120000});
  const select=async(id,value)=>page.locator('#robust-'+id).selectOption(value);
  const rows=page.locator('#robust-rows tr');
  assert.equal(await page.locator('#robust-method').inputValue(),'Midpoint');
  assert.equal(await page.locator('#robust-alignment').inputValue(),'Consensus');
  assert.equal(await page.locator('#robust-zone').inputValue(),'America/New_York');
  assert.ok(await rows.count()>0 && await rows.count()<=50);
  assert.ok(!(await page.locator('#robust-strike').textContent()).includes('undefined'));
  const png=process.argv[3];if(png){fs.mkdirSync(path.dirname(path.resolve(png)),{recursive:true});await page.screenshot({path:png,fullPage:false});}
  if(await page.locator('#robust-next').isEnabled()){await page.locator('#robust-next').click();assert.ok((await page.locator('#robust-page').textContent()).startsWith('2 /'));await page.locator('#robust-prev').click();}
  for(const method of ['AtQuote','Midpoint','MidpointTick']){
   await select('method',method);
   for(const alignment of ['Consensus','Latest','Source']){
    await select('alignment',alignment);assert.ok(await rows.count()>0);
    assert.equal(await page.locator('#robust-scenario option').count(),alignment==='Consensus'?1:alignment==='Latest'?7:5);
    assert.equal(await page.locator('#robust-scenario').isDisabled(),alignment==='Consensus');
   }
  }
  await select('alignment','Latest');await select('scenario','Latest/MidpointTick/500');await select('method','AtQuote');
  assert.equal(await page.locator('#robust-scenario').inputValue(),'Latest/AtQuote/500');
  await select('alignment','Source');await select('scenario','Source+100/AtQuote');await select('method','Midpoint');
  assert.equal(await page.locator('#robust-scenario').inputValue(),'Source+100/Midpoint');
  await page.locator('#robust-preset').click();
  const sample=await page.evaluate(()=>JSON.parse(document.getElementById('robust-data').textContent).buckets.find(r=>r.intervalMinutes===1&&r.scope==='RegularTrades'));
  await select('ticker',sample.contract.ticker);await select('strike',String(sample.contract.strikeUsd));await select('right',sample.contract.right===0?'Call':'Put');
  assert.ok(await rows.count()>0);
  for(const text of await rows.locator('td:first-child').allTextContents())assert.ok(text.includes(String(sample.contract.strikeUsd)));
  for(const term of ['method','alignment','tendency','coverage','bounds','timing','unknown','tolerance']){
   await page.locator('[data-help="'+term+'"]').click();assert.ok(await page.locator('#robust-dialog').isVisible());
   assert.ok((await page.locator('#robust-detail').textContent()).length>40);
   await page.keyboard.press('Escape');assert.ok(!await page.locator('#robust-dialog').isVisible());
  }
  const bars=await page.locator('.unknown-bar').evaluateAll(nodes=>nodes.map(n=>Array.from(n.children).reduce((sum,b)=>sum+parseFloat(b.style.width),0)));
  // Browser CSSOM rounds percentage strings; numeric mass conservation is checked separately.
  assert.ok(bars.length>0);for(const v of bars)assert.ok(Math.abs(v-100)<1e-4);
  await page.locator('.unknown-bar button').first().click();assert.match(await page.locator('#robust-dialog-title').textContent(),/^U\d+/);
  assert.ok((await page.locator('#robust-detail').textContent()).includes('占未知'));
  if(png)await page.screenshot({path:png.replace('.png','-reason.png')});
  await page.locator('#robust-close').click();
  const u25=page.locator('.unknown-bar button[aria-label^="U25"]').first();
  if(await u25.count()){
   await u25.click();assert.ok((await page.locator('#robust-detail').textContent()).includes('每笔只计一次'));
   const group=page.locator('#robust-detail .u25-group').first();assert.ok(await group.count());
   const widths=await page.locator('#robust-detail .u25-share span').evaluateAll(nodes=>nodes.reduce((sum,n)=>sum+parseFloat(n.style.width),0));assert.ok(Math.abs(widths-100)<1e-4);
   await group.locator(':scope > summary').click();await group.locator('.u25-example > summary').first().click();
   assert.ok((await group.textContent()).includes('买价 / 卖价'));
   assert.ok((await group.textContent()).includes('原始序号'));
   assert.match(await group.locator('.u25-example').first().textContent(),/\d{2}:\d{2}:\d{2}\.\d{3}/);
   if(png)await page.screenshot({path:png.replace('.png','-u25.png')});
   await page.locator('#robust-close').click();
  }
  await rows.first().locator('td:last-child button').click();assert.ok((await page.locator('#robust-detail').textContent()).includes('额外误判压力'));
  if(png)await page.screenshot({path:png.replace('.png','-detail.png')});
  await page.locator('#robust-close').click();
  await select('interval','0');assert.equal(await rows.count(),1);
  await select('zone','UTC');assert.ok(await rows.count()>0);
  await select('scope','AllTimeAndSales');assert.equal(await rows.count(),1);
  await select('right',sample.contract.right===0?'Put':'Call');assert.equal(await rows.count(),1);
  await select('interval','1');await page.setViewportSize({width:390,height:844});
  const overflow=await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1);assert.equal(overflow,false,'Only table should scroll horizontally, not the page');
  if(png)await page.screenshot({path:png.replace('.png','-mobile.png')});
  assert.deepEqual(errors,[]);
  console.log('PASS real Edge browser: 9 method/alignment combinations, defaults, strikes, rights, scopes, timezones, pagination, eight help dialogs, unknown bars total 100%, reason popup, compact detail, mobile overflow, no JS errors.');
 } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});

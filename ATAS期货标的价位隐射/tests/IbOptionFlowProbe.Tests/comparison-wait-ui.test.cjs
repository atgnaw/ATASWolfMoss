const fs=require('node:fs'),assert=require('node:assert/strict');
const {JSDOM,VirtualConsole}=require('../../tools/UwPageCollector/node_modules/jsdom');
const errors=[],vc=new VirtualConsole();vc.on('jsdomError',e=>errors.push(e.message));
const dom=new JSDOM(fs.readFileSync(process.argv[2],'utf8'),{runScripts:'dangerously',virtualConsole:vc});
const doc=dom.window.document,get=id=>doc.getElementById(id),data=JSON.parse(get('data').textContent);
assert.equal(data.configs.length,72);assert.equal(data.result.pairCount,100);
assert.ok(data.pairs.every(p=>p.start<'13:55'));
function select(id,value){get(id).value=value;get(id).dispatchEvent(new dom.window.Event('change'));}
for(const ticker of ['全部','QQQ','SPX']){
 select('ticker',ticker);
 for(const method of ['AtQuote','Midpoint'])for(const toggle of ['U15_ON','U15_OFF']){
  select('config',`AllTimeAndSales|${toggle}|Latest/${method}/-1`);
  assert.equal(get('waitrows').children.length,1);
  const buttons=[...get('waitrows').querySelectorAll('button')];
  buttons[0].click();assert.equal(get('config').value,`AllTimeAndSales|${toggle}|Conditional/${method}/50`);
  assert.equal(get('rows').children.length,ticker==='全部'?100:50);
 }
}
assert.deepEqual(errors,[]);dom.window.close();
console.log('PASS comparison 72 configs, 100 pairs, conditional-only comparison button, ticker/method/toggle changes, excluded final bucket');

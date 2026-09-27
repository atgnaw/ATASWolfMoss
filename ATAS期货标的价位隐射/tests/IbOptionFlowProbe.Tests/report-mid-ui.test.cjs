const fs = require('node:fs');
const assert = require('node:assert/strict');
const { JSDOM, VirtualConsole } = require('../../tools/UwPageCollector/node_modules/jsdom');
const html = fs.readFileSync(process.argv[2], 'utf8');
const errors=[];
const consoleCapture=new VirtualConsole();consoleCapture.on('jsdomError',e=>errors.push(e.message));
// Keep the large JSON out of jsdom's character-by-character HTML tokenizer.
const marker='<script id="robust-data" type="application/json">';
const start=html.indexOf(marker)+marker.length,end=html.indexOf('</script>',start);
const payload=html.slice(start,end);
const dom=new JSDOM(html.slice(0,start)+html.slice(end),{runScripts:'outside-only',virtualConsole:consoleCapture});
const doc=dom.window.document;
const get=id=>doc.getElementById('robust-'+id);
get('data').textContent=payload;
for(const script of doc.querySelectorAll('script[type="text/javascript"]'))dom.window.eval(script.textContent);
const data=JSON.parse(get('data').textContent);
assert.ok(data.outside);
assert.equal(get('outside').checked,false);
assert.equal(get('outside').disabled,false);
assert.ok(!data.reasons.some(r=>['U11','U17'].includes(r.code)));
let midTotal=0,changes=0;
for(let i=0;i<data.buckets.length;i++){
 const off=data.buckets[i],on=data.outside.buckets[i];
 assert.equal(off.contract.conId,on.contract.conId);
 assert.equal(off.startUtc,on.startUtc);
 for(const key of Object.keys(off.scenarios)){
  const a=off.scenarios[key],b=on.scenarios[key];
  for(const v of [a,b]){
   assert.ok(Math.abs(v.buy+v.sell+v.mid+v.unknown-v.metrics.total)<.01);
   assert.equal(v.unknownPremiums.LOCKED_QUOTE,undefined);
   assert.equal(v.unknownPremiums.MIDPOINT_UNKNOWN,undefined);
  }
  assert.ok(Math.abs(a.metrics.total-b.metrics.total)<.01);
  midTotal+=a.mid;
  if(Math.abs(a.buy-b.buy)+Math.abs(a.sell-b.sell)>.01)changes++;
 }
}
assert.ok(midTotal>0); assert.ok(changes>0);
function select(id,value){get(id).value=value;get(id).dispatchEvent(new dom.window.Event('change'));}
select('alignment','Latest');select('method','Midpoint');select('scenario','Latest/Midpoint/-1');
const before=get('summary').textContent;
get('outside').checked=true;get('outside').dispatchEvent(new dom.window.Event('change'));
assert.notEqual(get('summary').textContent,before);
assert.match(get('rows').textContent,/Mid/);
assert.match(get('rows').textContent,/买卖方向/);
get('outside').checked=false;get('outside').dispatchEvent(new dom.window.Event('change'));
assert.equal(get('summary').textContent,before);
get('outside').checked=true;get('outside').dispatchEvent(new dom.window.Event('change'));
get('preset').click();assert.equal(get('outside').checked,false);
assert.equal(get('alignment').value,'Consensus');
assert.deepEqual(errors,[]);
console.log('PASS: real report Mid accounting, U15 toggle, restore, total conservation, no script errors; changed scenarios='+changes);
dom.window.close();

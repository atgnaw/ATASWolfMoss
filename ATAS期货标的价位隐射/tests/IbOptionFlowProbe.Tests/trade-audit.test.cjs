'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),crypto=require('node:crypto');
const R=require('../../tools/IbOptionFlowProbe/CrossDayResearch.cjs'),A=require('../../tools/IbOptionFlowProbe/ResearchTradeAudit.cjs'),Report=require('../../tools/IbOptionFlowProbe/ResearchAuditReport.cjs'),Q=require('../../tools/IbOptionFlowProbe/QuoteAuditClassifier.cjs');
const dir='docs/reports/uw-ib-crossday/',audit=JSON.parse(fs.readFileSync(dir+'trade-audit.json')),research=JSON.parse(fs.readFileSync(dir+'research.json'));
const sum=a=>a.reduce((s,v)=>s+v,0),near=(a,b)=>assert.ok(Math.abs(a-b)<1e-10,`${a} != ${b}`);
assert.equal(audit.version,2);assert.equal(audit.buckets.length,257);assert.equal(sum(audit.buckets.map(b=>b.features.total)),940246);
const fixture=(bid,ask)=>({quote:{data_type:1,bid:{price:bid,invalid:false},ask:{price:ask,invalid:false},bid_size:1,ask_size:1},bid_age_ms:0,ask_age_ms:0});
assert.equal(Q.classify(fixture(2.81,2.91),2.86),2);assert.equal(Q.classify(fixture(2.81,2.89),2.86),0);
assert.equal(Q.classify(fixture(2.81,2.91),2.9,'AtQuote'),3);assert.equal(Q.classify(fixture(2.81,2.91),2.95,'AtQuote'),0);
assert.equal(Q.classify(fixture(2.91,2.81),2.86),3);assert.equal(Q.classify(fixture(2.81,2.81),2.86),2);
assert.equal(Q.classify({...fixture(2.81,2.91),ask_age_ms:null},2.86),3);assert.equal(Q.classify({...fixture(2.81,2.91),ask_age_ms:undefined},2.86),3);
assert.equal(Q.classify({error:'LOCKED_QUOTE'},2.86),2);assert.equal(Q.classify(null,2.86),3);
near(Q.elapsedMs('2026-09-23T17:49:08.2804100Z','2026-09-23T17:49:08.3277460Z'),47.336);
near(Q.elapsedMs('2026-09-23T17:49:08.9999999Z','2026-09-23T17:49:09.0499999Z'),50);assert.equal(Q.elapsedMs('bad','bad'),null);
const tiny=A.summarize([{size:9,bidAgeMs:2000,askAgeMs:0,sourceLagMs:100,reason:'AT_ASK',bid:1,ask:2,branch:'SMALL_LATEST',cumulativeVolume:9},{size:1,bidAgeMs:0,askAgeMs:0,sourceLagMs:0,reason:'BELOW_BID_OVERRIDE',bid:1,ask:2,branch:'SMALL_LATEST',cumulativeVolume:10}]);
near(tiny.largestShare,.9);near(tiny.old1000,.9);near(tiny.quoteAgeWeightedMs,1800);near(tiny.effectiveCallbacks,100/82);near(tiny.outsideVolume,.1);assert.equal(A.summarize([]).largestShare,null);
for(const b of audit.buckets){
 const r=research.rows.find(r=>r.day===b.day&&r.key===b.key);assert.ok(r);assert.deepEqual(b.counts,r.counts[R.BASE]);assert.equal(b.features.total,sum(b.counts));assert.equal(b.staleModels.length,18);
 assert.ok(b.features.largestShare<=b.features.top3Share+1e-12);assert.ok(b.features.top3Share<=1+1e-12);assert.ok(b.features.effectiveCallbacks<=b.features.callbacks+1e-9);
 for(const m of b.staleModels){assert.equal(sum(m.counts),sum(b.counts));assert.equal(sum(m.atQuote),sum(b.counts));
  for(const t of m.largestChanges){assert.ok(t.isLarge&&t.timelyNext&&t.nextDelayMs>=0&&t.nextDelayMs<=50);assert.ok(Math.max(t.bidAgeMs,t.askAgeMs)>=m.ageMs);assert.notEqual(t.category,t.nextCategory);assert.ok(t.nextCategory<3&&t.category<3);if(m.mode==='both-directional')assert.ok(t.category<2&&t.nextCategory<2);if(m.mode==='open-spread')assert.ok(t.nextBid<t.nextAsk);}
 }
}
const result=Report.build(research,audit);assert.equal(result.residual.length,4);assert.equal(result.bad.length,13);assert.equal(result.models.length,19);
for(const f of result.modelFolds){assert.ok(!f.trainDays.includes(f.day));assert.ok(result.models.some(m=>m.id===f.selected));}
const raw=research.rows.map(r=>({...r,features:{...r.features,...Object.fromEntries(Object.entries(audit.buckets.find(b=>b.day===r.day&&b.key===r.key).features).map(([k,v])=>['audit_'+k,v]))}}));
const changed=raw.map(r=>r.day==='2026-09-29'?{...r,uw:[r.uw[1],r.uw[0],r.uw[2],r.uw[3]]}:r);
const filters=Report.candidates();assert.equal(R.fitFilter(raw.filter(r=>r.day!=='2026-09-29'),.6,filters).filter.id,R.fitFilter(changed.filter(r=>r.day!=='2026-09-29'),.6,filters).filter.id);
const {JSDOM,VirtualConsole}=require('../../tools/UwPageCollector/node_modules/jsdom'),html=Report.html(result),marker='<script id="audit-data" type="application/json">',start=html.indexOf(marker)+marker.length,end=html.indexOf('</script>',start),errors=[];
const vc=new VirtualConsole();vc.on('jsdomError',e=>errors.push(e.message));const dom=new JSDOM(html.slice(0,start)+html.slice(end),{runScripts:'outside-only',virtualConsole:vc});
const doc=dom.window.document;doc.getElementById('audit-data').textContent=html.slice(start,end);for(const s of doc.querySelectorAll('script:not([type])'))dom.window.eval(s.textContent);
const select=doc.getElementById('bucket');assert.equal(select.options.length,13);for(let i=0;i<13;i++){select.value=String(i);select.dispatchEvent(new dom.window.Event('change'));assert.ok(doc.getElementById('detail').textContent.includes(result.bad[i].label));}assert.deepEqual(errors,[]);dom.window.close();
const saved=JSON.parse(fs.readFileSync(dir+'audit-research.json'));assert.equal(saved.protocol.sourceHashes.audit,crypto.createHash('sha256').update(fs.readFileSync(dir+'trade-audit.json')).digest('hex'));
assert.equal(saved.protocol.sourceHashes.research,crypto.createHash('sha256').update(fs.readFileSync(dir+'research.json')).digest('hex'));
console.log('PASS: 257-bucket trace reconciliation, decimal classifier, precise 50ms window, 18 model variants, IB-only features, date isolation, and audit HTML');

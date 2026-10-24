'use strict';
const fs=require('node:fs'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const V=require('../../tools/IbOptionFlowProbe/ValidateFrozenCandidate.cjs'),dir='docs/reports/uw-ib-20261020/corrected-time';
const bytes=fs.readFileSync(dir+'/comparison.html'),d=V.payload(bytes.toString()),specBytes=fs.readFileSync('docs/reports/uw-ib-crossday/filter-candidate.json'),spec=JSON.parse(specBytes),r=V.evaluate(d,spec),saved=JSON.parse(fs.readFileSync(dir+'/frozen-validation.json'));
assert.equal(r.date,'2026-10-20');assert.equal(r.window.startLabel,'14:45');assert.equal(r.window.endLabel,'15:10');assert.equal(r.window.excludedFinalLabel,'15:10–15:15');
assert.equal(r.rows.length,100);assert.equal(r.all.before.valid,60);assert.equal(r.all.before.missing,40);assert.equal(r.all.after.n,50);assert.equal(r.all.before.bad10,3);assert.equal(r.all.after.bad10,1);
assert.deepEqual(r.all,saved.all);assert.deepEqual(fs.readFileSync(dir+'/frozen-candidate.json'),specBytes);
assert.equal(saved.provenance.comparisonSha256,crypto.createHash('sha256').update(bytes).digest('hex'));
const valid=r.rows.filter(x=>x.error!==null),mean=valid.reduce((s,x)=>s+x.error*x.uwVolume,0)/valid.reduce((s,x)=>s+x.uwVolume,0);assert.ok(Math.abs(mean-r.all.before.mean)<1e-12);
for(const row of r.rows.filter(x=>x.error===null)){assert.ok(['15:00','15:05'].includes(row.start));assert.equal(row.ib[0]+row.ib[1]+row.ib[2],0);assert.equal(row.decision.reason,'NO_BUY_SELL_REFERENCE');}
const altered=structuredClone(d);for(const p of altered.pairs)[p.uw[0],p.uw[1]]=[p.uw[1],p.uw[0]];assert.deepEqual(V.evaluate(altered,spec).rows.map(x=>x.decision),r.rows.map(x=>x.decision));
const {JSDOM,VirtualConsole}=require('../../tools/UwPageCollector/node_modules/jsdom'),errors=[],vc=new VirtualConsole();vc.on('jsdomError',e=>errors.push(e.message));
const dom=new JSDOM(fs.readFileSync(dir+'/frozen-validation.html','utf8'),{runScripts:'dangerously',virtualConsole:vc}),doc=dom.window.document;
assert.ok(doc.getElementById('time-normalization-note'));const state=doc.getElementById('state'),rows=doc.getElementById('rows');assert.equal(rows.children.length,100);
for(const [v,n]of [['kept',50],['removed',50],['missed',1]]){state.value=v;state.dispatchEvent(new dom.window.Event('change'));assert.equal(rows.children.length,n);}
assert.match(rows.textContent,/7720/);assert.deepEqual(errors,[]);dom.window.close();
console.log('PASS October 20 frozen validation: no retuning, 40 unevaluable buckets excluded from errors, independent mean, source hash and UI');

'use strict';
const fs=require('node:fs'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const V=require('../../tools/IbOptionFlowProbe/ValidateFrozenCandidate.cjs');
const [dir,date]=process.argv.slice(2),bytes=fs.readFileSync(dir+'/comparison.html'),d=V.payload(bytes.toString()),specBytes=fs.readFileSync('docs/reports/uw-ib-crossday/filter-candidate.json'),spec=JSON.parse(specBytes),r=V.evaluate(d,spec),saved=JSON.parse(fs.readFileSync(dir+'/frozen-validation.json'));
assert.equal(r.date,date);assert.deepEqual(r.all,saved.all);assert.deepEqual(fs.readFileSync(dir+'/frozen-candidate.json'),specBytes);
const hash=x=>crypto.createHash('sha256').update(x).digest('hex');assert.equal(saved.provenance.specSha256,hash(specBytes));assert.equal(saved.provenance.comparisonSha256,hash(bytes));assert.equal(saved.source.uwSha256,hash(fs.readFileSync(saved.source.uwFile)));
for(const [rows,stats]of [[r.rows,r.all.before],[r.rows.filter(x=>x.decision.keep),r.all.after]]){
 const valid=rows.filter(x=>x.ib[0]+x.ib[1]>0&&x.uw[0]+x.uw[1]>0);assert.equal(stats.valid,valid.length);assert.equal(stats.missing,rows.length-valid.length);
 if(valid.length){const e=x=>Math.abs(x.ib[0]/(x.ib[0]+x.ib[1])-x.uw[0]/(x.uw[0]+x.uw[1])),mean=valid.reduce((s,x)=>s+e(x)*x.uwVolume,0)/valid.reduce((s,x)=>s+x.uwVolume,0);assert.ok(Math.abs(stats.mean-mean)<1e-12);assert.equal(stats.max,Math.max(...valid.map(e)));}
}
const altered=structuredClone(d);for(const p of altered.pairs)[p.uw[0],p.uw[1]]=[p.uw[1],p.uw[0]];assert.deepEqual(V.evaluate(altered,spec).rows.map(x=>x.decision),r.rows.map(x=>x.decision));
const {JSDOM,VirtualConsole}=require('../../tools/UwPageCollector/node_modules/jsdom'),errors=[],vc=new VirtualConsole();vc.on('jsdomError',e=>errors.push(e.message));
const dom=new JSDOM(fs.readFileSync(dir+'/frozen-validation.html','utf8'),{runScripts:'dangerously',virtualConsole:vc}),doc=dom.window.document,state=doc.getElementById('state'),rows=doc.getElementById('rows');
assert.equal(rows.children.length,r.rows.length);for(const [v,n]of [['kept',r.all.after.n],['removed',r.all.rejected.n],['missed',r.all.after.bad10]]){state.value=v;state.dispatchEvent(new dom.window.Event('change'));assert.equal(rows.children.length,n);}
assert.deepEqual(errors,[]);dom.window.close();console.log('PASS frozen new day '+date+': hashes, independent metrics, missing data denominator, no UW label leakage, UI filters');

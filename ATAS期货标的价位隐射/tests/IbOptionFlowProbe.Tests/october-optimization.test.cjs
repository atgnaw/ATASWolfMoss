'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const R=require('../../tools/IbOptionFlowProbe/CrossDayResearch.cjs'),Risk=require('../../tools/IbOptionFlowProbe/OctoberRisk.js'),E=require('../../tools/IbOptionFlowProbe/OctoberEvaluate.cjs'),S=require('../../tools/IbOptionFlowProbe/OctoberOptimization.cjs'),Future=require('../../tools/IbOptionFlowProbe/ValidateOctoberCandidate.cjs');
const file=path.join(S.OUT,'optimization.json'),d=JSON.parse(fs.readFileSync(file)),spec=JSON.parse(fs.readFileSync(path.join(S.OUT,'candidate-v2.json'))),auditBytes=fs.readFileSync(path.join(S.OUT,'trade-audit.json')),audit=JSON.parse(auditBytes);
const near=(a,b)=>assert.ok(Math.abs(a-b)<1e-11,`${a} != ${b}`),sha=b=>crypto.createHash('sha256').update(b).digest('hex');
assert.equal(sha(auditBytes),d.provenance.auditSha256);assert.equal(sha(fs.readFileSync(path.resolve('tools/IbOptionFlowProbe/OctoberRisk.js'))),d.provenance.riskSha256);
const {rows,days}=E.load(audit);assert.equal(rows.length,499);assert.deepEqual(days.map(d=>d.pairs.length),[99,100,100,100,100]);assert.equal(audit.buckets.reduce((s,b)=>s+b.trades.length,0),201873);
assert.deepEqual(spec.policy,d.fixed.policy);assert.ok(d.rows.every(r=>r.day>=spec.researchDates[0]));
for(const day of days){assert.ok(day.pairs.every(p=>p.t!==day.result.windowDetails.finalStart&&p.t+300000<=day.result.windowDetails.last));}
for(const b of audit.buckets){
 const counts=[0,0,0,0];for(const t of b.trades){counts[t.category]+=t.size;if(t.timelyNext)assert.ok(t.nextDelayMs>=0&&t.nextDelayMs<=50);}
 assert.deepEqual(counts,b.counts);for(const m of b.staleModels)assert.equal(m.counts.reduce((a,b)=>a+b,0),counts.reduce((a,b)=>a+b,0));
 const last=b.trades.at(-1),first=b.trades[0];if(b.day==='2026-10-20'&&b.label==='15:00–15:05 QQQ 716 PUT')assert.equal(last.cumulativeVolume-first.cumulativeVolume+first.size,1098);
}
// Independent buy-share metric: no mid or unknown in the buy/sell denominator.
const kept=d.rows.filter(r=>r.candidateDecision.keep),weights=kept.reduce((s,r)=>s+r.uwVolume,0);
const errors=kept.map(r=>{const c=r.counts[R.BASE],u=r.uw;return Math.abs(c[0]/(c[0]+c[1])-u[0]/(u[0]+u[1]));});
near(d.fixed.all.after.mean,kept.reduce((s,r,i)=>s+errors[i]*r.uwVolume,0)/weights);near(d.fixed.all.after.max,Math.max(...errors));assert.equal(d.fixed.all.after.bad10,errors.filter(e=>e>.1).length);
assert.equal(d.base.after.n,379);assert.equal(d.base.after.bad10,16);assert.equal(d.fixed.all.after.n,332);assert.equal(d.fixed.all.after.bad10,7);
assert.equal(d.fixed.all.before.n,d.fixed.all.after.n+d.fixed.all.rejected.n);assert.equal(d.fixed.all.before.ibVolume,d.fixed.all.after.ibVolume+d.fixed.all.rejected.ibVolume);
// Runtime classifier uses IB-only features. Unknown comparisons are not treated as zero gaps.
const p=spec.policy,valid={methodGap:.01,conditionalGap:.1,outsideGap:.05};assert.equal(Risk.assess(valid,p).keep,true);assert.equal(Risk.assess(valid,p).waitMs,50);
for(const k of Object.keys(valid)){for(const v of [null,undefined,NaN,Infinity])assert.equal(Risk.assess({...valid,[k]:v},p).keep,false);assert.equal(Risk.assess({...valid,[k]:valid[k]+.00001},p).keep,false);}
assert.equal(Risk.assess({methodGap:0,conditionalGap:0,outsideGap:0},p).keep,true); // no large trades => no disagreement, not automatic rejection
for(const row of rows)assert.deepEqual(Risk.assess(row.features,p),Risk.assess({...row.features,uwBuyShare:Math.random(),uwVolume:-1,multi:100},p));
// Held-out labels cannot affect fitting or held-out keep flags. Splits are ENTIRE dates.
for(const fold of d.folds){
 assert.ok(!fold.trainDays.includes(fold.day));const training=rows.filter(r=>r.day!==fold.day),fit=E.fit(training);assert.deepEqual(fit.policy,fold.policy);
 const altered=rows.map(r=>r.day===fold.day?{...r,uw:[r.uw[1],r.uw[0],r.uw[2],r.uw[3]],multi:'100%'}:r);
 assert.deepEqual(E.fit(altered.filter(r=>r.day!==fold.day)).policy,fold.policy);
 for(const t of fit.training){assert.ok(t.candidate.after.n>=.7*t.incumbent.after.n);assert.ok(t.candidate.after.ibVolume>=.8*t.incumbent.after.ibVolume);}
}
for(const fold of d.forward)assert.ok(fold.trainDays.every(day=>day<fold.day));
const loo=rows.filter(r=>Risk.assess(r.features,d.folds.find(f=>f.day===r.day).policy).keep);assert.deepEqual(R.metric(loo),d.crossfit.after);
const sourceHtml=fs.readFileSync(path.resolve(S.SOURCES.at(-1)[1]),'utf8'),payload=JSON.parse(sourceHtml.match(/<script id="data" type="application\/json">([\s\S]*?)<\/script>/)[1]);
assert.throws(()=>Future.evaluate(payload,spec),/genuinely later/);
// Synthetic later-date gate tests execution only, never counted as real validation evidence.
const fake=structuredClone(payload);fake.result.windowDetails.date='2026-10-26';assert.throws(()=>Future.evaluate(fake,spec),/timestamps/);
for(const p of fake.pairs){p.t+=3*86400000;p.key=p.conId+'|'+p.t;}for(const k of ['first','last','finalStart'])fake.result.windowDetails[k]+=3*86400000;
const result=Future.evaluate(fake,spec);assert.equal(result.candidate.after.n,byDay('2026-10-23').after.n);
function byDay(day){return d.policyResults.find(x=>x.policy.id===spec.policy.id).days.find(x=>x.day===day);}
const {JSDOM,VirtualConsole}=require('../../tools/UwPageCollector/node_modules/jsdom');const html=fs.readFileSync(path.join(S.OUT,'research.html'),'utf8'),mark='<script id="study-data" type="application/json">',start=html.indexOf(mark)+mark.length,end=html.indexOf('</script>',start),errorsUI=[],vc=new VirtualConsole();vc.on('jsdomError',e=>errorsUI.push(e.message));
const dom=new JSDOM(html.slice(0,start)+html.slice(end),{runScripts:'outside-only',virtualConsole:vc}),doc=dom.window.document,get=id=>doc.getElementById(id);get('study-data').textContent=html.slice(start,end);for(const s of doc.querySelectorAll('script:not([type])'))dom.window.eval(s.textContent);
assert.equal(get('rows').children.length,16);const select=(id,value)=>{get(id).value=value;get(id).dispatchEvent(new dom.window.Event('change'));};
select('state','remaining');assert.equal(get('rows').children.length,7);select('state','caught');assert.equal(get('rows').children.length,9);select('state','kept');assert.equal(get('rows').children.length,332);select('day','2026-10-23');assert.equal(get('rows').children.length,byDay('2026-10-23').after.n);select('day','all');select('policy','incumbent');assert.equal(get('rows').children.length,379);select('state','all');assert.equal(get('rows').children.length,499);assert.deepEqual(errorsUI,[]);dom.window.close();
console.log('PASS October optimization: 499 buckets / 201873 callbacks; source hashes; count conservation; metrics; whole-day splits; IB-only predicates; missing-value gates; frozen-date guard; HTML interactions');

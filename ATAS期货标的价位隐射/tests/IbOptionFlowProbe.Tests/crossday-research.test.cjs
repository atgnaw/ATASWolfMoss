'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs');
const R=require('../../tools/IbOptionFlowProbe/CrossDayResearch.cjs');
const Stability=require('../../tools/IbOptionFlowProbe/BuySellStability.js');
const days=R.loadSources(),rows=R.observations(days),result=R.run(days);
const near=(a,b)=>assert.ok(Math.abs(a-b)<1e-12,`${a} != ${b}`);
assert.deepEqual(days.map(d=>d.pairs.length),[100,68,89]);
assert.equal(rows.length,257);assert.equal(new Set(rows.map(r=>r.day+'|'+r.key)).size,257);
assert.ok(days.find(d=>d.day==='2026-09-28').pairs.every(p=>p.end<='15:55'));
assert.ok(days.every(d=>d.pairs.every(p=>p.t<d.result.windowDetails.last)));
assert.equal(result.rankings.length,72);
assert.ok(result.rankings.every(x=>!/(MidpointTick|Envelope|Source)/.test(x.id)));
assert.ok(result.folds.every(f=>!f.trainDays.includes(f.day)));
assert.ok(result.chronological.every(f=>f.trainDays.every(d=>d<f.day)));
// Independent metric calculation; midpoint and unknown cannot enter the BA denominator.
const total=rows.reduce((s,r)=>s+r.uwVolume,0);
const errors=rows.map(r=>Math.abs(r.counts[R.BASE][0]/(r.counts[R.BASE][0]+r.counts[R.BASE][1])-r.uw[0]/(r.uw[0]+r.uw[1])));
near(result.baseline.mean,rows.reduce((s,r,i)=>s+r.uwVolume*errors[i],0)/total);
near(result.baseline.max,Math.max(...errors));assert.equal(result.baseline.bad10,errors.filter(e=>e>.1).length);
const fake=(ib,uw)=>[{counts:{test:ib},uw,uwVolume:uw.reduce((a,b)=>a+b,0)}];
near(R.metric(fake([60,40,900,100],[30,20,0,0]),'test').mean,0);
assert.equal(R.metric(fake([0,0,100,0],[30,20,0,0]),'test').mean,null);
assert.equal(R.metric(fake([50,50,0,0],[0,0,100,0]),'test').missing,1);
assert.equal(R.metric([]).mean,null);
assert.equal(R.passes({score:0},{rules:[{feature:'score',op:'gte',value:0}]}),true);
assert.equal(R.passes({score:null},{rules:[{feature:'score',op:'gte',value:-100}]}),false);
assert.equal(R.passes({methodGap:.01},{rules:[{feature:'methodGap',op:'lte',value:.01}]}),true);
assert.equal(Stability.assess([60,40,0,0],[61,39,0,0]).keep,true);
assert.equal(Stability.assess([60,40,0,0],[62,38,0,0]).reason,'METHODS_DIVERGE');
assert.equal(Stability.assess([60,40,0,0],[61,38,0,0]).reason,'INCOMPARABLE_TOTALS');
assert.equal(Stability.assess([0,0,100,0],[60,40,0,0]).reason,'NO_BUY_SELL_REFERENCE');
assert.equal(Stability.assess([-1,101,0,0],[60,40,0,0]).reason,'INVALID_COUNTS');
assert.throws(()=>Stability.assess([60,40,0,0],[61,39,0,0],NaN));
for(const r of rows){const x=Stability.assess(r.counts[R.BASE],r.counts['AllTimeAndSales|U15_ON|Latest/AtQuote/-1']);assert.equal(x.keep,R.passes(r.features,{rules:[{feature:'methodGap',op:'lte',value:.01}]}));near(x.gap,r.features.methodGap);}
// Features have no UW/ticker/date input. Permuting reference labels cannot change features or held-out decisions.
for(const d of days){
 const altered={...d,pairs:d.pairs.map(p=>({...p,uw:[p.uw[1],p.uw[0],p.uw[2],p.uw[3]]}))};
 assert.deepEqual(R.observations([d]).map(r=>r.features),R.observations([altered]).map(r=>r.features));
 const train=rows.filter(r=>r.day!==d.day),changed=rows.map(r=>r.day===d.day?{...r,uw:[r.uw[1],r.uw[0],r.uw[2],r.uw[3]]}:r);
 for(const retain of [.6,.7,.8,.9]){
  const fitted=R.fitFilter(train,retain),again=R.fitFilter(changed.filter(r=>r.day!==d.day),retain);
  assert.equal(fitted.filter.id,again.filter.id);
  for(const s of fitted.training){assert.ok(s.bucketRetention+1e-12>=retain);assert.ok(s.volumeRetention+1e-12>=retain);assert.equal(s.after.missing,0);}
 }
 assert.equal(R.fitConfig(train,[...d.configs.keys()]).id,R.fitConfig(changed.filter(r=>r.day!==d.day),[...d.configs.keys()]).id);
}
for(const x of result.filterAudits){
 const s=x.all;assert.equal(s.after.n+s.rejected.n,s.before.n);near(s.after.ibVolume+s.rejected.ibVolume,s.before.ibVolume);
 assert.equal(s.after.bad10+s.rejected.bad10,s.before.bad10);assert.equal(s.removedKeys.length,s.rejected.n);
}
assert.equal(result.rankings[0].id,R.BASE);
const f=result.filterAudits.find(x=>x.filter.id==='methodGap:lte:0.01');
assert.equal(f.all.after.n,185);assert.equal(f.all.rejected.bad10,9);assert.equal(f.all.after.bad20,0);
// UI: avoid parsing the large data payload with the DOM tokenizer; execute the actual generated script.
const {JSDOM,VirtualConsole}=require('../../tools/UwPageCollector/node_modules/jsdom');
const html=R.html(result),marker='<script id="research-data" type="application/json">',start=html.indexOf(marker)+marker.length,end=html.indexOf('</script>',start),errorsUI=[];
const vc=new VirtualConsole();vc.on('jsdomError',e=>errorsUI.push(e.message));
const dom=new JSDOM(html.slice(0,start)+html.slice(end),{runScripts:'outside-only',virtualConsole:vc});
const doc=dom.window.document,get=id=>doc.getElementById(id);get('research-data').textContent=html.slice(start,end);
for(const script of doc.querySelectorAll('script:not([type])'))dom.window.eval(script.textContent);
assert.equal(get('buckets').children.length,257);assert.equal(get('ranking').children.length,16);
const select=(id,value)=>{get(id).value=value;get(id).dispatchEvent(new dom.window.Event('change'));};
select('rowpolicy','candidate');select('candidate','methodGap:lte:0.01');select('state','removed');assert.equal(get('buckets').children.length,72);
assert.match(get('buckets').textContent,/740 PUT/);assert.match(get('buckets').textContent,/中间价与贴价法/);
select('state','kept');assert.equal(get('buckets').children.length,185);
for(const d of days){select('rowday',d.day);const a=f.days.find(x=>x.day===d.day);assert.equal(get('buckets').children.length,a.after.n);}
select('candidate','none');select('state','removed');assert.equal(get('buckets').children.length,0);
select('rowday','all');select('state','all');select('rowpolicy','crossfit');
for(const retain of [.6,.7,.8,.9]){select('retain',String(retain));assert.equal(get('buckets').children.length,257);assert.equal(get('folds').children.length,3);}
assert.deepEqual(errorsUI,[]);dom.window.close();
console.log('PASS cross-day metrics, exclusions, label isolation, chronological folds, retention, audit conservation, and interactive HTML');

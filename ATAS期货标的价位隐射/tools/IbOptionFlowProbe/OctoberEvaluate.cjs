'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const R=require('./CrossDayResearch.cjs'),Study=require('./OctoberOptimization.cjs'),Risk=require('./OctoberRisk.js');
const sum=a=>a.reduce((s,x)=>s+x,0),ba=c=>c[0]+c[1]>0?c[0]/(c[0]+c[1]):null,sha=b=>crypto.createHash('sha256').update(b).digest('hex');
function enrich(b){
 const ts=b.trades,v=sum(ts.map(t=>t.size)),directional=sum(b.counts.slice(0,2));
 const rate=fn=>v?sum(ts.filter(fn).map(t=>t.size))/v:0;
 return {...b.features,insideDirectional:rate(t=>t.category<2&&t.atQuoteCategory===3),
  ambiguousLarge:rate(t=>t.isLarge&&t.timelyNext&&t.category!==3&&t.nextCategory!==3&&t.category!==t.nextCategory),
  directionalConcentration:directional?Math.max(0,...ts.filter(t=>t.category<2).map(t=>t.size))/directional:null,
  largeUnconfirmed:rate(t=>t.isLarge&&(!t.timelyNext||t.nextCategory===3))};
}
function load(audit){
 const days=R.loadSources(Study.SOURCES),byKey=new Map(audit.buckets.map(b=>[b.day+'|'+b.key,b]));
 for(const d of days)assert.equal(d.sha256,audit.comparisonSources.find(s=>s.day===d.day).sha256,'Comparison changed since trade audit');
 const rows=R.observations(days).map(r=>{
  const b=byKey.get(r.day+'|'+r.key),d=days.find(d=>d.day===r.day),p=d.pairs.find(p=>p.key===r.key);assert.deepEqual(r.counts[R.BASE],b.counts);
  return {...r,uwAfterEnd:p.uwAfterEnd,features:{...r.features,...enrich(b)},largest:b.largest,
   counts:{...r.counts,...Object.fromEntries(b.staleModels.map(m=>[`StaleNext/${m.mode}/${m.ageMs}`,m.counts]))}};
 });
 assert.equal(rows.length,byKey.size);return {days,rows};
}
function stats(rows,p){
 const kept=rows.filter(r=>Risk.assess(r.features,p).keep),removed=rows.filter(r=>!Risk.assess(r.features,p).keep),before=R.metric(rows),after=R.metric(kept),rejected=R.metric(removed);
 return {before,after,rejected,volumeRetention:before.ibVolume?after.ibVolume/before.ibVolume:null,bucketRetention:rows.length?kept.length/rows.length:null,removedKeys:removed.map(r=>r.day+'|'+r.key)};
}
// Every training day must retain >=80% of incumbent volume and >=70% of incumbent buckets.
// All feature families are exploratory; date splits protect label fitting only.
function fit(rows,candidates=Risk.candidates()){
 const days=[...new Set(rows.map(r=>r.day))];assert.ok(days.length);
 const fits=candidates.map(policy=>{
  const training=days.map(day=>{const rs=rows.filter(r=>r.day===day);return {day,incumbent:stats(rs,candidates[0]),candidate:stats(rs,policy)};});
  const viable=training.every(s=>s.candidate.after.n>=Math.max(10,.7*s.incumbent.after.n)&&s.candidate.after.ibVolume>=.8*s.incumbent.after.ibVolume&&s.candidate.after.missing===0);
  if(!viable)return null;
  const loss=sum(training.map(s=>s.candidate.after.mean+.25*s.candidate.after.p95+.1*s.candidate.after.bad10/s.candidate.after.valid))/days.length;
  return {policy,loss,training};
 }).filter(Boolean).sort((a,b)=>a.loss-b.loss||a.policy.rules.length-b.policy.rules.length||a.policy.id.localeCompare(b.policy.id));
 assert.ok(fits.length);return fits[0];
}
function evaluate(rows,days){
 const policies=Risk.candidates(),incumbent=policies[0],dates=days.map(d=>d.day),base=stats(rows,incumbent),fixed=fit(rows),kept=rows.filter(r=>Risk.assess(r.features,incumbent).keep);
 const folds=dates.map(day=>{const trained=fit(rows.filter(r=>r.day!==day));return {day,trainDays:dates.filter(d=>d!==day),policy:trained.policy,loss:trained.loss,test:stats(rows.filter(r=>r.day===day),trained.policy),incumbent:stats(rows.filter(r=>r.day===day),incumbent)};});
 const forward=dates.slice(1).map(day=>{const trained=fit(rows.filter(r=>r.day<day));return {day,trainDays:dates.filter(d=>d<day),policy:trained.policy,loss:trained.loss,test:stats(rows.filter(r=>r.day===day),trained.policy),incumbent:stats(rows.filter(r=>r.day===day),incumbent)};});
 const foldMetrics=fs=>{const eligible=rows.filter(r=>fs.some(f=>f.day===r.day)),retained=eligible.filter(r=>Risk.assess(r.features,fs.find(f=>f.day===r.day).policy).keep);return {before:R.metric(eligible),incumbent:R.metric(eligible.filter(r=>Risk.assess(r.features,incumbent).keep)),after:R.metric(retained),volumeRetention:R.metric(retained).ibVolume/R.metric(eligible).ibVolume};};
 const policyResults=policies.map(policy=>({policy,all:stats(rows,policy),days:dates.map(day=>({day,...stats(rows.filter(r=>r.day===day),policy)}))}));
 // Compare classifications on IDENTICAL buckets; do not let unknown values disappear as zero error.
 const ids=Object.keys(rows[0].counts),models=ids.map(id=>({id,all:R.metric(rows,id),sameIncumbentBuckets:R.metric(kept,id),days:dates.map(day=>({day,...R.metric(rows.filter(r=>r.day===day),id)}))}));
 const modelFolds=dates.map(day=>{const train=rows.filter(r=>r.day!==day),selected=R.fitConfig(train,ids);return {day,trainDays:dates.filter(d=>d!==day),id:selected.id,test:R.metric(rows.filter(r=>r.day===day),selected.id),baseline:R.metric(rows.filter(r=>r.day===day))};});
 const error=r=>{const b=ba(r.counts[R.BASE]),u=ba(r.uw);return b===null||u===null?null:Math.abs(b-u);};
 const bad=kept.filter(r=>error(r)>.1),good=kept.filter(r=>error(r)!==null&&error(r)<=.1);
 const med=(rs,f)=>{const a=rs.map(r=>r.features[f]).filter(Number.isFinite).sort((a,b)=>a-b);return a.length?(a[Math.floor((a.length-1)/2)]+a[Math.floor(a.length/2)])/2:null;};
 const associations=Object.keys(Risk.LABELS).map(feature=>({feature,badMedian:med(bad,feature),otherMedian:med(good,feature)}));
 const details=rows.map(r=>({...r,error:error(r),ibBuyShare:ba(r.counts[R.BASE]),uwBuyShare:ba(r.uw),baseDecision:Risk.assess(r.features,incumbent),candidateDecision:Risk.assess(r.features,fixed.policy),foldDecision:Risk.assess(r.features,folds.find(f=>f.day===r.day).policy),volumeRatio:sum(r.counts[R.BASE])/r.uwVolume,uwMulti:Number.parseFloat(r.multi)/100}));
 const externalDiagnostics={note:'Only for error investigation. Never passed to risk predicates.',bad:bad.length,other:good.length,volumeMismatch:details.filter(r=>r.baseDecision.keep&&Math.abs(r.volumeRatio-1)>.05).map(r=>({day:r.day,label:r.label,error:r.error,volumeRatio:r.volumeRatio})),postEnd:stats(rows.filter(r=>r.uwAfterEnd),fixed.policy),noInferred:stats(rows.filter(r=>!r.uwInferred),fixed.policy)};
 return {version:1,createdUtc:new Date().toISOString(),dates,protocol:{baseline:R.BASE,incumbent:'methodGap <= .01',candidateCount:policies.length,classifierCount:ids.length,selectionObjective:'equal-day mean + .25 P95 + .1 proportion(error>.1)',constraints:'each training day >=80% incumbent IB volume and >=70% incumbent buckets; >=10 valid buckets',validation:'leave-one-day-out + chronological expanding window; ALL dates already inspected, so exploratory not pristine external validation',liveInputs:'IB only; UW labels, UW multi and UW/IB volume difference are diagnostic only',timing:'conditional evidence available after at most 50ms; not receipt-instant realtime',sourceDates:Study.SOURCES},
  base,fixed:{policy:fixed.policy,trainingLoss:fixed.loss,all:stats(rows,fixed.policy)},folds,forward,crossfit:foldMetrics(folds),walkForward:foldMetrics(forward),policyResults,models,modelFolds,associations,externalDiagnostics,rows:details};
}
function run(){const file=path.join(Study.OUT,'trade-audit.json'),bytes=fs.readFileSync(file),audit=JSON.parse(bytes),{days,rows}=load(audit),result=evaluate(rows,days);
 // Backward compatibility only: September is NOT used in candidate fitting and is NOT a fresh holdout.
 const prior=R.loadSources();result.priorDateRegression=prior.map(day=>{const rs=R.observations([day]);return {day:day.day,sourceSha256:day.sha256,incumbent:stats(rs,Risk.candidates()[0]),candidate:stats(rs,result.fixed.policy)};});
 result.provenance={auditSha256:sha(bytes),comparisonSources:audit.comparisonSources,analysisResolution:audit.analysisResolution,codeSha256:sha(fs.readFileSync(__filename)),riskSha256:sha(fs.readFileSync(path.join(__dirname,'OctoberRisk.js')))};fs.writeFileSync(path.join(Study.OUT,'optimization.json'),JSON.stringify(result,null,2));console.log(JSON.stringify({baseline:result.base,fixed:result.fixed,crossfit:result.crossfit,walkForward:result.walkForward,folds:result.folds.map(f=>({day:f.day,policy:f.policy.id,mean:f.test.after.mean,bad:f.test.after.bad10,max:f.test.after.max,volume:f.test.volumeRetention})),models:result.models.filter(m=>m.all.missing===0&&m.all.coverage>=.7).sort((a,b)=>a.all.mean-b.all.mean).slice(0,8)},null,2));return result;}
if(require.main===module)run();
module.exports={load,enrich,stats,fit,evaluate,run};

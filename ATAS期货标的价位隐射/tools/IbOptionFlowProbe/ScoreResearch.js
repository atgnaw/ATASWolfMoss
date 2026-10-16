// Same deterministic implementation in Node and the offline browser report.
(function(root,factory){const api=factory();if(typeof module==='object'&&module.exports)module.exports=api;else root.ScoreResearch=api;})(typeof globalThis!=='undefined'?globalThis:this,()=>{
 'use strict';
 const sum=a=>a.reduce((s,v)=>s+v,0);
 function score(counts,volumes={},events={},mergeScore={}){
  const total=sum(counts),agree=volumes.LARGE_AGREE||0,allMerged=volumes.LARGE_MERGED||0;
  if(allMerged&&((mergeScore.changedVolume||0)+(mergeScore.unchangedVolume||0)!==allMerged))throw Error('Missing or inconsistent merged classification evidence');
  const merged=mergeScore.changedVolume||0,unchanged=mergeScore.unchangedVolume||0,assessed=agree+merged;
  if(assessed>total+1e-8)throw Error('Scored volume exceeds observed volume');
  const large=sum(Object.entries(volumes).filter(([key])=>key.startsWith('LARGE_')).map(([,v])=>v));
  const noLarge=total>0&&large===0&&volumes.SMALL_LATEST===total;
  const timeoutOnly=total>0&&large>0&&large===volumes.LARGE_TIMEOUT&&((volumes.SMALL_LATEST||0)+large)===total;
  const neutralOnly=total>0&&!noLarge&&assessed===0&&((volumes.SMALL_LATEST||0)+(volumes.LARGE_TIMEOUT||0)+unchanged)===total;
  return {value:noLarge||neutralOnly?0:total&&assessed?100*(agree-merged)/total:null,noLarge,timeoutOnly,neutralOnly,large,unchanged,coverage:total?assessed/total:0,
   events:(events.LARGE_AGREE||0)+(mergeScore.changedEvents||0),agree,merged,neutral:total-assessed,total};
 }
 function pass(s,{enabled=false,minimum=0,coverage=.1,events=3}={}){
  return !enabled||(s.value!==null&&s.value>=minimum&&(s.noLarge||(s.coverage>=coverage&&s.events>=events)));
 }
 function metric(counts,uw){
  const v=sum(counts),u=sum(uw),b=counts[0]+counts[1],ub=uw[0]+uw[1];
  return {tv:v&&u?sum(counts.map((x,i)=>Math.abs(x/v-uw[i]/u)))/2:null,
   ba:b&&ub?Math.abs(counts[0]/b-uw[0]/ub):null,
   net:v&&u?Math.abs((counts[0]-counts[1])/v-(uw[0]-uw[1])/u):null,
   ib:v,uw:u,directional:b};
 }
 function summary(pairs,config,indices){
  const rows=indices.map(i=>metric(config.rows[i],pairs[i].uw));
  const result={buckets:indices.length,ibVolume:sum(rows.map(r=>r.ib)),uwVolume:sum(rows.map(r=>r.uw))};
  result.directionalCoverage=result.ibVolume?sum(rows.map(r=>r.directional))/result.ibVolume:null;
  for(const key of ['tv','ba','net']){
   const valid=rows.filter(r=>r[key]!==null),weight=sum(valid.map(r=>r.uw)),sorted=valid.map(r=>r[key]).sort((a,b)=>a-b);
   result[key]={valid:valid.length,missing:rows.length-valid.length,
    mean:weight?sum(valid.map(r=>r[key]*r.uw))/weight:null,
    max:sorted.length?sorted.at(-1):null,p95:sorted.length?sorted[Math.ceil(sorted.length*.95)-1]:null};
  }
  return result;
 }
 const bins=[{name:'无大单 · 基础 0 分',test:s=>s.noLarge},
  {name:'大单超时或合并未改变分类 · 0 分',test:s=>s.neutralOnly},
  {name:'无评分（大单无加减分依据或数据不足）',test:s=>s.value===null},
  {name:'低于 −20',test:s=>s.value!==null&&s.value< -20},
  {name:'−20 至小于 0',test:s=>s.value!==null&&s.value>=-20&&s.value<0},
  {name:'0 至小于 20（有加减分依据）',test:s=>!s.noLarge&&!s.neutralOnly&&s.value!==null&&s.value>=0&&s.value<20},
  {name:'20 至 100',test:s=>s.value!==null&&s.value>=20}];
 function study(pairs,config,baseline,indices){
  const scores=pairs.map((p,i)=>score(config.rows[i],config.waits[i],config.waitCounts?.[i],config.mergeScores?.[i]));
  return bins.map(bin=>{const selected=indices.filter(i=>bin.test(scores[i]));return {name:bin.name,
   conditional:summary(pairs,config,selected),baseline:summary(pairs,baseline,selected)};});
 }
 return {score,pass,metric,summary,study};
});

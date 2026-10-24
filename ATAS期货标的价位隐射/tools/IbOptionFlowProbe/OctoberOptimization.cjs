'use strict';
// Five NEW dates, kept separate from the original September research sample.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const R=require('./CrossDayResearch.cjs'),Audit=require('./ResearchTradeAudit.cjs');
const ROOT=path.resolve(__dirname,'../..'),OUT=path.join(ROOT,'docs/reports/uw-ib-october-optimization');
const SOURCES=[['2026-10-01','20261001'],['2026-10-20','20261020/repaired'],['2026-10-21','20261021/repaired'],['2026-10-22','20261022/corrected-time'],['2026-10-23','20261023']].map(([day,dir])=>[day,`docs/reports/uw-ib-${dir}/comparison.html`]);
async function audit(){
 const days=R.loadSources(SOURCES),resolved=[];
 // User moved the repaired analysis folders. Resolve by immutable run ID, not parent directory name.
 const available=days.flatMap(d=>fs.readdirSync(d.result.run).filter(n=>n.startsWith('analysis-')).map(n=>path.join(d.result.run,n)));
 for(const d of days){
  const original=d.result.analysis;
  if(!fs.existsSync(original)){
   const runId=JSON.parse(fs.readFileSync(path.join(d.result.run,'capture-summary.json'),'utf8')).run_id;
   const matches=available.filter(a=>JSON.parse(fs.readFileSync(path.join(a,'report.json'),'utf8')).run_id===runId);
   assert.equal(matches.length,1,`Ambiguous analysis relocation for ${d.day}`);d.result.analysis=matches[0];
  }
  resolved.push({day:d.day,original,resolved:d.result.analysis});assert.ok(d.pairs.every(p=>p.t!==d.result.windowDetails.finalStart));
 }
 const result=await Audit.build(days,{includeTrades:true});
 result.comparisonSources=days.map(d=>({day:d.day,file:d.file,sha256:d.sha256}));
 result.analysisResolution=resolved;
 fs.mkdirSync(OUT,{recursive:true});fs.writeFileSync(path.join(OUT,'trade-audit.json'),JSON.stringify(result));
 console.log('AUDIT COMPLETE',result.buckets.length,'buckets',result.buckets.reduce((s,b)=>s+b.trades.length,0),'callbacks');
}
if(require.main===module){assert.equal(process.argv[2],'audit','Usage: OctoberOptimization.cjs audit');audit().catch(e=>{console.error(e);process.exitCode=1;});}
module.exports={SOURCES,OUT,audit};

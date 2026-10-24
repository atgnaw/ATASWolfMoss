'use strict';
// Frozen prospective validation: rejects the research dates and never fits thresholds.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const R=require('./CrossDayResearch.cjs'),Risk=require('./OctoberRisk.js'),Study=require('./OctoberOptimization.cjs'),Eval=require('./OctoberEvaluate.cjs');
const sha=b=>crypto.createHash('sha256').update(b).digest('hex');
function evaluate(d,spec){
 assert.equal(spec.version,2);assert.equal(spec.classification,R.BASE);assert.equal(spec.volumeUnit,'CONTRACTS');assert.equal(spec.bucketMinutes,5);
 assert.ok(spec.policy.rules.every(r=>['methodGap','conditionalGap','outsideGap'].includes(r.feature)),'Unsupported future-validation feature');
 const date=d.result.windowDetails.date;assert.ok(spec.researchDates.every(day=>date>day),'Use a genuinely later trading date; no research-date revalidation');
 const nyDate=t=>new Intl.DateTimeFormat('en-CA',{timeZone:'America/New_York',year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date(t));
 assert.ok(d.pairs.every(p=>nyDate(p.t)===date),'Bucket timestamps must match trading date');
 const configs=new Map(d.configs.map(c=>[c.id,c])),day={day:date,configs,pairs:d.pairs},rows=R.observations([day]);
 assert.equal(new Set(d.pairs.map(p=>p.key)).size,d.pairs.length);
 assert.ok(d.pairs.every(p=>p.t>=d.result.windowDetails.first&&p.t+300000<=d.result.windowDetails.last&&p.t!==d.result.windowDetails.finalStart));
 const incumbent=Risk.candidates()[0];return {version:1,date,spec,window:d.result.windowDetails,excluded:d.result.excluded,incumbent:Eval.stats(rows,incumbent),candidate:Eval.stats(rows,spec.policy),rows:rows.map(r=>({...r,decision:Risk.assess(r.features,spec.policy)}))};
}
function run(file,out,specFile=path.join(Study.OUT,'candidate-v2.json')){
 const comparison=fs.readFileSync(file),specBytes=fs.readFileSync(specFile),m=comparison.toString().match(/<script id="data" type="application\/json">([\s\S]*?)<\/script>/);assert.ok(m);
 const result=evaluate(JSON.parse(m[1]),JSON.parse(specBytes));result.provenance={comparisonSha256:sha(comparison),specSha256:sha(specBytes),comparisonFile:path.resolve(file),codeSha256:sha(fs.readFileSync(__filename))};
 fs.mkdirSync(out,{recursive:true});const target=path.join(out,'candidate-v2-validation.json');assert.ok(!fs.existsSync(target),'Do not overwrite earlier validation');fs.writeFileSync(target,JSON.stringify(result,null,2));console.log(JSON.stringify({date:result.date,incumbent:result.incumbent.after,candidate:result.candidate.after,retention:result.candidate.volumeRetention},null,2));return result;
}
if(require.main===module){const [file,out,spec]=process.argv.slice(2);assert.ok(file&&out,'Usage: ValidateOctoberCandidate.cjs comparison.html new-output-dir [spec.json]');run(file,out,spec);}
module.exports={evaluate,run};

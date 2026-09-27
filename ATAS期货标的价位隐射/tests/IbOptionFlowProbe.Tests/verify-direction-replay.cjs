// Usage: node verify-direction-replay.cjs <new analysis directory> <old report.json>
const fs=require('node:fs'),path=require('node:path'),readline=require('node:readline'),assert=require('node:assert/strict');
const dir=process.argv[2],fresh=JSON.parse(fs.readFileSync(path.join(dir,'report.json'),'utf8'));
const old=JSON.parse(fs.readFileSync(process.argv[3],'utf8'));
assert.deepEqual(fresh.totals,old.totals);assert.deepEqual(fresh.buckets,old.buckets);
const legacy=[...fresh.totals,...fresh.buckets],rows=[...fresh.robustness.totals,...fresh.robustness.buckets];
const key=r=>JSON.stringify([r.contract.con_id,r.scope,r.session,r.interval_minutes,r.bucket_start_utc??r.start_utc??null]);
const index=new Map(legacy.map(r=>[key(r),r]));
const close=(a,b)=>assert.ok(Math.abs(a-b)<=Math.max(1e-6,Math.abs(b)*1e-12),`${a} != ${b}`);
const known=new Set(fresh.robustness.reason_catalog.map(r=>r.key));
for(const r of rows){const original=index.get(key(r));assert.ok(original);close(r.primary.metrics.total,original.observed_premium);close(r.primary.buy+r.primary.sell+r.primary.unknown,original.observed_premium);
 for(const s of [r.primary,...Object.values(r.scenarios),...Object.values(r.consensus)]){close(s.metrics.total,original.observed_premium);close(Object.values(s.unknown_premiums).reduce((sum,x)=>sum+x,0),s.unknown);assert.ok(Object.keys(s.unknown_premiums).every(k=>known.has(k)));
 const groups=Object.values(s.unresolved_groups);close(groups.reduce((sum,g)=>sum+g.premium,0),s.unknown_premiums.UNRESOLVED_CANDIDATE||0);assert.equal(groups.reduce((sum,g)=>sum+g.events,0),s.unknown_counts.UNRESOLVED_CANDIDATE||0);
 for(const g of groups){assert.ok(g.reasons.every(k=>known.has(k)));assert.equal(new Set(g.reasons).size,g.reasons.length);assert.ok(g.examples.length<=2);for(const e of g.examples)for(const c of e.candidates){if(c.bid_sequence!=null)assert.ok(c.bid_sequence<e.sequence);if(c.ask_sequence!=null)assert.ok(c.ask_sequence<e.sequence);}}
 }
 for(const v of Object.values(r.time_conflict_premiums))assert.ok(v>=0 && v<=r.primary.metrics.total+1e-6);
 if(r.consensus.Midpoint){close(r.consensus.Midpoint.buy,r.primary.buy);close(r.consensus.Midpoint.sell,r.primary.sell);close(r.consensus.Midpoint.unknown,r.primary.unknown);}
}
(async()=>{let count=0;for(const file of fs.readdirSync(dir).filter(f=>/^decisions-.*jsonl$/.test(f)).sort()){
 const reader=readline.createInterface({input:fs.createReadStream(path.join(dir,file)),crlfDelay:Infinity});
 for await(const line of reader){const d=JSON.parse(line),e=d.direction_evidence;assert.equal(d.schema_version,3);assert.ok(e);count++;
  if(d.disposition!=='ACCEPTED'){assert.equal(e.matches.length,0);continue;}
  assert.equal(e.matches.length,36);assert.equal(e.alignments.length,6);assert.equal(e.consensus.length,3);
  for(const a of e.alignments){if(a.quote){for(const q of [a.quote.bid,a.quote.ask])if(q)assert.ok(q.sequence<d.sequence);}
   if(a.error==='FUTURE_CANDIDATE'){assert.equal(a.feasible,false);assert.ok(e.matches.filter(m=>m.alignment_id===a.id).every(m=>m.sign===0));}}
 }}assert.equal(count,fresh.trade_callbacks);console.log(JSON.stringify({legacyTotalsAndBucketsUnchanged:true,bucketMassConserved:rows.length,evidenceRecords:count,noFutureQuoteReferences:true},null,2));})().catch(e=>{console.error(e);process.exitCode=1;});

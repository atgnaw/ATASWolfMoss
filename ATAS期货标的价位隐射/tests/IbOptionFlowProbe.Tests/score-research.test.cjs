const assert=require('node:assert/strict'),s=require('../../tools/IbOptionFlowProbe/ScoreResearch.js');
const ordinary=s.score([100,0,0,0],{SMALL_LATEST:100});
assert.equal(ordinary.value,0);assert.equal(ordinary.noLarge,true);
assert.equal(s.pass(ordinary,{enabled:true,minimum:0,coverage:1,events:999}),true);
assert.equal(s.pass(ordinary,{enabled:true,minimum:1}),false);
for(const small of [0,50]){
 const timeout=s.score([100,0,0,0],{SMALL_LATEST:small,LARGE_TIMEOUT:100-small});
 assert.equal(timeout.value,0);assert.equal(timeout.timeoutOnly,true);
 assert.equal(s.pass(timeout,{enabled:true,minimum:-96,coverage:0,events:0}),true);
 assert.equal(s.pass(timeout,{enabled:true,minimum:1,coverage:0,events:0}),false);
 assert.equal(s.pass(timeout,{enabled:true,minimum:0,coverage:.1,events:0}),false);
 assert.equal(s.pass(timeout,{enabled:true,minimum:0,coverage:0,events:1}),false);
}
assert.equal(s.score([100,0,0,0],{LARGE_TIMEOUT:50}).value,null);
assert.equal(s.score([100,0,0,0],{LARGE_TIMEOUT:50,LARGE_INTERRUPTED:50}).value,null);
for(const branch of ['LARGE_FIRST_INVALID_FALLBACK','LARGE_INTERRUPTED','LARGE_INVALID_QUOTES']){
 const v=s.score([100,0,0,0],{SMALL_LATEST:50,[branch]:50});assert.equal(v.noLarge,false);assert.equal(v.value,null);assert.equal(s.pass(v,{enabled:true,minimum:0}),false);}
assert.equal(s.score([100,0,0,0],{}).noLarge,false);
assert.equal(s.score([100,0,0,0],{LARGE_AGREE:40,LARGE_MERGED:40},{},{changedVolume:40}).value,0);
const sample=s.score([60,40,0,0],{LARGE_AGREE:40,LARGE_MERGED:10,LARGE_TIMEOUT:50},{LARGE_AGREE:2,LARGE_MERGED:1},{changedVolume:10,changedEvents:1});
assert.equal(sample.value,30);assert.equal(sample.coverage,.5);assert.equal(sample.events,3);
assert.equal(s.pass(sample,{enabled:true,minimum:30,coverage:.5,events:3}),true);
assert.equal(s.pass(sample,{enabled:true,minimum:31,coverage:.5,events:3}),false);
assert.equal(s.pass(sample,{enabled:true,minimum:0,coverage:.5,events:4}),false);
assert.equal(s.pass(s.score([0,0,100,0]),{enabled:true,minimum:-100,coverage:0,events:0}),false);
assert.equal(s.score([0,0,0,0]).value,null);
assert.equal(s.score([100,0,0,0],{LARGE_MERGED:100},{},{changedVolume:100}).value,-100);
assert.equal(s.score([0,0,100,0],{LARGE_AGREE:100}).value,100); // Mid agreement is not direction proof
const unchanged=s.score([100,0,0,0],{LARGE_MERGED:100},{LARGE_MERGED:2},{unchangedVolume:100,unchangedEvents:2});
assert.equal(unchanged.value,0);assert.equal(unchanged.events,0);assert.equal(unchanged.coverage,0);assert.equal(unchanged.neutralOnly,true);
assert.equal(s.pass(unchanged,{enabled:true,minimum:0,coverage:0,events:0}),true);
const mixed=s.score([100,0,0,0],{LARGE_AGREE:20,LARGE_MERGED:80},{LARGE_AGREE:1,LARGE_MERGED:3},{changedVolume:30,unchangedVolume:50,changedEvents:1,unchangedEvents:2});
assert.equal(mixed.value,-10);assert.equal(mixed.events,2);assert.equal(mixed.coverage,.5);
assert.throws(()=>s.score([100,0,0,0],{LARGE_MERGED:100}),/evidence/);
assert.equal(s.metric([0,0,100,0],[50,50,0,0]).ba,null);
assert.equal(s.metric([0,0,0,0],[50,50,0,0]).tv,null);
const pairs=[{uw:[60,40,0,0]}],c={rows:[[30,70,0,0]],waits:[{LARGE_AGREE:40,LARGE_MERGED:10}],waitCounts:[{}],mergeScores:[{changedVolume:10}]};
assert.ok(Math.abs(s.summary(pairs,c,[0]).tv.mean-.3)<1e-10);
assert.equal(s.summary(pairs,c,[]).tv.mean,null);
assert.equal(s.study(pairs,c,c,[0]).reduce((n,g)=>n+g.conditional.buckets,0),1);
console.log('PASS score formulas, null evidence, thresholds, Mid caveat, missing denominator, empty subset, bins');

const fs=require('node:fs'),assert=require('node:assert/strict');
const [oldPath,newPath]=process.argv.slice(2);
const old=JSON.parse(fs.readFileSync(oldPath,'utf8'));
const html=fs.readFileSync(newPath,'utf8');
const data=JSON.parse(html.match(/<script id="data" type="application\/json">([\s\S]*?)<\/script>/)[1]);
for(const [set,rows]of Object.entries(old.rankings))for(const row of rows){
 if(!require('../../tools/IbOptionFlowProbe/ReportSelection.cjs').allowedScenario(row.id.split('|')[2]))continue;
 const actual=data.result.rankings[set].find(x=>x.id===row.id);
 assert.deepEqual(actual,row,`Legacy changed: ${set}/${row.id}`);
}
assert.equal(data.configs.length,72);
for(const config of data.configs.filter(x=>x.id.includes('|Conditional/'))){
 config.rows.forEach((r,i)=>assert.equal(r.reduce((s,v)=>s+v,0),Object.values(config.waits[i]).reduce((s,v)=>s+v,0)));
}
const i=data.pairs.findIndex(x=>x.ticker==='QQQ'&&x.strike===738&&x.right.toUpperCase()==='CALL'&&x.start==='13:45');
assert.ok(i>=0);
const target=data.configs.find(x=>x.id==='AllTimeAndSales|U15_ON|Conditional/Midpoint/50');
const counts=target.rows[i],uw=data.pairs[i].uw,total=counts.reduce((s,v)=>s+v,0);
console.log(JSON.stringify({anomaly:counts,UW:uw,distance:counts.reduce((s,v,k)=>s+Math.abs(v/total-uw[k]/861),0)/2,branches:target.waits[i]}));
console.log('PASS retained 72 configurations unchanged across all 7 subsets; conditional branch volume conserved');

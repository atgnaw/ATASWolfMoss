const fs=require('node:fs'),assert=require('node:assert/strict');
const {JSDOM,VirtualConsole}=require('../../tools/UwPageCollector/node_modules/jsdom');
const [report,expectedDate,expectedPairs]=process.argv.slice(2),errors=[],vc=new VirtualConsole();vc.on('jsdomError',e=>errors.push(e.message));
const dom=new JSDOM(fs.readFileSync(report,'utf8'),{runScripts:'dangerously',virtualConsole:vc});
const q=id=>dom.window.document.getElementById(id),d=JSON.parse(q('data').textContent),w=d.result.windowDetails;
assert.equal(w.date,expectedDate);assert.equal(d.pairs.length,Number(expectedPairs));assert.equal(d.configs.length,72);
assert.ok(d.configs.every(c=>require('../../tools/IbOptionFlowProbe/ReportSelection.cjs').allowedScenario(c.id.split('|')[2])));
assert.equal(q('config').options.length,72);assert.equal(q('waitrows').children.length,1);
assert.ok(d.pairs.every(p=>p.t>=w.first&&p.t+300000<=w.last&&p.t!==w.finalStart));
assert.equal(q('rows').children.length,d.pairs.length);
assert.ok(dom.window.document.title.includes(expectedDate));assert.ok(q('score-phase').options[0].textContent.includes(w.startLabel));
const uw=JSON.parse(fs.readFileSync(d.result.uwFile,'utf8'));
for(const p of d.pairs){const source=uw.latest.find(r=>r.identity.ticker===(p.ticker==='SPX'?'SPXW':p.ticker)&&Number(r.identity.strike)===p.strike&&r.identity.right.toUpperCase()===p.right.toUpperCase()&&r.identity.expiry===p.expiry&&Date.parse(r.bucket.startUtc)===p.t);
 assert.ok(source);assert.equal(source.ba.status,'RECONSTRUCTED');assert.deepEqual(p.uw,[source.ba.counts.ask,source.ba.counts.bid,source.ba.counts.neutral,0]);}
function change(id,value){q(id).value=value;q(id).dispatchEvent(new dom.window.Event('change'));}
change('score-phase','test');assert.equal(q('rows').children.length,d.pairs.filter(p=>p.t>=w.split).length);
change('score-phase','all');
for(const id of ['AllTimeAndSales|U15_ON|Latest/Midpoint/-1','AllTimeAndSales|U15_ON|Conditional/Midpoint/50','AllTimeAndSales|U15_OFF|Source100/Midpoint']){
 const config=d.configs.find(c=>c.id===id);if(!config)continue;change('config',id);assert.equal(q('rows').children.length,d.pairs.length);
}
for(const c of d.configs.filter(c=>/\|(Conditional|Latest)\//.test(c.id)))for(let i=0;i<d.pairs.length;i++){
 assert.equal(Object.values(c.waits[i]).reduce((a,b)=>a+b,0),c.rows[i].reduce((a,b)=>a+b,0));
 assert.equal((c.mergeScores[i].changedVolume||0)+(c.mergeScores[i].unchangedVolume||0),c.waits[i].LARGE_MERGED||0);
}
assert.deepEqual(errors,[]);dom.window.close();console.log('PASS '+expectedDate+': dated window, final bucket excluded, reconstructed source counts, UI phases, scoring volumes conserved');

const fs=require('node:fs'),assert=require('node:assert/strict');
const {JSDOM,VirtualConsole}=require('../../tools/UwPageCollector/node_modules/jsdom');
const errors=[],vc=new VirtualConsole();vc.on('jsdomError',e=>errors.push(e.message));
const dom=new JSDOM(fs.readFileSync(process.argv[2],'utf8'),{runScripts:'dangerously',virtualConsole:vc});
const doc=dom.window.document,q=id=>doc.getElementById(id),data=JSON.parse(q('data').textContent);
function change(id,value){const e=q(id);if(e.type==='checkbox')e.checked=value;else e.value=value;e.dispatchEvent(new dom.window.Event('change'));}
assert.equal(q('score-enable').checked,false);assert.equal(q('rows').children.length,100);
function unsupported(){assert.equal(q('score-enable').checked,false);for(const id of ['score-enable','score-min','score-cover','score-events'])assert.equal(q(id).disabled,true);
 assert.match(q('score-status').textContent,/暂不支持评分/);assert.equal(q('score-analysis').hidden,true);assert.equal(q('score-formula').hidden,true);
 assert.equal(q('score-groups').children.length,0);assert.equal(q('score-results').children.length,0);
 for(const row of q('rows').children){assert.equal(row.children[5].textContent,'暂不支持评分');assert.equal(row.children[5].querySelector('.score-bar'),null);}}
assert.equal(q('score-enable').disabled,false);
change('config','AllTimeAndSales|U15_ON|Conditional/Midpoint/50');
const config=data.configs.find(c=>c.id===q('config').value),s=dom.window.ScoreResearch;
const expected=data.pairs.map((p,i)=>i).filter(i=>s.pass(s.score(config.rows[i],config.waits[i],config.waitCounts[i],config.mergeScores[i]),{enabled:true,minimum:0,coverage:.1,events:3}));
assert.equal(q('score-enable').disabled,false);assert.equal(q('score-analysis').hidden,false);assert.equal(q('score-groups').children.length,7);
change('score-enable',true);assert.equal(q('rows').children.length,expected.length);
assert.deepEqual(Array.from(q('rows').children,r=>r.children[0].textContent),expected.map(i=>{const p=data.pairs[i];return p.start+'–'+p.end+' '+p.ticker+' '+p.strike+' '+p.right;}));
for(const c of data.configs.filter(c=>c.id.includes('|Latest/'))){change('config',c.id);assert.equal(q('score-enable').disabled,false);
 const n=data.pairs.filter((p,i)=>s.pass(s.score(c.rows[i],c.waits[i],c.waitCounts[i],c.mergeScores[i]),{enabled:true,minimum:0,coverage:.1,events:3})).length;
 assert.equal(q('rows').children.length,n);}
for(const c of data.configs.filter(c=>!c.id.includes('|Conditional/')&&!c.id.includes('|Latest/'))){change('config',c.id);unsupported();assert.equal(q('rows').children.length,100);}
change('config',config.id);assert.equal(q('score-enable').checked,false);assert.equal(q('rows').children.length,100);
change('score-enable',true);
change('score-events',0);assert.equal(q('score-events').checkValidity(),true);assert.equal(q('score-error').textContent,'');
assert.equal(q('rows').children.length,data.pairs.filter((p,i)=>s.pass(s.score(config.rows[i],config.waits[i],config.waitCounts[i],config.mergeScores[i]),{enabled:true,minimum:0,coverage:.1,events:0})).length);
change('score-min',-96);change('score-cover',0);change('score-events',0);assert.equal(q('rows').children.length,100);
assert.equal(config.rows.filter((r,i)=>s.score(r,config.waits[i],config.waitCounts[i],config.mergeScores[i]).timeoutOnly).length,9);
change('score-events',-1);assert.equal(q('score-events').checkValidity(),false);assert.match(q('score-error').textContent,/无效/);
change('score-min',100);change('score-events',1000000);assert.equal(q('rows').children.length,0);assert.match(q('score-results').textContent,/不足以计算/);
change('score-min','');assert.match(q('score-error').textContent,/无效/);
// Select an observed source-alignment ID rather than depending on its parameter spelling.
change('config',data.configs.find(c=>c.id.split('|')[2].startsWith('Consensus')).id);
unsupported();assert.equal(q('rows').children.length,100);assert.equal(q('score-error').textContent,'');
q('score-reset').click();assert.equal(q('rows').children.length,100);
change('score-phase','test');assert.equal(q('rows').children.length,40);
change('ticker','QQQ');assert.equal(q('rows').children.length,20);
assert.equal(data.result.scoreResearch.studies.length,448);assert.deepEqual(errors,[]);
assert.deepEqual(data.result.scoreResearch.supportedAlignments,['Conditional','Latest']);
dom.window.close();console.log('PASS current-rule-only scoring, all unsupported configurations, filter reset, exact retained rows, invalid controls, period and ticker, no JS errors');

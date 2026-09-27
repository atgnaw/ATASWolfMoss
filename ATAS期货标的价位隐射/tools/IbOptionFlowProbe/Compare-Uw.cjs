// Offline diagnostic only. Reuses versioned per-trade decisions; never connects to IB/UW.
const fs=require('node:fs'),path=require('node:path'),readline=require('node:readline'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const [run,uwFile,analysis,out]=process.argv.slice(2);
if(!out)throw Error('Usage: node Compare-Uw.cjs <run> <uw.json> <analysis> <new-output>');
fs.mkdirSync(out,{recursive:false});
const read=p=>JSON.parse(fs.readFileSync(p,'utf8'));
const uw=read(uwFile),contracts=read(path.join(run,'contracts.json')).map(x=>x.contract),summary=read(path.join(run,'capture-summary.json'));
const byId=new Map(contracts.map(c=>[c.con_id,c]));
const identity=(ticker,expiry,strike,right)=>[ticker==='SPXW'?'SPX':ticker,expiry,Number(strike),right.toUpperCase()].join('|');
const contractKeys=new Map(contracts.map(c=>[identity(c.ticker,c.expiration,c.strike_usd,c.right),c.con_id]));
const first=Date.parse('2026-09-23T17:30:00Z'),last=Date.parse('2026-09-23T17:55:00Z');
assert.equal(uw.run.config.timezone,'America/New_York');
assert.ok(Date.parse(summary.capture_started_utc)<=first && Date.parse(summary.ended_utc)>=last);
const pairKey=(conId,t)=>conId+'|'+t;
const pairs=[],excluded={outsideWindow:0,unsubscribed:0,invalidCounts:0};
for(const r of uw.latest){
 const t=Date.parse(r.bucket.startUtc),end=Date.parse(r.bucket.endUtc);
 if(t<first||end>last||end-t!==300000){excluded.outsideWindow++;continue;}
 const cid=contractKeys.get(identity(r.identity.ticker,r.identity.expiry,r.identity.strike,r.identity.right));
 if(!cid){excluded.unsubscribed++;continue;}
 const c=r.ba.counts;
 if(r.ba.status!=='RECONSTRUCTED'||!c||![c.bid,c.ask,c.neutral,r.volume].every(x=>Number.isSafeInteger(x)&&x>=0)||c.bid+c.ask+c.neutral!==r.volume||!r.volume){excluded.invalidCounts++;continue;}
 pairs.push({key:pairKey(cid,t),conId:cid,ticker:byId.get(cid).ticker,strike:Number(r.identity.strike),right:r.identity.right,expiry:r.identity.expiry,start:r.bucket.start,end:r.bucket.end,t,uw:[c.ask,c.bid,c.neutral,0],uwVolume:r.volume,uwInferred:!!r.ba.inference,uwAfterEnd:Date.parse(r.pageReadUtc||r.capturedUtc)>=end,uwCaptured:r.capturedUtc,uwRevision:r.revision,multi:r.raw.fields.find(x=>x.name==='% Multi')?.text});
}
pairs.sort((a,b)=>a.t-b.t||a.ticker.localeCompare(b.ticker)||a.strike-b.strike||a.right.localeCompare(b.right));
assert.equal(new Set(pairs.map(r=>r.key)).size,pairs.length);
const pairMap=new Map(pairs.map(x=>[x.key,x])),tradeBySequence=new Map(),configs=new Map(),dispositions={};
async function* lines(p){const stream=fs.createReadStream(p);for await(const s of readline.createInterface({input:stream,crlfDelay:Infinity}))if(s)yield JSON.parse(s);}
async function main(){
 for(const file of fs.readdirSync(run).filter(x=>/^events-.*\.jsonl$/.test(x)).sort()){
  for await(const e of lines(path.join(run,file))){
   const raw=e.raw;if(raw.kind!=='tickString'||![48,77].includes(raw.field)||!raw.contract)continue;
   const parts=raw.value.split(';'),t=Number(parts[2]),bucket=Math.floor(t/300000)*300000,key=pairKey(raw.contract.con_id,bucket);
   if(!pairMap.has(key))continue;
   const size=Number(parts[1]);if(!Number.isFinite(size)||size<0)continue;
   tradeBySequence.set(raw.sequence,{key,size,price:Number(parts[0]),field:raw.field});
  }
 }
 console.log('Raw trade callbacks retained for matched buckets:',tradeBySequence.size);
 for(const file of fs.readdirSync(analysis).filter(x=>/^decisions-.*\.jsonl$/.test(x)).sort()){
  for await(const d of lines(path.join(analysis,file))){
   const trade=tradeBySequence.get(d.sequence);if(!trade)continue;
   assert.equal(d.scope,trade.field===77?'RegularTrades':'AllTimeAndSales');
   const dk=d.scope+'/'+d.disposition;dispositions[dk]=(dispositions[dk]||0)+1;
   if(d.disposition!=='ACCEPTED')continue;
   for(const [outside,e] of [[false,d.direction_evidence],[true,d.outside_quote_evidence]]){
    assert.ok(e,'Missing dual-mode trace; regenerate 0.6 report first');
    for(const m of [...e.matches,...e.consensus.map(x=>({...x,scenario_id:'Consensus/'+x.scenario_id}))]){
     const id=d.scope+'|'+(outside?'U15_ON':'U15_OFF')+'|'+m.scenario_id;
     if(!configs.has(id))configs.set(id,{id,scope:d.scope,outside,scenario:m.scenario_id,rows:new Map()});
     const rows=configs.get(id).rows;if(!rows.has(trade.key))rows.set(trade.key,{counts:[0,0,0,0],events:0});
     const a=rows.get(trade.key);const category=m.sign>0?0:m.sign<0?1:['LOCKED_QUOTE','MIDPOINT_UNKNOWN','MID_TIME_AGREE'].includes(m.reason)?2:3;
     a.counts[category]+=trade.size;a.events++;
    }
   }
  }
  console.log('Processed',file);
 }
 const sum=a=>a.reduce((s,x)=>s+x,0),average=(a,w)=>sum(a.map((v,i)=>v*w[i]))/sum(w);
 function measurements(c,subset){return subset.map(p=>{
  const a=c.rows.get(p.key)||{counts:[0,0,0,0],events:0},v=sum(a.counts),ip=a.counts.map(n=>v?n/v:0),up=p.uw.map(n=>n/p.uwVolume);
  // No IB observed volume is NOT an all-zero directional distribution.
  if(!v)return {p,a,v,missing:true};
  const tv=sum(ip.map((x,i)=>Math.abs(x-up[i])))/2,ibNet=ip[0]-ip[1],uwNet=up[0]-up[1];
  return {p,a,v,ip,up,tv,netError:Math.abs(ibNet-uwNet),askError:Math.abs(ip[0]-up[0]),ibNet,uwNet,coverage:1-ip[3],directionalCoverage:ip[0]+ip[1],volumeRatio:v/p.uwVolume};
 });}
 function metric(c,subset){const ms=measurements(c,subset),valid=ms.filter(x=>!x.missing),weights=valid.map(x=>x.p.uwVolume);
  if(!valid.length)return {n:0,missing:ms.length};
  const n=valid.length,agree=valid.filter(x=>Math.sign(x.ibNet)===Math.sign(x.uwNet)).length;
  const iw=valid.map(x=>x.v),uvol=sum(weights),ivol=sum(iw),ic=[0,1,2,3].map(i=>sum(valid.map(x=>x.a.counts[i]))),uc=[0,1,2,3].map(i=>sum(valid.map(x=>x.p.uw[i])));
  return {n,missing:ms.length-n,uwVolume:uvol,ibVolume:ivol,volumeRatio:ivol/uvol,weightedTV:average(valid.map(x=>x.tv),weights),meanTV:sum(valid.map(x=>x.tv))/n,weightedNetMAE:average(valid.map(x=>x.netError),weights),weightedAskMAE:average(valid.map(x=>x.askError),weights),signAgreement:agree/n,signCount:agree,coverage:average(valid.map(x=>x.coverage),iw),directionalCoverage:average(valid.map(x=>x.directionalCoverage),iw),unknownFraction:ic[3]/ivol,midFraction:ic[2]/ivol,uwMidFraction:uc[2]/uvol,ibShares:ic.map(x=>x/ivol),uwShares:uc.map(x=>x/uvol),aggregateNet: (ic[0]-ic[1])/ivol,uwAggregateNet:(uc[0]-uc[1])/uvol};
 }
 const sets={all:pairs,QQQ:pairs.filter(x=>x.ticker==='QQQ'),SPX:pairs.filter(x=>x.ticker==='SPX'),postEnd:pairs.filter(x=>x.uwAfterEnd),noInferred:pairs.filter(x=>!x.uwInferred),train:pairs.filter(x=>x.t<first+900000),test:pairs.filter(x=>x.t>=first+900000)};
 const rankings={};for(const [name,subset]of Object.entries(sets))rankings[name]=[...configs.values()].map(c=>({id:c.id,...metric(c,subset)})).sort((a,b)=>(a.missing-b.missing)||(a.weightedTV-b.weightedTV));
 const best=rankings.all[0],selected=configs.get(best.id),bestRows=measurements(selected,pairs).map(x=>({...x.p,ib:x.a.counts,events:x.a.events,tv:x.tv,netError:x.netError,ibNet:x.ibNet,uwNet:x.uwNet,volumeRatio:x.volumeRatio}));
 const byBucket=[...new Set(pairs.map(x=>x.start))].map(start=>({start,...metric(selected,pairs.filter(x=>x.start===start))}));
 const winners={};for(const name of ['all','QQQ','SPX','postEnd','noInferred','train']){const id=rankings[name][0]?.id;if(id)winners[name]={id,all:metric(configs.get(id),pairs),train:metric(configs.get(id),sets.train),test:metric(configs.get(id),sets.test)};}
 const coverageByScope={};for(const scope of ['RegularTrades','AllTimeAndSales'])coverageByScope[scope]=metric([...configs.values()].find(c=>c.scope===scope),pairs);
 const result={createdUtc:new Date().toISOString(),uwFile,run,analysis,uwSha256:crypto.createHash('sha256').update(fs.readFileSync(uwFile)).digest('hex'),window:'2026-09-23 13:30–13:55 America/New_York',excluded,uwLatest:uw.latest.length,uwRevisions:uw.revisions.length,pairCount:pairs.length,possiblePairs:contracts.length*5,uwInferredPairs:pairs.filter(x=>x.uwInferred).length,uwPostEndPairs:sets.postEnd.length,dispositions,coverageByScope,rankings,winners,byBucket,bestRows};
 fs.writeFileSync(path.join(out,'comparison.json'),JSON.stringify(result,null,2));
 const exportConfigs=[...configs.values()].map(c=>({id:c.id,rows:pairs.map(p=>(c.rows.get(p.key)||{counts:[0,0,0,0]}).counts)}));
 fs.writeFileSync(path.join(out,'comparison.html'),html({pairs,configs:exportConfigs,best:best.id,result}));
 console.log(JSON.stringify({pairCount:result.pairCount,excluded,uwInferredPairs:result.uwInferredPairs,uwPostEndPairs:result.uwPostEndPairs,top:rankings.all.slice(0,5),QQQ:rankings.QQQ.slice(0,2),SPX:rankings.SPX.slice(0,2),winners},null,2));
}
function html(payload){return `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>9/23 IB 与 UW 对照</title><style>body{font:15px system-ui;background:#f4f7fa;color:#20394b;margin:30px}main{max-width:1500px;margin:auto}select{padding:10px;max-width:95%;margin:8px}table{width:100%;border-collapse:collapse;background:white}td,th{padding:12px;border-bottom:1px solid #dde5ec;text-align:left}th{background:#e7eff5}.bar{display:flex;width:260px;height:16px;border-radius:4px;overflow:hidden;margin:5px 0}.buy{background:#2db494}.sell{background:#ef4966}.mid{background:#8175b5}.unknown{background:#b3bcc6}small{color:#688095}section{background:white;border-radius:12px;padding:20px;margin:16px 0}.warning{background:#fff2d9;padding:16px}strong{font-size:20px}</style><main><h1>IB × UW · 2026/09/23</h1><p>纽约时间 13:30–13:55；排除 13:55–14:00 和 IB 未完整覆盖的早期桶。按成交数量，不按权利金比较。</p><p class="warning">UW 是页面比例还原的参考，不是主动买卖真值。最接近配置由本次样本选出，不代表未来准确率。未知不并入 Mid；无成交数据不当作零方向。</p><section><label>IB 配置<select id="config"></select></label><label>品种<select id="ticker"><option>全部</option><option>QQQ</option><option>SPX</option></select></label><p id="stats"></p><small>颜色：绿 Ask/买 · 红 Bid/卖 · 紫 Neutral/Mid · 灰 未知。行距离 = 四类成交量占比分布的总变差距离（越小越近）；不等于逐笔准确率。</small></section><table><thead><tr><th>时间 / 合约</th><th>UW 数量与占比</th><th>IB 数量与占比</th><th>观察量 / UW</th><th>行距离</th><th>UW 保存质量</th></tr></thead><tbody id="rows"></tbody></table></main><script id="data" type="application/json">${JSON.stringify(payload).replace(/</g,'\u003c')}</script><script>
const d=JSON.parse(document.getElementById('data').textContent),q=id=>document.getElementById(id),pct=x=>(x*100).toFixed(2)+'%',sum=a=>a.reduce((s,x)=>s+x,0);function node(t,s){let e=document.createElement(t);if(s!=null)e.textContent=s;return e;}function label(id){const [scope,toggle,scenario]=id.split('|');const parts=scenario.split('/');const method={AtQuote:'仅贴买卖价',Midpoint:'中间价法',MidpointTick:'中间价＋逐笔涨跌'}[parts[1]];const align=parts[0]==='Latest'?'最新报价 / '+(parts[2]==='-1'?'无年龄上限':parts[2]+' ms'):parts[0]==='Consensus'?'多时间候选一致':'源时间 '+parts[0].replace('Source','')+' ms';return(scope==='AllTimeAndSales'?'233 全部成交回报':'375 常规成交')+' · '+method+' · '+align+' · U15 '+(toggle==='U15_ON'?'开':'关');}for(const c of d.configs){const o=node('option',label(c.id));o.value=c.id;q('config').append(o);}q('config').value=d.best;
function bar(a){const box=node('div'),v=sum(a),b=node('div');b.className='bar';a.forEach((n,i)=>{const s=node('span');s.className=['buy','sell','mid','unknown'][i];s.style.width=(v?n/v*100:0)+'%';s.title=n+' / '+(v?pct(n/v):'无数据');b.append(s);});box.append(b,node('small','买 '+a[0]+' / 卖 '+a[1]+' / 中性 '+a[2]+' / 未知 '+a[3]));return box;}
function render(){q('rows').replaceChildren();const c=d.configs.find(x=>x.id===q('config').value);let n=0,w=0,dist=0,ib=0,uw=0;d.pairs.forEach((p,i)=>{if(q('ticker').value!=='全部'&&p.ticker!==q('ticker').value)return;const a=c.rows[i],v=sum(a),tv=v?a.reduce((s,x,k)=>s+Math.abs(x/v-p.uw[k]/p.uwVolume),0)/2:null;const tr=node('tr');const texts=[p.start+'–'+p.end+' '+p.ticker+' '+p.strike+' '+p.right,bar(p.uw),bar(a),v+' / '+p.uwVolume+' ('+pct(v/p.uwVolume)+')',tv==null?'无数据':pct(tv),(p.uwInferred?'含缺段补算':'三侧比例还原')+' · '+(p.uwAfterEnd?'结束后保存':'结束前最后变化')];texts.forEach(t=>{let td=node('td');typeof t==='string'?td.textContent=t:td.append(t);tr.append(td);});q('rows').append(tr);n++;ib+=v;uw+=p.uwVolume;if(tv!=null){dist+=tv*p.uwVolume;w+=p.uwVolume;}});q('stats').textContent=n+' 个合约桶；成交量 IB '+ib+' / UW '+uw+'；加权平均分布距离 '+(w?pct(dist/w):'无数据');}q('config').onchange=render;q('ticker').onchange=render;render();</script></html>`;}
main().catch(e=>{console.error(e);process.exitCode=1;});

// Offline diagnostic only. Reuses versioned per-trade decisions; never connects to IB/UW.
const fs=require('node:fs'),path=require('node:path'),readline=require('node:readline'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const ScoreResearch=require('./ScoreResearch.js');
const {latestScore}=require('./LatestScore.cjs');
const {comparisonWindow,exclusion}=require('./CompareWindow.cjs');
const {allowedScenario}=require('./ReportSelection.cjs');
const asset=name=>fs.readFileSync(path.join(__dirname,name),'utf8');
const [run,uwFile,analysis,out,...flags]=process.argv.slice(2);
assert.ok(flags.every(x=>x==='--exclude-ib-anomalous-buckets'||x.startsWith('--end-utc=')),'Unknown comparison option');
const qualityMode=flags.includes('--exclude-ib-anomalous-buckets'),requestedEndUtc=flags.find(x=>x.startsWith('--end-utc='))?.slice(10);
if(!out)throw Error('Usage: node Compare-Uw.cjs <run> <uw.json> <analysis> <new-output>');
fs.mkdirSync(out,{recursive:false});
const read=p=>JSON.parse(fs.readFileSync(p,'utf8'));
const uw=read(uwFile),contracts=read(path.join(run,'contracts.json')).map(x=>x.contract),summary=read(path.join(run,'capture-summary.json'));
// Read only the small metadata prefix, not the hundreds of MiB of bucket detail.
const reportFd=fs.openSync(path.join(analysis,'report.json'),'r'),headerBuffer=Buffer.alloc(1024*1024);
let headerText;try{headerText=headerBuffer.subarray(0,fs.readSync(reportFd,headerBuffer,0,headerBuffer.length,0)).toString('utf8');}finally{fs.closeSync(reportFd);}
const headerEnd=headerText.indexOf('"totals":');assert.ok(headerEnd>0,'Missing finalized analysis metadata');
const analysisMetadata=JSON.parse(headerText.slice(0,headerEnd).replace(/,\s*$/,'')+'}');
assert.equal(analysisMetadata.run_id,summary.run_id);assert.equal(analysisMetadata.algorithm_version,'0.9.2','Use the same 0.9.2 analysis rules');
const byId=new Map(contracts.map(c=>[c.con_id,c]));
const identity=(ticker,expiry,strike,right)=>[ticker==='SPXW'?'SPX':ticker,expiry,Number(strike),right.toUpperCase()].join('|');
const contractKeys=new Map(contracts.map(c=>[identity(c.ticker,c.expiration,c.strike_usd,c.right),c.con_id]));
const window=comparisonWindow(uw,summary,requestedEndUtc),{first,last,split}=window;
const pairKey=(conId,t)=>conId+'|'+t;
const pairs=[],excluded={invalidBucket:0,finalUwBucket:0,invalidCounts:0,requestedEnd:0,outsideWindow:0,unsubscribed:0},excludedRows=[];
for(const r of uw.latest){
 const t=Date.parse(r.bucket.startUtc),end=Date.parse(r.bucket.endUtc);
 const cid=contractKeys.get(identity(r.identity.ticker,r.identity.expiry,r.identity.strike,r.identity.right));
 const rejected=exclusion(r,window,!!cid);
 if(rejected){excluded[rejected]++;excludedRows.push({identity:r.identity,bucket:r.bucket,reason:rejected,uwStatus:r.ba?.status,uwReason:r.ba?.reason});continue;}
 const c=r.ba.counts;
 pairs.push({key:pairKey(cid,t),conId:cid,ticker:byId.get(cid).ticker,strike:Number(r.identity.strike),right:r.identity.right,expiry:r.identity.expiry,start:r.bucket.start,end:r.bucket.end,t,uw:[c.ask,c.bid,c.neutral,0],uwVolume:r.volume,uwInferred:!!r.ba.inference,uwAfterEnd:Date.parse(r.pageReadUtc||r.capturedUtc)>=end,uwCaptured:r.capturedUtc,uwRevision:r.revision,multi:r.raw.fields.find(x=>x.name==='% Multi')?.text});
}
pairs.sort((a,b)=>a.t-b.t||a.ticker.localeCompare(b.ticker)||a.strike-b.strike||a.right.localeCompare(b.right));
assert.equal(new Set(pairs.map(r=>r.key)).size,pairs.length);
assert.ok(pairs.length,'No reconstructed matching contract buckets');
const pairMap=new Map(pairs.map(x=>[x.key,x])),tradeBySequence=new Map(),configs=new Map(),dispositions={},largeThresholds={};
const ibAnomalies=[],anomalousKeys=new Set();
const categoryOf=m=>m.sign>0?0:m.sign<0?1:['LOCKED_QUOTE','MIDPOINT_UNKNOWN','MID_TIME_AGREE'].includes(m.reason)?2:3;
async function* lines(p){const stream=fs.createReadStream(p);for await(const s of readline.createInterface({input:stream,crlfDelay:Infinity}))if(s)yield JSON.parse(s);}
async function main(){
 for(const file of fs.readdirSync(run).filter(x=>/^events-.*\.jsonl$/.test(x)).sort()){
  for await(const e of lines(path.join(run,file))){
   const raw=e.raw;if(raw.kind!=='tickString'||![48,77].includes(raw.field)||!raw.contract)continue;
   const parts=raw.value.split(';'),t=Number(parts[2]),bucket=Math.floor(t/300000)*300000,key=pairKey(raw.contract.con_id,bucket);
   if(!pairMap.has(key))continue;
   const size=Number(parts[1]);if(!Number.isFinite(size)||size<0)continue;
   tradeBySequence.set(raw.sequence,{key,size,price:Number(parts[0]),field:raw.field,rawValue:parts.join(';'),sourceUtc:new Date(t).toISOString(),receivedUtc:raw.received_utc});
  }
 }
 console.log('Raw trade callbacks retained for matched buckets:',tradeBySequence.size);
 for(const file of fs.readdirSync(analysis).filter(x=>/^decisions-.*\.jsonl$/.test(x)).sort()){
  for await(const d of lines(path.join(analysis,file))){
   const trade=tradeBySequence.get(d.sequence);if(!trade)continue;
   assert.equal(d.scope,trade.field===77?'RegularTrades':'AllTimeAndSales');
   const dk=d.scope+'/'+d.disposition;dispositions[dk]=(dispositions[dk]||0)+1;
   if(d.disposition!=='ACCEPTED')continue;
   // Diagnostic magnitude flag, not an IB-defined validity limit. Raw comparison keeps it.
   if(trade.size>=2147483648){anomalousKeys.add(trade.key);ibAnomalies.push({sequence:d.sequence,scope:d.scope,...trade});}
   for(const [outside,e] of [[false,d.direction_evidence],[true,d.outside_quote_evidence]]){
    assert.ok(e,'Missing dual-mode trace; regenerate 0.8 report first');
    assert.ok(e.wait_matches?.length===12 && e.wait_matches.every(m=>m.scenario_id.startsWith('Envelope/')),'Requires 0.8 simulated-envelope trace, not the old consistency rule');
    const ticker=pairMap.get(trade.key).ticker;assert.ok(!largeThresholds[ticker]||largeThresholds[ticker]===e.large_trade_threshold);largeThresholds[ticker]=e.large_trade_threshold;
    assert.equal(e.conditional_matches?.length,3,'Requires 0.9 conditional trace');
    for(const m of [...e.matches,...(e.wait_matches||[]),...(e.conditional_matches||[]),...e.consensus.map(x=>({...x,scenario_id:'Consensus/'+x.scenario_id}))]){
     if(!allowedScenario(m.scenario_id))continue;
     const id=d.scope+'|'+(outside?'U15_ON':'U15_OFF')+'|'+m.scenario_id;
     if(!configs.has(id))configs.set(id,{id,scope:d.scope,outside,scenario:m.scenario_id,rows:new Map()});
     const rows=configs.get(id).rows;if(!rows.has(trade.key))rows.set(trade.key,{counts:[0,0,0,0],events:0,waitVolumes:{},waitCounts:{},mergeScore:{changedVolume:0,unchangedVolume:0,changedEvents:0,unchangedEvents:0}});
     const a=rows.get(trade.key);const category=m.sign>0?0:m.sign<0?1:['LOCKED_QUOTE','MIDPOINT_UNKNOWN','MID_TIME_AGREE'].includes(m.reason)?2:3;
     a.counts[category]+=trade.size;a.events++;
     const scoring=m.scenario_id.startsWith('Latest/')?latestScore(m,e):m;
     if(scoring.branch==='LARGE_MERGED'){
      const method=m.scenario_id.split('/')[1],pre=m.scenario_id.startsWith('Latest/')?m:e.matches.find(x=>x.scenario_id==='Latest/'+method+'/-1');
      assert.ok(pre,'Missing same-trade latest quote classification');
      const change=categoryOf(pre)!==categoryOf(scoring)?'changed':'unchanged';
      a.mergeScore[change+'Volume']+=trade.size;a.mergeScore[change+'Events']++;
     }
     if(scoring.branch){a.waitVolumes[scoring.branch]=(a.waitVolumes[scoring.branch]||0)+trade.size;a.waitCounts[scoring.branch]=(a.waitCounts[scoring.branch]||0)+1;}
     if(m.scenario_id.startsWith('Envelope/')){const wait=Number(m.scenario_id.split('/')[2]),status=e.wait_decisions.find(x=>x.wait_ms===wait)?.status;assert.ok(status,'Missing wait audit');a.waitVolumes[status]=(a.waitVolumes[status]||0)+trade.size;}
    }
   }
  }
  console.log('Processed',file);
 }
 const ibAnomalyPairs=pairs.filter(p=>anomalousKeys.has(p.key));
 if(qualityMode)for(let i=pairs.length-1;i>=0;i--)if(anomalousKeys.has(pairs[i].key))pairs.splice(i,1);
 assert.ok(pairs.length,'No pairs remain after quality selection');
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
 const sets={all:pairs,QQQ:pairs.filter(x=>x.ticker==='QQQ'),SPX:pairs.filter(x=>x.ticker==='SPX'),postEnd:pairs.filter(x=>x.uwAfterEnd),noInferred:pairs.filter(x=>!x.uwInferred),train:pairs.filter(x=>x.t<split),test:pairs.filter(x=>x.t>=split)};
 const rankings={};for(const [name,subset]of Object.entries(sets))rankings[name]=[...configs.values()].map(c=>({id:c.id,...metric(c,subset)})).sort((a,b)=>(a.missing-b.missing)||(a.weightedTV-b.weightedTV));
 const best=rankings.all[0],selected=configs.get(best.id),bestRows=measurements(selected,pairs).map(x=>({...x.p,ib:x.a.counts,events:x.a.events,tv:x.tv,netError:x.netError,ibNet:x.ibNet,uwNet:x.uwNet,volumeRatio:x.volumeRatio}));
 const byBucket=[...new Set(pairs.map(x=>x.start))].map(start=>({start,...metric(selected,pairs.filter(x=>x.start===start))}));
 const winners={};for(const name of ['all','QQQ','SPX','postEnd','noInferred','train']){const id=rankings[name][0]?.id;if(id)winners[name]={id,all:metric(configs.get(id),pairs),train:metric(configs.get(id),sets.train),test:metric(configs.get(id),sets.test)};}
 const coverageByScope={};for(const scope of ['RegularTrades','AllTimeAndSales'])coverageByScope[scope]=metric([...configs.values()].find(c=>c.scope===scope),pairs);
 const waitComparison=[...configs.values()].filter(c=>c.scenario.startsWith('Envelope/')||c.scenario.startsWith('Conditional/')).map(c=>{const [_,method,wait]=c.scenario.split('/'),baseline=configs.get(c.scope+'|'+(c.outside?'U15_ON':'U15_OFF')+'|Latest/'+method+'/-1');const waits={};for(const a of c.rows.values())for(const[k,v]of Object.entries(a.waitVolumes))waits[k]=(waits[k]||0)+v;const m=metric(c,pairs),b=metric(baseline,pairs);const now=measurements(c,pairs),old=measurements(baseline,pairs);return {id:c.id,waitMs:Number(wait),...m,baselineTV:b.weightedTV,deltaTV:m.weightedTV-b.weightedTV,waitVolumes:waits,improved:now.filter((x,i)=>x.tv<old[i].tv-1e-12).length,worsened:now.filter((x,i)=>x.tv>old[i].tv+1e-12).length,QQQ:metric(c,sets.QQQ),SPX:metric(c,sets.SPX),train:metric(c,sets.train),test:metric(c,sets.test)};});
 const result={qualityMode:qualityMode?'EXCLUDE_FLAGGED_PAIR_SENSITIVITY':'RAW_COMPARISON',ibAnomalies,ibAnomalyPairs,largeThresholds,createdUtc:new Date().toISOString(),uwFile,run,analysis,uwSha256:crypto.createHash('sha256').update(fs.readFileSync(uwFile)).digest('hex'),window:window.date+' '+window.startLabel+'–'+window.endLabel+' America/New_York',windowDetails:window,captureSummary:summary,analysisMetadata,uwRun:{date:uw.run.config.date,startedUtc:uw.run.startedUtc,stoppedUtc:uw.run.stoppedUtc,lastPollUtc:uw.run.lastPollUtc,gaps:uw.run.gaps},excluded,excludedRows,uwLatest:uw.latest.length,uwRevisions:uw.revisions.length,pairCount:pairs.length,possiblePairs:contracts.length*((last-first)/300000),uwInferredPairs:pairs.filter(x=>x.uwInferred).length,uwPostEndPairs:sets.postEnd.length,dispositions,coverageByScope,rankings,winners,byBucket,bestRows,waitComparison};

 const exportConfigs=[...configs.values()].map(c=>({id:c.id,rows:pairs.map(p=>(c.rows.get(p.key)||{counts:[0,0,0,0]}).counts),waits:pairs.map(p=>c.rows.get(p.key)?.waitVolumes||{}),waitCounts:pairs.map(p=>c.rows.get(p.key)?.waitCounts||{}),mergeScores:pairs.map(p=>c.rows.get(p.key)?.mergeScore||{})}));
 result.scoreResearch={version:4,supportedAlignments:['Conditional','Latest'],filterBinding:'CURRENT_ALIGNMENT_ONLY',formula:'100 * (AGREE_VOLUME - MERGED_CHANGED_VOLUME) / OBSERVED_VOLUME',weights:{LARGE_AGREE:1,MERGED_CHANGED:-1,MERGED_UNCHANGED:0,other:0},defaultFilter:{enabled:false,minimum:0,coverage:.1,events:3},studies:[]};
 for(const c of exportConfigs.filter(c=>/\|(Conditional|Latest)\//.test(c.id))){
  const parts=c.id.split('|'),method=parts[2].split('/')[1];const baseline=exportConfigs.find(x=>x.id===parts[0]+'|'+parts[1]+'|Latest/'+method+'/-1');
  for(const [set,subset]of Object.entries(sets)){const keys=new Set(subset.map(p=>p.key)),indices=pairs.map((p,i)=>i).filter(i=>keys.has(pairs[i].key));
   result.scoreResearch.studies.push({id:c.id,set,groups:ScoreResearch.study(pairs,c,baseline,indices)});
  }
 }
 fs.writeFileSync(path.join(out,'comparison.json'),JSON.stringify(result,null,2));
 fs.writeFileSync(path.join(out,'comparison.html'),html({pairs,configs:exportConfigs,best:best.id,result}));
 console.log(JSON.stringify({pairCount:result.pairCount,excluded,uwInferredPairs:result.uwInferredPairs,uwPostEndPairs:result.uwPostEndPairs,top:rankings.all.slice(0,5),QQQ:rankings.QQQ.slice(0,2),SPX:rankings.SPX.slice(0,2),winners},null,2));
}
function html(payload){return `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>${payload.result.windowDetails.date} IB 与 UW 对照</title><style>body{font:15px system-ui;background:#f4f7fa;color:#20394b;margin:30px}main{max-width:1500px;margin:auto}select{padding:10px;max-width:95%;margin:8px}table{width:100%;border-collapse:collapse;background:white}td,th{padding:12px;border-bottom:1px solid #dde5ec;text-align:left}th{background:#e7eff5}.bar{display:flex;width:260px;height:16px;border-radius:4px;overflow:hidden;margin:5px 0}.buy{background:#2db494}.sell{background:#ef4966}.mid{background:#8175b5}.unknown{background:#b3bcc6}small{color:#688095}section{background:white;border-radius:12px;padding:20px;margin:16px 0}.warning{background:#fff2d9;padding:16px}strong{font-size:20px} .score-controls{display:flex;flex-wrap:wrap;align-items:center;gap:14px}.score-controls label{display:flex;align-items:center;gap:6px}.score-controls input[type=number]{width:72px;padding:8px}.score-scroll{overflow-x:auto}.score-bar{display:flex;width:145px;height:9px;margin:6px 0;border-radius:5px;overflow:hidden}#score-error{color:#bd283b}details{margin:12px 0}summary{cursor:pointer;color:#147c80}</style><main><h1>IB × UW · ${payload.result.windowDetails.date}</h1>${payload.result.ibAnomalies.length?`<p class="warning">严重数量异常：原始成交回调有 ${payload.result.ibAnomalies.length} 笔数量 ≥ 2³¹，涉及 ${payload.result.ibAnomalyPairs.length} 个合约桶。此阈值仅作异常提示，不是IB官方有效性界限。${payload.result.qualityMode==='RAW_COMPARISON'?'本报告保留原始数量，受影响口径的总体量比及评分不可视作正常样本结果。':'本报告为敏感性对照：在所有配置中排除整个异常合约桶；不把大数改成小数，不修改原始记录。'}</p>`:''}<p>纽约时间 ${payload.result.windowDetails.startLabel}–${payload.result.windowDetails.endLabel}；排除 UW 最后桶 ${payload.result.windowDetails.excludedFinalLabel}、未成功还原的数据及 IB 未完整覆盖的桶。按成交数量，不按权利金比较。</p><p>纳入 ${payload.result.pairCount} 个合约桶。排除行数：最后桶 ${payload.result.excluded.finalUwBucket}；还原失败或数量不合法 ${payload.result.excluded.invalidCounts}；指定截止时刻之后 ${payload.result.excluded.requestedEnd}；IB 覆盖范围之外 ${payload.result.excluded.outsideWindow}；合约未订阅 ${payload.result.excluded.unsubscribed}；无效时间桶 ${payload.result.excluded.invalidBucket}（最后桶、还原失败、指定截止、IB范围、未订阅依次判定，每行只计一次；JSON含逐行原因）。</p><p class="warning">IB运行结束原因：${payload.result.captureSummary.stop_reason}；整次采集完整标记：${payload.result.captureSummary.complete?'是':'否'}；本地丢弃 ${payload.result.captureSummary.dropped}；记录无效 ${payload.result.analysisMetadata.integrity.invalid_records}；字段解析错误 ${payload.result.analysisMetadata.integrity.parse_errors}；IB 10090/10091权限警告 ${payload.result.analysisMetadata.diagnostics.IB_10090||0}/${payload.result.analysisMetadata.diagnostics.IB_10091||0}。时间范围完整不保证上游成交完整；结论仅适用于纳入的合约桶。</p><p class="warning">UW 是页面比例还原的参考，不是主动买卖真值。最接近配置由本次样本选出，不代表未来准确率。未知不并入 Mid；无成交数据不当作零方向。</p><section><label>IB 配置<select id="config"></select></label><label>品种<select id="ticker"><option>全部</option><option>QQQ</option><option>SPX</option></select></label><p id="stats"></p><small>颜色：绿 Ask/买 · 红 Bid/卖 · 紫 Neutral/Mid · 灰 未知。行距离 = 四类成交量占比分布的总变差距离（越小越近）；不等于逐笔准确率。</small></section>${asset('ScorePanel.html')}<section><h2>规则对照 · 条件式大单（全区间，不受评分过滤）</h2><p>与上方选定配置使用相同成交口径、分类方法和 U15 开关；基线为接收时最新报价、不设年龄上限。占比按成交量计算。条件大单：QQQ≥${payload.result.largeThresholds.QQQ}张、SPX≥${payload.result.largeThresholds.SPX}张，仅大单等待50毫秒，第一次价格更新无效立即回退前报价，不再寻找后续报价；到期无后报价也回退前报价；一致保留，不一致模拟合并。条件大单行的末列依次为：一致 / 合并 / 普通成交 / 超时 / 中断 / 前报价无效 / 首次后报价无效立即回退。</p><table><thead><tr><th>等待上限</th><th>分布距离 ↓</th><th>相对基线变化</th><th>未知占比</th><th>前后报价 / 超时回退 / 未完成</th></tr></thead><tbody id="waitrows"></tbody></table></section><table><thead><tr><th>时间 / 合约</th><th>UW 数量与占比</th><th>IB 数量与占比</th><th>观察量 / UW</th><th>行距离</th><th>当前规则评分 / 证据覆盖</th><th>UW 保存质量</th></tr></thead><tbody id="rows"></tbody></table></main><script id="data" type="application/json">${JSON.stringify(payload).replace(/</g,'\u003c')}</script><script>
${asset('ScoreResearch.js')}
const d=JSON.parse(document.getElementById('data').textContent),q=id=>document.getElementById(id),pct=x=>(x*100).toFixed(2)+'%',sum=a=>a.reduce((s,x)=>s+x,0);function node(t,s){let e=document.createElement(t);if(s!=null)e.textContent=s;return e;}function label(id){const [scope,toggle,scenario]=id.split('|');const parts=scenario.split('/');const method={AtQuote:'仅贴买卖价',Midpoint:'中间价法'}[parts[1]];const align=parts[0]==='Latest'?'最新报价 / '+(parts[2]==='-1'?'无年龄上限':parts[2]+' ms'):parts[0]==='Consensus'?'多时间候选一致':parts[0]==='Conditional'?'条件式大单 / 50 ms':parts[0]==='Envelope'?'前后模拟报价 / 等待 '+parts[2]+' ms':'源时间 '+parts[0].replace('Source','')+' ms';return(scope==='AllTimeAndSales'?'233 全部成交回报':'375 常规成交')+' · '+method+' · '+align+' · U15 '+(toggle==='U15_ON'?'开':'关');}for(const c of d.configs){const o=node('option',label(c.id));o.value=c.id;q('config').append(o);}q('config').value=d.best;
function bar(a){const box=node('div'),v=sum(a),b=node('div');b.className='bar';a.forEach((n,i)=>{const s=node('span');s.className=['buy','sell','mid','unknown'][i];s.style.width=(v?n/v*100:0)+'%';s.title=n+' / '+(v?pct(n/v):'无数据');b.append(s);});box.append(b,node('small','买 '+a[0]+' / 卖 '+a[1]+' / 中性 '+a[2]+' / 未知 '+a[3]));return box;}
${asset('ScorePanel.js')}
function render(){q('rows').replaceChildren();const c=d.configs.find(x=>x.id===q('config').value);let n=0,w=0,dist=0,ib=0,uw=0;d.pairs.forEach((p,i)=>{if(!scoreVisible(i))return;const a=c.rows[i],v=sum(a),tv=v?a.reduce((s,x,k)=>s+Math.abs(x/v-p.uw[k]/p.uwVolume),0)/2:null;const tr=node('tr');const texts=[p.start+'–'+p.end+' '+p.ticker+' '+p.strike+' '+p.right,bar(p.uw),bar(a),v+' / '+p.uwVolume+' ('+pct(v/p.uwVolume)+')',tv==null?'无数据':pct(tv),scoreCell(i),(p.uwInferred?'含缺段补算':'三侧比例还原')+' · '+(p.uwAfterEnd?'结束后保存':'结束前最后变化')];texts.forEach(t=>{let td=node('td');typeof t==='string'?td.textContent=t:td.append(t);tr.append(td);});q('rows').append(tr);n++;ib+=v;uw+=p.uwVolume;if(tv!=null){dist+=tv*p.uwVolume;w+=p.uwVolume;}});q('stats').textContent=n+' 个合约桶；成交量 IB '+ib+' / UW '+uw+'；加权平均分布距离 '+(w?pct(dist/w):'无数据');}function waitRender(){q('waitrows').replaceChildren();const c=d.configs.find(x=>x.id===q('config').value),[scope,toggle,scenario]=c.id.split('|'),method=scenario.split('/')[1],set=q('ticker').value==='全部'?'all':q('ticker').value,rank=d.result.rankings[set],base=rank.find(x=>x.id===scope+'|'+toggle+'|Latest/'+method+'/-1');for(const wait of ['条件大单']){const conditional=typeof wait==='string',id=scope+'|'+toggle+'|'+(conditional?'Conditional/':'Envelope/')+method+'/'+(conditional?50:wait),m=rank.find(x=>x.id===id),cfg=d.configs.find(x=>x.id===id);if(!m||!cfg)continue;let v=0,w={};d.pairs.forEach((p,i)=>{if(set!=='all'&&p.ticker!==set)return;v+=sum(cfg.rows[i]);for(const[k,n]of Object.entries(cfg.waits[i]))w[k]=(w[k]||0)+n;});const delta=m.weightedTV-base.weightedTV,tr=node('tr'),button=node('button',conditional?'条件大单 · 50毫秒':wait+' 毫秒');button.onclick=()=>{q('config').value=id;update();};for(const x of [button,pct(m.weightedTV),(delta>0?'+':'')+(delta*100).toFixed(2)+' 个百分点',pct(m.unknownFraction),(conditional?['LARGE_AGREE','LARGE_MERGED','SMALL_LATEST','LARGE_TIMEOUT','LARGE_INTERRUPTED','LARGE_INVALID_QUOTES','LARGE_FIRST_INVALID_FALLBACK']:['PAIRED','TIMEOUT_PRE_ONLY','WAIT_INTERRUPTED']).map(k=>pct(v?(w[k]||0)/v:0)).join(' / ')]){const td=node('td');typeof x==='string'?td.textContent=x:td.append(x);tr.append(td);}q('waitrows').append(tr);}}function update(){scoreSync();render();waitRender();scoreRender();}q('config').onchange=update;q('ticker').onchange=update;update();</script></html>`;}
main().catch(e=>{console.error(e);process.exitCode=1;});

'use strict';
const fs=require('node:fs'),path=require('node:path'),readline=require('node:readline'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const Research=require('./CrossDayResearch.cjs');
const {classify,elapsedMs}=require('./QuoteAuditClassifier.cjs');
const sum=a=>a.reduce((s,v)=>s+v,0),category=m=>m.sign>0?0:m.sign<0?1:['LOCKED_QUOTE','MIDPOINT_UNKNOWN','MID_TIME_AGREE'].includes(m.reason)?2:3;
async function* records(file,hashes){const hash=crypto.createHash('sha256'),stream=fs.createReadStream(file);stream.on('data',b=>hash.update(b));for await(const line of readline.createInterface({input:stream,crlfDelay:Infinity}))if(line)yield JSON.parse(line);hashes.push({file,sha256:hash.digest('hex')});}
function summarize(trades){
 const total=sum(trades.map(t=>t.size)),squares=sum(trades.map(t=>t.size*t.size)),maxN=n=>sum([...trades].sort((a,b)=>b.size-a.size).slice(0,n).map(t=>t.size))/total;
 const rate=fn=>total?sum(trades.filter(fn).map(t=>t.size))/total:null;
 const avg=fn=>{const valid=trades.filter(t=>Number.isFinite(fn(t))),v=sum(valid.map(t=>t.size));return v?sum(valid.map(t=>fn(t)*t.size))/v:null;};
 const age=t=>t.bidAgeMs===null||t.askAgeMs===null?null:Math.max(t.bidAgeMs,t.askAgeMs);
 const lastCounter=trades.filter(t=>Number.isFinite(t.cumulativeVolume));let reversals=0;for(let i=1;i<lastCounter.length;i++)if(lastCounter[i].cumulativeVolume<lastCounter[i-1].cumulativeVolume)reversals++;
 return {total,callbacks:trades.length,largestShare:total?maxN(1):null,top3Share:total?maxN(3):null,effectiveCallbacks:squares?total*total/squares:0,
  old1000:rate(t=>age(t)!==null&&age(t)>1000),old5000:rate(t=>age(t)!==null&&age(t)>5000),missingAge:rate(t=>age(t)===null),
  quoteAgeWeightedMs:avg(age),sideAgeSkewWeightedMs:avg(t=>age(t)===null?null:Math.abs(t.bidAgeMs-t.askAgeMs)),
  sourceLagWeightedMs:avg(t=>t.sourceLagMs),late1500:rate(t=>t.sourceLagMs>1500),
  relativeSpread:avg(t=>t.bid>0&&t.ask>=t.bid?2*(t.ask-t.bid)/(t.ask+t.bid):null),
  outsideVolume:rate(t=>/OVERRIDE$/.test(t.reason)),insideVolume:rate(t=>/^INSIDE_/.test(t.reason)),
  invalidNextVolume:rate(t=>/INVALID/.test(t.branch)),cumulativeReversals:reversals};
}
async function build(days=Research.loadSources()){
 const out={version:2,createdUtc:new Date().toISOString(),scope:'AllTimeAndSales',scenario:'Latest/Midpoint/-1',outside:true,sourceFiles:[],buckets:[]};
 for(const d of days){
  const pairs=new Map(d.pairs.map((p,i)=>[p.key,{p,i}])),raw=new Map(),trades=new Map(d.pairs.map(p=>[p.key,[]]));
  for(const file of fs.readdirSync(d.result.run).filter(x=>/^events-.*\.jsonl$/.test(x)).sort()){
   for await(const e of records(path.join(d.result.run,file),out.sourceFiles)){
    const r=e.raw;if(r.kind!=='tickString'||r.field!==48||!r.contract)continue;
    const parts=r.value.split(';'),time=Number(parts[2]),key=r.contract.con_id+'|'+Math.floor(time/300000)*300000;
    if(!pairs.has(key))continue;
    raw.set(r.sequence,{key,size:Number(parts[1]),price:Number(parts[0]),cumulativeVolume:Number(parts[3]),receivedUtc:r.received_utc,sourceUtc:new Date(time).toISOString()});
   }
  }
  for(const file of fs.readdirSync(d.result.analysis).filter(x=>/^decisions-.*\.jsonl$/.test(x)).sort()){
   for await(const e of records(path.join(d.result.analysis,file),out.sourceFiles)){
    if(e.scope!=='AllTimeAndSales'||e.disposition!=='ACCEPTED')continue;
    const t=raw.get(e.sequence);if(!t)continue;
    assert.ok(Number.isFinite(t.size)&&t.size>=0&&t.size<2147483648,'Unexpected raw quantity anomaly in research window');
    const evidence=e.outside_quote_evidence,latest=evidence.alignments.find(a=>a.id==='Latest'),match=evidence.matches.find(m=>m.scenario_id==='Latest/Midpoint/-1'),conditional=evidence.conditional_matches.find(m=>m.scenario_id==='Conditional/Midpoint/50');
    assert.ok(latest&&match&&conditional);const q=latest.quote,nextAlignment=evidence.conditional_next_quote,next=nextAlignment?.quote;
    const preCategory=classify(latest,t.price),atQuoteCategory=classify(latest,t.price,'AtQuote'),nextCategory=classify(nextAlignment,t.price),nextAtQuoteCategory=classify(nextAlignment,t.price,'AtQuote'),nextDelayMs=elapsedMs(e.received_utc,nextAlignment?.target_utc);
    assert.equal(preCategory,category(match),`Midpoint classifier parity ${d.day} ${e.sequence}`);
    assert.equal(atQuoteCategory,category(evidence.matches.find(m=>m.scenario_id==='Latest/AtQuote/-1')),`AtQuote classifier parity ${d.day} ${e.sequence}`);
    const timelyNext=nextDelayMs!==null&&nextDelayMs>=0&&nextDelayMs<=50;
    if(['LARGE_AGREE','LARGE_MERGED'].includes(conditional.branch))assert.ok(timelyNext&&nextCategory!==3,'Expected a valid first next quote within original deadline');
    trades.get(t.key).push({...t,sequence:e.sequence,category:category(match),sign:match.sign,reason:match.reason,branch:conditional.branch,conditionalCategory:category(conditional),
     bid:q?.bid?.price??null,ask:q?.ask?.price??null,bidAgeMs:latest.bid_age_ms,askAgeMs:latest.ask_age_ms,sourceLagMs:e.source_lag_ms,
     nextBid:next?.bid?.price??null,nextAsk:next?.ask?.price??null,nextDelayMs,nextCategory,nextAtQuoteCategory,atQuoteCategory,timelyNext,isLarge:evidence.is_large_trade,conditionalStatus:evidence.conditional_wait_decision?.status??null});
   }
   console.log(d.day,'audited',file);
  }
  for(const [key,ts]of trades){
   const {p,i}=pairs.get(key),counts=[0,0,0,0];for(const t of ts)counts[t.category]+=t.size;
   assert.deepEqual(counts,d.configs.get(Research.BASE).rows[i],`Trade trace mismatch: ${d.day} ${key}`);
   const features=summarize(ts),largest=[...ts].sort((a,b)=>b.size-a.size).slice(0,8),reasonVolumes={};for(const t of ts)reasonVolumes[t.reason]=(reasonVolumes[t.reason]||0)+t.size;
   const staleModels=['all-valid','open-spread','both-directional'].flatMap(mode=>[0,250,500,1000,2000,5000].map(ageMs=>{const counts=[0,0,0,0],atQuote=[0,0,0,0];let switchedVolume=0,switchedEvents=0;const changes=[];
    for(const t of ts){const modeOK=mode==='all-valid'||(mode==='open-spread'?t.nextBid<t.nextAsk:t.category<2&&t.nextCategory<2);
     const use=modeOK&&t.isLarge&&t.category!==3&&Math.max(t.bidAgeMs,t.askAgeMs)>=ageMs&&t.timelyNext&&t.nextCategory!==3;
     counts[use?t.nextCategory:t.category]+=t.size;atQuote[use?t.nextAtQuoteCategory:t.atQuoteCategory]+=t.size;
     if(use&&t.nextCategory!==t.category){switchedVolume+=t.size;switchedEvents++;changes.push(t);}
    }
    return {mode,ageMs,counts,atQuote,switchedVolume,switchedEvents,largestChanges:changes.sort((a,b)=>b.size-a.size).slice(0,4)};
   }));
   out.buckets.push({day:d.day,key,label:`${p.start}–${p.end} ${p.ticker} ${p.strike} ${p.right}`,ticker:p.ticker,counts,features,reasonVolumes,largest,staleModels});
  }
 }
 return out;
}
if(require.main===module)build().then(out=>{const target=process.argv[2]||path.resolve(__dirname,'../../docs/reports/uw-ib-crossday/trade-audit.json');fs.writeFileSync(target,JSON.stringify(out,null,2));console.log('Verified against existing comparison:',out.buckets.length,'buckets;',sum(out.buckets.map(b=>b.features.total)),'contracts');}).catch(e=>{console.error(e);process.exitCode=1;});
module.exports={summarize,build};

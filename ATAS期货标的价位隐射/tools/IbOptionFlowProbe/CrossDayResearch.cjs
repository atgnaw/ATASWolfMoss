// Offline, bucket-level research. UW is a reference label, never an inference feature.
'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const Score=require('./ScoreResearch.js');
const Stability=require('./BuySellStability.js');
const ROOT=path.resolve(__dirname,'../..');
const BASE='AllTimeAndSales|U15_ON|Latest/Midpoint/-1';
const CONDITIONAL='AllTimeAndSales|U15_ON|Conditional/Midpoint/50';
const sum=a=>a.reduce((s,x)=>s+x,0);
const ba=c=>c[0]+c[1]>0?c[0]/(c[0]+c[1]):null;
const quantile=(a,p)=>a.length?[...a].sort((a,b)=>a-b)[Math.ceil(a.length*p)-1]:null;
const SOURCES=[['2026-09-23','docs/reports/uw-ib-20260923/comparison.html'],['2026-09-28','docs/reports/uw-ib-20260928/no-close/comparison.html'],['2026-09-29','docs/reports/uw-ib-20260929/comparison.html']];
function loadSources(sources=SOURCES){return sources.map(([day,file])=>{
 const bytes=fs.readFileSync(path.resolve(ROOT,file)),m=bytes.toString('utf8').match(/<script id="data" type="application\/json">([\s\S]*?)<\/script>/);
 assert.ok(m,`No comparison data: ${file}`);const d=JSON.parse(m[1]);
 assert.equal(d.result.windowDetails.date,day);assert.equal(d.configs.length,72);
 const configs=new Map(d.configs.map(c=>[c.id,c]));assert.ok(configs.has(BASE));
 for(const c of d.configs){assert.equal(c.rows.length,d.pairs.length);for(const r of c.rows)assert.ok(r.every(v=>Number.isFinite(v)&&v>=0));}
 assert.equal(new Set(d.pairs.map(p=>p.key)).size,d.pairs.length);
 assert.ok(d.pairs.every(p=>p.uw.every(v=>v>=0)&&sum(p.uw)===p.uwVolume));
 return {day,file,sha256:crypto.createHash('sha256').update(bytes).digest('hex'),pairs:d.pairs,configs,result:d.result};
});}
// Keep feature calculation separate from pairs/UW: this function cannot access reference labels.
function features(configs,i){
 const c=configs.get(BASE),counts=c.rows[i],v=sum(counts),w=c.waits[i],n=c.waitCounts[i],m=c.mergeScores[i],s=Score.score(counts,w,n,m);
 const rate=x=>v?x/v:0,ratio=id=>ba(configs.get(id).rows[i]),b=ba(counts),diff=x=>b===null||x===null?null:Math.abs(b-x);
 return {
  score:s.value,changed:rate(m.changedVolume||0),merged:rate(w.LARGE_MERGED||0),agree:rate(w.LARGE_AGREE||0),large:rate(sum(Object.entries(w).filter(([k])=>k.startsWith('LARGE_')).map(([,v])=>v))),
  timeout:rate(w.LARGE_TIMEOUT||0),invalid:rate(sum(Object.entries(w).filter(([k])=>/INVALID|UNSCORABLE|INTERRUPTED/.test(k)).map(([,v])=>v))),
  coverage:rate(counts[0]+counts[1]),unknown:rate(counts[3]),volume:v,events:sum(Object.values(n)),
  conditionalGap:diff(ratio(CONDITIONAL)),methodGap:Stability.assess(counts,configs.get('AllTimeAndSales|U15_ON|Latest/AtQuote/-1').rows[i]).gap,
  scopeGap:diff(ratio('RegularTrades|U15_ON|Latest/Midpoint/-1')),
  age1000Gap:diff(ratio('AllTimeAndSales|U15_ON|Latest/Midpoint/1000')),
  age5000Gap:diff(ratio('AllTimeAndSales|U15_ON|Latest/Midpoint/5000')),
  outsideGap:diff(ratio('AllTimeAndSales|U15_OFF|Latest/Midpoint/-1'))
 };
}
function observations(days){return days.flatMap(d=>d.pairs.map((p,i)=>({day:d.day,key:p.key,ticker:p.ticker,label:`${p.start}–${p.end} ${p.ticker} ${p.strike} ${p.right}`,uw:p.uw,uwVolume:p.uwVolume,uwInferred:p.uwInferred,multi:p.multi,
 counts:Object.fromEntries([...d.configs].map(([id,c])=>[id,c.rows[i]])),features:features(d.configs,i)})));}
function metric(rows,id=BASE){
 const a=rows.map(r=>{const c=Array.isArray(id)?r.counts[BASE].map((v,j)=>v*(1-id[0])+r.counts[CONDITIONAL][j]*id[0]):r.counts[id];const x=ba(c),u=ba(r.uw);return {v:sum(c),directional:c[0]+c[1],u:r.uwVolume,e:x===null||u===null?null:Math.abs(x-u),tv:sum(c)&&r.uwVolume?sum(c.map((v,j)=>Math.abs(v/sum(c)-r.uw[j]/r.uwVolume)))/2:null};});
 const valid=a.filter(x=>x.e!==null),weight=sum(valid.map(x=>x.u)),ib=sum(a.map(x=>x.v));
 return {n:rows.length,valid:valid.length,missing:a.length-valid.length,ibVolume:ib,uwVolume:sum(a.map(x=>x.u)),coverage:ib?sum(a.map(x=>x.directional))/ib:null,
  mean:weight?sum(valid.map(x=>x.e*x.u))/weight:null,equalMean:valid.length?sum(valid.map(x=>x.e))/valid.length:null,
  p95:quantile(valid.map(x=>x.e),.95),max:valid.length?Math.max(...valid.map(x=>x.e)):null,
  bad10:valid.filter(x=>x.e>.1).length,bad20:valid.filter(x=>x.e>.2).length,
  tv:weight?sum(valid.map(x=>x.tv*x.u))/weight:null};
}
const FEATURE_SPECS={
 score:['gte',[-40,-20,-10,-5,0,5,10,20]],changed:['lte',[0,.01,.025,.05,.1,.15,.2,.3,.4]],merged:['lte',[.025,.05,.1,.2,.3,.5]],
 agree:['gte',[.01,.025,.05,.1,.2]],large:['lte',[.2,.3,.4,.5,.6,.7,.8,.9]],timeout:['lte',[.05,.1,.2,.4,.6]],invalid:['lte',[0,.001,.01,.05]],
 coverage:['gte',[.5,.6,.7,.8,.85,.9,.95,.98]],unknown:['lte',[0,.001,.01,.05,.1]],volume:['gte',[100,250,500,1000,2000]],events:['gte',[10,20,50,100]],
 conditionalGap:['lte',[.01,.025,.05,.075,.1,.15,.2,.3]],methodGap:['lte',[.01,.025,.05,.1,.15,.2]],scopeGap:['lte',[.01,.025,.05,.1,.15,.2]],
 age1000Gap:['lte',[.01,.025,.05,.1,.15,.2]],age5000Gap:['lte',[.01,.025,.05,.1,.15,.2]],outsideGap:['lte',[.01,.025,.05,.1,.15,.2]]
};
const LABELS={score:'当前最新报价辅助评分',changed:'模拟合并改变分类的成交量占比',merged:'进入模拟合并的成交量占比',agree:'大单前后分类一致占比',large:'大单成交量占比',timeout:'大单等待超时占比',invalid:'无效或中断证据占比',coverage:'买卖分类覆盖率',unknown:'未知占比',volume:'IB 成交张数',events:'IB 成交回调数',conditionalGap:'最新与条件式的买方比例差',methodGap:'中间价与贴价法的买方比例差',scopeGap:'全部与常规成交的买方比例差',age1000Gap:'1 秒年龄上限的买方比例差',age5000Gap:'5 秒年龄上限的买方比例差',outsideGap:'U15 开关的买方比例差'};
function candidates(){const singles=Object.entries(FEATURE_SPECS).flatMap(([feature,[op,values]])=>values.map(value=>({id:`${feature}:${op}:${value}`,rules:[{feature,op,value}]})));
 // Explicit, bounded two-factor family; do not try arbitrary feature combinations.
 const pairs=singles.filter(x=>['changed','conditionalGap','score'].includes(x.rules[0].feature)).flatMap(a=>singles.filter(b=>b.rules[0].feature==='coverage').map(b=>({id:a.id+'&'+b.id,rules:[...a.rules,...b.rules]})));
 return [{id:'none',rules:[]},...singles,...pairs];}
function passes(f,filter){return filter.rules.every(({feature,op,value})=>Number.isFinite(f[feature])&&(op==='gte'?f[feature]>=value-1e-12:f[feature]<=value+1e-12));}
function selectionStats(rows,filter,id=BASE){const kept=rows.filter(r=>passes(r.features,filter)),removed=rows.filter(r=>!passes(r.features,filter)),before=metric(rows,id),after=metric(kept,id),rejected=metric(removed,id);return {before,after,rejected,bucketRetention:rows.length?kept.length/rows.length:0,volumeRetention:before.ibVolume?after.ibVolume/before.ibVolume:0,
 bad10Recall:before.bad10?(before.bad10-after.bad10)/before.bad10:null,bad10Precision:removed.length?rejected.bad10/removed.length:null,
 removedKeys:removed.map(r=>r.day+'|'+r.key)};}
// Training objective gives each trading day equal importance. Thresholds are fixed above.
// UW weighting is only a training/evaluation weight, never a live filter feature.
function objective(metrics){return sum(metrics.map(m=>m.mean+.25*m.p95+.10*m.bad10/m.valid))/metrics.length;}
function fitFilter(train,retain,allFilters=candidates()){
 const dates=[...new Set(train.map(r=>r.day))];let best;
 for(const filter of allFilters){const studies=dates.map(day=>selectionStats(train.filter(r=>r.day===day),filter));
  if(studies.some(s=>s.bucketRetention+1e-12<retain||s.volumeRetention+1e-12<retain||s.after.missing||s.after.n<10))continue;
  const loss=objective(studies.map(s=>s.after));
  if(!best||loss<best.loss-1e-12||(Math.abs(loss-best.loss)<1e-12&&filter.rules.length<best.filter.rules.length))best={filter,loss,training:studies};
 }
 assert.ok(best,'No viable filter');return best;
}
function fitConfig(train,ids){const dates=[...new Set(train.map(r=>r.day))];return ids.map(id=>({id,perDay:dates.map(day=>metric(train.filter(r=>r.day===day),id))})).filter(x=>x.perDay.every(m=>m.missing===0&&m.coverage>=.7)).map(x=>({...x,loss:sum(x.perDay.map(m=>m.mean))/dates.length})).sort((a,b)=>a.loss-b.loss||a.id.localeCompare(b.id))[0];}
function run(days){
 const rows=observations(days),ids=[...days[0].configs.keys()],filters=candidates(),retentions=[.6,.7,.8,.9];
 const rankings=ids.map(id=>({id,all:metric(rows,id),days:days.map(d=>({day:d.day,...metric(rows.filter(r=>r.day===d.day),id)}))})).sort((a,b)=>(a.all.missing-b.all.missing)||(a.all.mean-b.all.mean));
 const folds=days.map(d=>{const train=rows.filter(r=>r.day!==d.day),test=rows.filter(r=>r.day===d.day),selected=fitConfig(train,ids);
  return {day:d.day,trainDays:days.filter(x=>x.day!==d.day).map(x=>x.day),selectedConfig:selected.id,configTrainingLoss:selected.loss,configTest:metric(test,selected.id),baseline:metric(test),conditional:metric(test,CONDITIONAL),
   filters:retentions.map(retain=>{const fit=fitFilter(train,retain,filters);return {retain,...fit,test:selectionStats(test,fit.filter)};})};});
 const fixed=retentions.map(retain=>({retain,...fitFilter(rows,retain,filters)}));
 const policies=retentions.map(retain=>{const keep=rows.filter(r=>passes(r.features,folds.find(f=>f.day===r.day).filters.find(f=>f.retain===retain).filter)),selected=new Set(keep.map(r=>r.day+'|'+r.key));return {retain,all:metric(keep),rejected:metric(rows.filter(r=>!selected.has(r.day+'|'+r.key))),bucketRetention:keep.length/rows.length,volumeRetention:metric(keep).ibVolume/metric(rows).ibVolume};});
 const filterAudits=filters.map(f=>({filter:f,all:selectionStats(rows,f),days:days.map(d=>({day:d.day,...selectionStats(rows.filter(r=>r.day===d.day),f)}))}));
 const blends=[0,.25,.5,.75,1].map(weight=>({conditionalWeight:weight,all:metric(rows,[weight]),days:days.map(d=>({day:d.day,...metric(rows.filter(r=>r.day===d.day),[weight])}))}));
 const methodFamily=filters.filter(f=>f.id==='none'||f.rules.length===1&&f.rules[0].feature==='methodGap');
 const methodOnlyFolds=days.map(d=>{const fit=fitFilter(rows.filter(r=>r.day!==d.day),.6,methodFamily);return {day:d.day,...fit,test:selectionStats(rows.filter(r=>r.day===d.day),fit.filter)};});
 const chronological=days.slice(1).map((d,i)=>{const trainDays=days.slice(0,i+1).map(d=>d.day),train=rows.filter(r=>trainDays.includes(r.day)),test=rows.filter(r=>r.day===d.day);
  return {day:d.day,trainDays,fullFamily:(()=>{const fit=fitFilter(train,.6,filters);return {...fit,test:selectionStats(test,fit.filter)};})(),methodFamily:(()=>{const fit=fitFilter(train,.6,methodFamily);return {...fit,test:selectionStats(test,fit.filter)};})()};});
 const stabilityFilter=filters.find(f=>f.id==='methodGap:lte:0.01');
 const subgroupChecks=['QQQ','SPX','no-inferred'].map(group=>({group,...selectionStats(rows.filter(r=>group==='no-inferred'?!r.uwInferred:r.ticker===group),stabilityFilter)}));
 // Exploratory alternative: switch WHOLE bucket to AtQuote when methods disagree.
 // It changes the reported method, not the recorded trades. Do not silently deploy it.
 const switches=[0,.01,.025,.05,.1,.15,.2,.3,.4,1].map(threshold=>{
  const switched=rows.map(r=>{const c=r.counts['AllTimeAndSales|U15_ON|Latest/AtQuote/-1'],use=Number.isFinite(r.features.methodGap)&&r.features.methodGap>threshold&&ba(c)!==null;return {...r,counts:{...r.counts,SWITCH:use?c:r.counts[BASE]},switched:use};});
  return {threshold,changed:switched.filter(r=>r.switched).length,all:metric(switched,'SWITCH'),days:days.map(d=>({day:d.day,...metric(switched.filter(r=>r.day===d.day),'SWITCH')}))};
 });
 const largestErrors=rows.map(r=>({day:r.day,key:r.key,label:r.label,ticker:r.ticker,uw:r.uw,ib:r.counts[BASE],conditional:r.counts[CONDITIONAL],baselineError:Math.abs(ba(r.counts[BASE])-ba(r.uw)),conditionalError:Math.abs(ba(r.counts[CONDITIONAL])-ba(r.uw)),features:r.features})).sort((a,b)=>b.baselineError-a.baselineError);
 return {version:1,createdUtc:new Date().toISOString(),protocol:{primary:'abs(IB Ask/(Ask+Bid) - UW Ask/(Ask+Bid))',largeError:.1,veryLargeError:.2,filterTrainingObjective:'equal-day mean of (UW-volume-weighted BA error + 0.25 * bucket-equal P95 + 0.10 * fraction above 10pp)',constraints:'In each training day retain at least target fraction of BOTH buckets and IB volume. No missing BA, at least 10 buckets. Config selection additionally requires >=70% directional volume coverage.',validation:'Leave one entire trading day out. No row-wise random split. Feature and threshold selection never use held-out UW labels. This is exploratory internal validation on already observed dates, NOT a prospective accuracy test. The method-only family was examined after all-family validation and is post-hoc; chronological replay cannot undo this selection.',baseline:BASE,conditional:CONDITIONAL,filterCandidateCount:filters.length,retentions,scope:'9/28 excludes 15:55–16:00. All dates exclude final UW bucket and unreconstructed data. UW is a reference, not ground truth.'},sources:days.map(d=>({day:d.day,file:d.file,sha256:d.sha256,pairs:d.pairs.length,window:d.result.window,excluded:d.result.excluded})),baseline:metric(rows),conditional:metric(rows,CONDITIONAL),rankings,blends,switches,subgroupChecks,folds,policies,fixed,filterAudits,methodOnlyFolds,chronological,largestErrors,rows};
}
function ruleName(f){if(!f.rules.length)return '不过滤';return f.rules.map(r=>`${LABELS[r.feature]} ${r.op==='gte'?'≥':'≤'} ${['volume','events','score'].includes(r.feature)?r.value:(100*r.value).toFixed(1)+(r.feature.endsWith('Gap')?' 个百分点':'%')}`).join(' 且 ');}
const esc=s=>String(s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function html(result){return `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>三日 IB × UW · 买卖比例与过滤验证</title><style>
body{margin:0;background:#f3f6fa;color:#193246;font:15px/1.6 system-ui}main{max-width:1440px;margin:auto;padding:28px}h1{font-size:29px;margin-bottom:5px}h2{font-size:21px}section,.card{background:#fff;border:1px solid #dde5ef;border-radius:14px;padding:22px;margin:18px 0}.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:16px}.card{margin:0}.value{font-size:30px;font-weight:700;color:#00838a}small,.muted{color:#667d90}.warning{background:#fff3d9;border-left:4px solid #dfac46;padding:14px}table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}th,td{padding:10px;border-bottom:1px solid #e4eaf1;text-align:left}th{background:#edf3f8;font-size:13px}.scroll{overflow:auto}button,select{padding:9px;border:1px solid #bccddb;border-radius:6px;background:#fff;color:#193246}label{margin-right:18px;display:inline-block;margin-bottom:10px}.bad{color:#c44550}.good{color:#00838a}.bar{height:9px;background:#e6edf3;width:110px;border-radius:9px;overflow:hidden}.bar i{display:block;background:#15949a;height:100%}details{margin:12px 0}summary{cursor:pointer;color:#007780}svg{width:100%;max-width:700px;height:auto}.pill{background:#eaf3f4;padding:4px 8px;border-radius:5px}@media(max-width:800px){main{padding:12px}.grid{grid-template-columns:1fr}th,td{padding:7px}}</style><main>
<h1>三日 IB × UW：先看买卖比例，再检验过滤</h1><p class="muted">9/23、9/28（去尾盘）、9/29 · 共 ${result.baseline.n} 个合约时间桶 · 只做离线研究，不改动原采集和分类结果</p><p><a href="audit.html">查看逐笔审计与补充过滤实验：哪些异常被方法一致漏掉了？</a></p>
<p class="warning">买方比例 = 买 ÷（买＋卖）。误差 5 个百分点表示例如 IB 为 65%，UW 为 60%。Mid 与未知不进入比例分母，但仍展示覆盖率。UW 不是逐笔主动方向真值。三个日期都已用于此前探索，下面是内部跨日检验，不是未见过数据的正式准确率。</p>
<section><h2>先看结论</h2><p>当前推荐候选：<b>233 全部成交＋中间价法＋最新报价不限年龄＋U15开启</b>；可选过滤为同桶中间价法和贴价法的买方比例差≤1个百分点。两份结果必须使用相同成交口径、报价对齐和U15开关。</p><p>现有三日样本：保留185/257桶及92.38%的成交量，平均买卖比例误差2.07→1.72点，最大84.27→17.24点，超过10点的桶13→4。代价：剔除72桶，其中63桶原本未超过10点偏差。下面“固定条件”部分可调整门槛；逐桶列表默认对应这个候选。</p><p class="muted">补充18种后报价实验未显示稳定跨日优势；增加第二个过滤条件会损失更多正常成交量。保留简单方法作为待新增日期验证的候选，不把样本内拟合当作准确率。</p></section>
<div class="grid" id="cards"></div>
<section><h2>1 · 哪种配置更接近？</h2><p>主排名按买卖比例误差，均值按 UW 桶成交量加权。P95、最大值、大偏差桶数按桶统计。买卖不可计算的桶单列，不能当零误差。</p><label>日期 <select id="rankday"><option value="all">三个交易日</option>${result.sources.map(d=>`<option>${d.day}</option>`).join('')}</select></label><div class="scroll"><table><thead><tr><th>配置</th><th>平均误差 ↓</th><th>P95 ↓</th><th>最大 ↓</th><th>误差＞10点</th><th>买卖覆盖</th><th>不可比较</th></tr></thead><tbody id="ranking"></tbody></table></div><details><summary>配置排名和验证如何区分？</summary>此排名使用全部所选样本，属于样本内比较。跨日检验用另外两天选配置，再在留出的整天计算误差；要求训练日买卖覆盖率至少70%，且没有不可比较桶。不在每个桶上用UW挑最好的配置。</details><div id="configfolds"></div></section>
<section><h2>2 · 过滤能否排除大偏差桶？</h2><p>基线固定为「233 全部成交＋中间价法＋最新报价不限年龄＋U15开」。只用 IB 特征过滤。保留目标同时约束训练日的桶数和 IB 成交量；留出日不强行保证保留量。</p><label>训练保留目标 <select id="retain">${result.protocol.retentions.map(x=>`<option value="${x}" ${x===.8?'selected':''}>至少 ${x*100}%</option>`).join('')}</select></label><div id="filterstats"></div><div class="scroll"><table><thead><tr><th>留出验证日</th><th>由另外两天选出的条件</th><th>保留桶 / 量</th><th>均值 前→后</th><th>P95 前→后</th><th>最大 前→后</th><th>大偏差 剔除/原有</th></tr></thead><tbody id="folds"></tbody></table></div><details><summary>筛选目标与限制</summary>在每个训练日满足保留目标后，选择使「日均加权误差＋0.25×P95＋0.10×大偏差桶比例」最低的条件。各日同权，避免成交量最多的一天主导。阈值来自固定网格；最多两个条件；无训练合格条件时保留全部。比较的是选择过滤条件这一流程，不是宣称某个阈值已被独立验证。评分不是准确概率。</details><div id="fixed"></div></section>
<section><h2>3 · 固定条件的取舍（样本内探索）</h2><p>重点候选：中间价法与贴价法的买方比例差不超过 1 个百分点。两者都使用同一套最新报价和U15开关，只改变分类方法；不是买卖方向真实一致的保证。与单纯提高大单评分对照。</p><label>候选条件 <select id="candidate">${result.filterAudits.filter(x=>x.filter.id==='none'||x.filter.rules.length===1&&['methodGap','score','volume'].includes(x.filter.rules[0].feature)).map(x=>`<option value="${x.filter.id}" ${x.filter.id==='methodGap:lte:0.01'?'selected':''}>${esc(ruleName(x.filter))}</option>`).join('')}</select></label><div id="candidateStats"></div><div class="scroll"><table><thead><tr><th>范围</th><th>保留桶 / 量</th><th>均值 前→后</th><th>最大 前→后</th><th>大偏差 剔除/原有</th><th>被剔除桶中大偏差占比</th></tr></thead><tbody id="candidateRows"></tbody></table></div><div id="chronological"></div><details><summary>为什么这还不是独立验证？</summary>全特征自动选择在各日选出了不同条件；方法差异这一候选族是在看到结果后重点检查的。限定只选“方法差异”时，三次整日留出都选1点阈值；按时间先后重放也可计算，但这个候选族的选择仍受已有三天数据影响。只能标为待下一交易日验证，不能宣传为已证明未来准确。</details></section>
<section><h2>4 · 被保留与被剔除的具体桶</h2><label>使用条件 <select id="rowpolicy"><option value="crossfit">第2节：整日留出选择</option><option value="candidate" selected>第3节：当前固定候选</option></select></label><label>日期 <select id="rowday"><option value="all">全部</option>${result.sources.map(d=>`<option>${d.day}</option>`).join('')}</select></label><label>显示 <select id="state"><option value="all">全部</option><option value="removed">剔除</option><option value="kept">保留</option></select></label><p class="muted">按原始误差从大到小排列。比例条：青色买，红色卖；已移除中性和未知的分母。完整展示被剔除的正常桶，不能只展示成功案例。</p><div class="scroll"><table><thead><tr><th>日期 / 合约桶</th><th>IB 买方比例</th><th>UW 买方比例</th><th>误差</th><th>结果与具体原因</th></tr></thead><tbody id="buckets"></tbody></table></div></section>
<section><h2>5 · 额外核对：分品种、还原质量与改分类的代价</h2><div id="robustness"></div><details><summary>混合或改用另一种分类，有没有更好？</summary><div id="alternatives"></div><p>这些为样本内补充试验；即使均值下降，也需同时检查P95和大偏差桶数。未据此改变采集器或原报告的分类结果。</p></details></section>
<section><h2>研究边界</h2><ul><li>主分析使用9/28去尾盘版本；不把数量异常回调改成正常数。UW未还原和最后未完成桶沿用既有排除。</li><li>相同交易日内相邻桶、合约可能相关；${result.baseline.n}个桶不等于${result.baseline.n}个独立样本。只有3个交易日，不能给“高分更准”的普适结论。</li><li>辅助前后报价证据需要最多50毫秒；用于实时过滤时必须等证据到达，不能假称成交回调当下已知。“中间价与贴价法差异”本身不需要后报价，但完整时间桶要等桶结束。</li><li>来源文件哈希、完整72配置、单条件/组合实验、各日结果和每桶原因均保存在同目录 research.json。图表未改变数据。</li></ul><div id="sources"></div></section></main>
<script id="research-data" type="application/json">${JSON.stringify(result).replace(/</g,'\\u003c')}</script><script>
const D=JSON.parse(document.getElementById('research-data').textContent),BASE=${JSON.stringify(BASE)},LABELS=${JSON.stringify(LABELS)};
const esc=${esc.toString()},ruleName=${ruleName.toString()},passes=${passes.toString()},ba=${ba.toString()};
const pc=v=>v===null||v===undefined?'—':(v*100).toFixed(2)+'%',pp=v=>v===null?'—':(v*100).toFixed(2)+'点',get=id=>document.getElementById(id);
const name=id=>id.replace('AllTimeAndSales','233 全部成交').replace('RegularTrades','375 常规成交').replace('U15_ON','U15开').replace('U15_OFF','U15关').replace('Latest/Midpoint/','最新·中间价·').replace('Latest/AtQuote/','最新·贴价·').replace('Conditional/Midpoint/50','条件式·中间价·50ms').replace('Conditional/AtQuote/50','条件式·贴价·50ms').replace('Consensus/Midpoint','多候选一致·中间价').replace('Consensus/AtQuote','多候选一致·贴价').replace('/-1','不限年龄').replace(/\\|-1$/,'|不限年龄').replace(/·-1$/,'·不限年龄');
get('cards').innerHTML=[['最新报价平均买卖比例误差',pp(D.baseline.mean),'P95 '+pp(D.baseline.p95)+' · 最大 '+pp(D.baseline.max)],['条件式大单平均误差',pp(D.conditional.mean),'P95 '+pp(D.conditional.p95)+' · 最大 '+pp(D.conditional.max)],['最新报价大偏差桶（＞10点）',D.baseline.bad10+' / '+D.baseline.n,'不通过删去 Mid 或未知掩盖覆盖情况']].map(x=>'<div class="card"><div>'+x[0]+'</div><div class="value">'+x[1]+'</div><small>'+x[2]+'</small></div>').join('');
function rank(){const day=get('rankday').value;get('ranking').innerHTML=D.rankings.map(x=>({id:x.id,m:day==='all'?x.all:x.days.find(d=>d.day===day)})).sort((a,b)=>a.m.missing-b.m.missing||a.m.mean-b.m.mean).slice(0,16).map(({id,m})=>'<tr><td>'+esc(name(id))+'</td><td>'+pp(m.mean)+'</td><td>'+pp(m.p95)+'</td><td>'+pp(m.max)+'</td><td>'+m.bad10+'/'+m.valid+'</td><td>'+pc(m.coverage)+'</td><td>'+m.missing+'</td></tr>').join('');}
get('configfolds').innerHTML='<h3>整日留出：用两天选配置，检验第三天</h3>'+D.folds.map(f=>'<p><b>'+f.day+'</b>：'+esc(name(f.selectedConfig))+'；留出日平均误差 '+pp(f.configTest.mean)+'（固定最新报价 '+pp(f.baseline.mean)+'）</p>').join('');
function render(){const r=Number(get('retain').value),p=D.policies.find(p=>p.retain===r);get('filterstats').innerHTML='<p class="pill">合并留出结果：保留 '+p.all.n+'/'+D.baseline.n+' 桶（'+pc(p.bucketRetention)+'），IB 量 '+pc(p.volumeRetention)+'；平均误差 '+pp(D.baseline.mean)+' → '+pp(p.all.mean)+'；最大 '+pp(D.baseline.max)+' → '+pp(p.all.max)+'；剔除 '+p.rejected.bad10+'/'+D.baseline.bad10+' 个大偏差桶，全部剔除桶 '+p.rejected.n+' 个。</p>';
 get('folds').innerHTML=D.folds.map(f=>{const x=f.filters.find(x=>x.retain===r),s=x.test,a=s.after,b=s.before;return '<tr><td>'+f.day+'</td><td>'+esc(ruleName(x.filter))+'</td><td>'+a.n+'/'+b.n+' / '+pc(s.volumeRetention)+'</td><td class="'+(a.mean<=b.mean?'good':'bad')+'">'+pp(b.mean)+' → '+pp(a.mean)+'</td><td>'+pp(b.p95)+' → '+pp(a.p95)+'</td><td>'+pp(b.max)+' → '+pp(a.max)+'</td><td>'+(b.bad10-a.bad10)+'/'+b.bad10+'</td></tr>';}).join('');
 const fixed=D.fixed.find(x=>x.retain===r);get('fixed').innerHTML='<p class="warning">三天合起来拟合的下一批待验证条件：'+esc(ruleName(fixed.filter))+'。这只是候选，不能把这三天的拟合成绩当作新日期表现。</p>';
 const candidate=D.filterAudits.find(x=>x.filter.id===get('candidate').value),c=candidate.all;
 get('candidateStats').innerHTML='<p class="warning">样本内结果：保留 '+c.after.n+'/'+c.before.n+' 桶，剔除 '+c.rejected.n+' 桶，其中 '+c.rejected.bad10+' 个误差超过10点、'+(c.rejected.n-c.rejected.bad10)+' 个未超过。下一交易日仍需验证。</p>';
 get('candidateRows').innerHTML=[{day:'合计',...c},...candidate.days].map(s=>'<tr><td>'+s.day+'</td><td>'+s.after.n+'/'+s.before.n+' / '+pc(s.volumeRetention)+'</td><td>'+pp(s.before.mean)+' → '+pp(s.after.mean)+'</td><td>'+pp(s.before.max)+' → '+pp(s.after.max)+'</td><td>'+(s.before.bad10-s.after.bad10)+'/'+s.before.bad10+'</td><td>'+pc(s.bad10Precision)+'</td></tr>').join('');
 const date=get('rowday').value,state=get('state').value;
 get('buckets').innerHTML=D.largestErrors.filter(x=>date==='all'||x.day===date).map(x=>{const f=get('rowpolicy').value==='candidate'?candidate.filter:D.folds.find(f=>f.day===x.day).filters.find(f=>f.retain===r).filter,keep=passes(x.features,f);return {x,f,keep};}).filter(x=>state==='all'||(state==='kept')===x.keep).map(({x,f,keep})=>{const reasons=f.rules.filter(rule=>!passes(x.features,{rules:[rule]})).map(rule=>LABELS[rule.feature]+'＝'+(x.features[rule.feature]===null?'缺失':['volume','events','score'].includes(rule.feature)?x.features[rule.feature].toFixed(2):pc(x.features[rule.feature]))+'，要求 '+ruleName({rules:[rule]}));const bar=v=>pc(v)+'<div class="bar" style="background:#e87782"><i style="width:'+(v*100)+'%"></i></div>';return '<tr><td>'+x.day+'<br>'+esc(x.label)+'</td><td>'+bar(ba(x.ib))+'</td><td>'+bar(ba(x.uw))+'</td><td>'+pp(x.baselineError)+'</td><td class="'+(keep?'good':'bad')+'">'+(keep?'保留':esc(reasons.join('；')))+'</td></tr>';}).join('');}
get('chronological').innerHTML='<h3>只用更早日期训练：时间顺序重放</h3>'+D.chronological.map(f=>'<p>'+f.trainDays.join('＋')+' → '+f.day+'：全候选族选出「'+esc(ruleName(f.fullFamily.filter))+'」，验证日误差 '+pp(f.fullFamily.test.after.mean)+'；限定方法差异候选族选出「'+esc(ruleName(f.methodFamily.filter))+'」，验证日误差 '+pp(f.methodFamily.test.after.mean)+'。</p>').join('');
get('robustness').innerHTML='<p>固定“方法差≤1点”的子集检查：</p>'+D.subgroupChecks.map(s=>'<p>'+({'no-inferred':'排除UW缺失色段补算'}[s.group]||s.group)+'：保留 '+s.after.n+'/'+s.before.n+' 桶；均值 '+pp(s.before.mean)+' → '+pp(s.after.mean)+'；最大 '+pp(s.before.max)+' → '+pp(s.after.max)+'。</p>').join('');
get('alternatives').innerHTML=D.blends.map(x=>'<p>条件式结果占比 '+pc(x.conditionalWeight)+'（其余最新报价；四类数量按比例混合，不是实际成交张数）：均值 '+pp(x.all.mean)+'，P95 '+pp(x.all.p95)+'，大偏差 '+x.all.bad10+' 桶。</p>').join('')+'<p>按方法差切换整桶到贴价法：</p>'+D.switches.map(x=>'<p>差异＞'+pp(x.threshold)+' 才切换：'+x.changed+' 桶改用贴价；均值 '+pp(x.all.mean)+'，P95 '+pp(x.all.p95)+'，最大 '+pp(x.all.max)+'，大偏差 '+x.all.bad10+' 桶。</p>').join('');
get('sources').innerHTML=D.sources.map(s=>'<p>'+s.day+' · '+s.pairs+' 桶 · '+esc(s.window)+'</p>').join('');get('rankday').addEventListener('change',rank);for(const id of ['retain','rowday','state','rowpolicy','candidate'])get(id).addEventListener('change',render);rank();render();
</script></html>`;}
if(require.main===module){const out=path.resolve(process.argv[2]||path.join(ROOT,'docs/reports/uw-ib-crossday'));fs.mkdirSync(out,{recursive:true});const result=run(loadSources());fs.writeFileSync(path.join(out,'research.json'),JSON.stringify(result,null,2));fs.writeFileSync(path.join(out,'research.html'),html(result));console.log(JSON.stringify({baseline:result.baseline,conditional:result.conditional,top:result.rankings.slice(0,5).map(x=>({id:x.id,all:x.all})),folds:result.folds.map(f=>({day:f.day,selectedConfig:f.selectedConfig,filters:f.filters.map(x=>({retain:x.retain,rule:ruleName(x.filter),test:x.test}))})),policies:result.policies,fixed:result.fixed.map(x=>({retain:x.retain,rule:ruleName(x.filter)}))},null,2));}
module.exports={BASE,CONDITIONAL,loadSources,features,observations,metric,candidates,passes,selectionStats,fitFilter,fitConfig,run,html,ruleName};

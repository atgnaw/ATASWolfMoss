'use strict';
const fs=require('node:fs'),path=require('node:path'),readline=require('node:readline'),assert=require('node:assert/strict');
const V=require('./ValidateFrozenCandidate.cjs');
const dir=process.argv[2];assert.ok(dir);
async function main(){
 const d=V.payload(fs.readFileSync(path.join(dir,'comparison.html'),'utf8')),byBucket={},keys=new Set(d.pairs.map(p=>p.key));
 for(const name of fs.readdirSync(d.result.analysis).filter(x=>/^decisions-.*jsonl$/.test(x))){
  const reader=readline.createInterface({input:fs.createReadStream(path.join(d.result.analysis,name)),crlfDelay:Infinity});
  for await(const line of reader){const r=JSON.parse(line);if(r.scope!=='AllTimeAndSales'||r.disposition!=='ACCEPTED')continue;const t=Math.floor(Date.parse(r.source_utc)/300000)*300000,key=r.con_id+'|'+t;if(!keys.has(key))continue;const m=r.matches?.find(x=>x.maximum_age_ms===-1);if(!m)continue;const b=byBucket[key]??={};b[m.reason]=(b[m.reason]||0)+1;}
 }
 fs.writeFileSync(path.join(dir,'coverage-audit.json'),JSON.stringify({scope:'AllTimeAndSales',basis:'Accepted callbacks, source-time bucket, Latest unlimited-age quote validation; counts are callbacks not contracts.',byBucket},null,2));
 const spec=JSON.parse(fs.readFileSync('docs/reports/uw-ib-crossday/filter-candidate.json')),r=V.evaluate(d,spec),missing=r.rows.filter(x=>x.error===null),reasonTotals={};
 for(const row of missing)for(const [reason,n]of Object.entries(byBucket[row.key]||{}))reasonTotals[reason]=(reasonTotals[reason]||0)+n;
 const esc=s=>String(s).replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));
 const banner='<section id="coverage-audit-note" style="background:#fff2d9;padding:18px;border:1px solid #deb76c"><b>数据完整性提示：不能把无方向数据算作过滤成功</b><p>本日 UW 按原文件的纽约时间读取，未做时区矫正；已剔除最新桶 '+esc(r.window.excludedFinalLabel)+'。</p><p>匹配 '+r.rows.length+' 桶，其中 '+missing.length+' 桶买卖比例不可计算（时间桶开始：'+esc([...new Set(missing.map(x=>x.start))].join('、'))+'）。这些桶不进入买卖比例误差均值。其已接受 233 回调的报价校验原因：'+esc(JSON.stringify(reasonTotals))+'。</p><p>NOT_CONFIRMED_REALTIME 表示程序未确认实时行情，不等同于已证明报价延迟。即使可计算的桶也可能只分类了部分成交，不能将其方向代表性视为完整。整体买卖分类覆盖仅 '+(r.all.before.coverage*100).toFixed(2)+'%。此样本不适合单独证明过滤有效或无效；本次不放宽校验、不调参。详见 coverage-audit.json。</p></section>';
 const stamp=file=>{let html=fs.readFileSync(file,'utf8');html=html.replace(/<section id="coverage-audit-note"[\s\S]*?<\/section>/,'');html=html.replace(/(<main[^>]*>)/,'$1'+banner);fs.writeFileSync(file,html);};
 stamp(path.join(dir,'comparison.html'));V.generate(path.join(dir,'comparison.html'),dir);stamp(path.join(dir,'frozen-validation.html'));
 console.log(JSON.stringify({missingBuckets:missing.length,reasonTotals,intervals:[...new Set(missing.map(x=>x.start))]}));
}
main().catch(e=>{console.error(e);process.exitCode=1;});

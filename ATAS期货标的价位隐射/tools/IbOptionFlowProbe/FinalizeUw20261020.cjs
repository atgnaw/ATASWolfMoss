'use strict';
const fs=require('node:fs'),path=require('node:path'),readline=require('node:readline'),assert=require('node:assert/strict');
const V=require('./ValidateFrozenCandidate.cjs');
const dir='docs/reports/uw-ib-20261020/corrected-time';
async function main(){
 const d=V.payload(fs.readFileSync(dir+'/comparison.html','utf8')),counts={};
 for(const name of fs.readdirSync(d.result.analysis).filter(x=>/^decisions-.*jsonl$/.test(x))){
  const reader=readline.createInterface({input:fs.createReadStream(path.join(d.result.analysis,name)),crlfDelay:Infinity});
  for await(const line of reader){const r=JSON.parse(line);if(r.scope!=='AllTimeAndSales'||r.disposition!=='ACCEPTED'||r.source_utc<'2026-10-20T19:00:00'||r.source_utc>='2026-10-20T19:10:00')continue;const m=r.matches?.find(x=>x.maximum_age_ms===-1);if(m)counts[m.reason]=(counts[m.reason]||0)+1;}
 }
 assert.ok(counts.NOT_CONFIRMED_REALTIME>0);
 fs.writeFileSync(dir+'/unknown-audit.json',JSON.stringify({intervalUtc:['2026-10-20T19:00:00Z','2026-10-20T19:10:00Z'],scope:'AllTimeAndSales',acceptedCallbackReasons:counts},null,2));
 const banner='<section id="time-normalization-note" style="background:#fff2d9;padding:18px;border:1px solid #deb76c"><b>时间已按用户确认的 UTC+8 转换</b><p>北京时间 10月21日 02:xx–03:xx，对应纽约时间 10月20日 14:xx–15:xx。原始文件未修改；仅使用保留原字段的时间修正副本。已剔除纽约 15:10–15:15 最新桶。</p><p><b>重要限制：</b>15:00–15:10 的 40 个桶在 IB 侧全部为未知，不能计算买卖比例，不能当成零误差或过滤成功。抽取该区间全部已接受的 233 回调，报价年龄不限时的原因统计见 unknown-audit.json；出现 NOT_CONFIRMED_REALTIME（未确认实时行情）。这不等于已证实行情延迟。误差指标只评价其余可计算桶。</p></section>';
 const stamp=file=>{let html=fs.readFileSync(file,'utf8');if(!html.includes('id="time-normalization-note"'))html=html.replace(/(<main[^>]*>)/,'$1'+banner);fs.writeFileSync(file,html);};
 stamp(dir+'/comparison.html');const result=V.generate(dir+'/comparison.html',dir);stamp(dir+'/frozen-validation.html');
 console.log(JSON.stringify({reasons:counts,before:result.all.before,after:result.all.after}));
}
main().catch(e=>{console.error(e);process.exitCode=1;});

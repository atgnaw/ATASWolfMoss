'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),readline=require('node:readline'),assert=require('node:assert/strict'),V=require('./ValidateFrozenCandidate.cjs');
async function main(){for(const day of ['20261020','20261021']){
 const dir='docs/reports/uw-ib-'+day+'/repaired',d=V.payload(fs.readFileSync(dir+'/comparison.html','utf8'));
 assert.equal(d.result.analysisMetadata.algorithm_version,'0.9.3');
 const originalFiles=[],boundaries=[];
 for(const name of fs.readdirSync(d.result.run).filter(x=>/^events-.*jsonl$/.test(x))){
  const file=path.join(d.result.run,name),hash=crypto.createHash('sha256');for await(const b of fs.createReadStream(file))hash.update(b);
  originalFiles.push({name,bytes:fs.statSync(file).size,sha256:hash.digest('hex')});
  for await(const line of readline.createInterface({input:fs.createReadStream(file),crlfDelay:Infinity})){const r=JSON.parse(line).raw;if(r.error_code===2103||r.error_code===2104)boundaries.push({sequence:r.sequence,utc:r.received_utc,code:r.error_code});}
 }
 const down=boundaries.find(x=>x.code===2103),up=boundaries.find(x=>x.code===2104&&x.sequence>down.sequence);assert.ok(up);
 let checked=0;const callbackReasons={};
 for(const name of fs.readdirSync(d.result.analysis).filter(x=>/^decisions-.*jsonl$/.test(x))){for await(const line of readline.createInterface({input:fs.createReadStream(path.join(d.result.analysis,name)),crlfDelay:Infinity})){
  const r=JSON.parse(line);if(r.scope!=='AllTimeAndSales'||r.sequence<=up.sequence)continue;
  const q=r.direction_evidence?.alignments.find(x=>x.id==='Latest')?.quote;
  if(q?.data_type===1){assert.ok(!q.bid||q.bid.sequence>up.sequence);assert.ok(!q.ask||q.ask.sequence>up.sequence);assert.ok(!q.bid_size_sequence||q.bid_size_sequence>up.sequence);assert.ok(!q.ask_size_sequence||q.ask_size_sequence>up.sequence);checked++;}
  const m=r.matches?.find(x=>x.maximum_age_ms===-1);if(m)callbackReasons[m.reason]=(callbackReasons[m.reason]||0)+1;
 }}
 assert.ok(checked>0);
 const audit={algorithm:'0.9.3',originalFiles,boundaries,recovery:{down,up,checkedPostRecoveryRealtimeCallbacks:checked,callbackReasons},limitation:'Original logs lack farm names; 2104 is not a per-subscription realtime confirmation. Recovery continues a previously explicit type=1 on unchanged subscription/epoch and requires fresh ordinary quotes. Missing events are not fabricated.'};
 fs.writeFileSync(dir+'/repair-audit.json',JSON.stringify(audit,null,2));
 const banner='<section id="repair-note" style="background:#fff2d9;padding:18px;border:1px solid #deb76c"><b>0.9.3 状态修复后重算 · 原始文件及旧报告保留</b><p>此前确认实时的同一订阅，在2103后暂停；2104后丢弃中断期间报价，并等待新的价格/数量。未确认、延迟或真正断线的订阅不会自动升为实时。诊断恢复20个订阅；这是程序延续既有类型，不是IB新发出的实时确认。</p><p>原日志未记录行情服务器名，无法精确配对服务器；修复不能补回中断期间缺失的行情。新基线会使少量成交不再参与累计差分。逐笔检查恢复后报价均晚于恢复事件，证据和原文件哈希见 repair-audit.json。固定过滤阈值未改动。</p><p>'+ (day==='20261020'?'10月20日沿用用户确认的UTC+8时间修正副本；剔除15:10–15:15。':'10月21日直接按纽约时间读取，未做时区修正；剔除14:50–14:55。')+'</p></section>';
 const stamp=file=>{let html=fs.readFileSync(file,'utf8');html=html.replace(/<section id="repair-note"[\s\S]*?<\/section>/,'');fs.writeFileSync(file,html.replace(/(<main[^>]*>)/,'$1'+banner));};
 stamp(dir+'/comparison.html');V.generate(dir+'/comparison.html',dir);stamp(dir+'/frozen-validation.html');console.log(day+' PASS fresh-quote recovery audit '+checked);
}}
main().catch(e=>{console.error(e);process.exitCode=1;});

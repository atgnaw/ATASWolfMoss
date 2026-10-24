'use strict';
// Explicit source calendar date is required: never guess timezone from similarity.
const fs=require('node:fs'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const [input,output,sourceDate,tradingDate]=process.argv.slice(2);
assert.ok(input&&output&&/^\d{4}-\d{2}-\d{2}$/.test(sourceDate)&&/^\d{4}-\d{2}-\d{2}$/.test(tradingDate));
assert.ok(!fs.existsSync(output),'Refusing to overwrite');
const bytes=fs.readFileSync(input),d=JSON.parse(bytes);assert.equal(d.run.config.date,tradingDate);assert.ok(!d.timeNormalization);
const clock=new Intl.DateTimeFormat('en-GB',{timeZone:'America/New_York',hour:'2-digit',minute:'2-digit',hourCycle:'h23'}),date=new Intl.DateTimeFormat('en-CA',{timeZone:'America/New_York',year:'numeric',month:'2-digit',day:'2-digit'});
for(const r of [...d.latest,...d.revisions]){
 const b=r.bucket;assert.ok(b&&/^\d{2}:\d{2}$/.test(b.start)&&/^\d{2}:\d{2}$/.test(b.end));
 const start=Date.parse(sourceDate+'T'+b.start+':00+08:00'),end=Date.parse(sourceDate+'T'+b.end+':00+08:00');
 assert.equal(end-start,300000);assert.equal(date.format(start),tradingDate);assert.equal(date.format(end-1),tradingDate);
 r.originalBucket={...b};r.originalKey=r.key;
 r.bucket={...b,date:tradingDate,start:clock.format(start),end:clock.format(end),timezone:'America/New_York',startUtc:new Date(start).toISOString(),endUtc:new Date(end).toISOString()};
 const k=JSON.parse(r.key);k[k.length-2]=r.bucket.startUtc;k[k.length-1]=r.bucket.endUtc;r.key=JSON.stringify(k);
 r.timeState=Date.parse(r.pageReadUtc||r.capturedUtc)>=end?'ENDED_NOT_FINAL':'IN_PROGRESS';
}
assert.equal(new Set(d.latest.map(r=>r.key)).size,d.latest.length);
d.timeNormalization={originalFile:input,originalSha256:crypto.createHash('sha256').update(bytes).digest('hex'),confirmedSourceTimezone:'Asia/Shanghai',sourceCalendarDate:sourceDate,tradingDate,note:'User-confirmed UTC+8 wall clock converted to New York; raw fields, quantities, B/A counts and capture times preserved.'};
fs.writeFileSync(output,JSON.stringify(d));console.log(JSON.stringify({rows:d.latest.length,revisions:d.revisions.length,normalization:d.timeNormalization}));

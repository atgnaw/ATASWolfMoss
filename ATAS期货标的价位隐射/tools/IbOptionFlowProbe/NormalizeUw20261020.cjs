'use strict';
// User-confirmed UTC+8 page clock; retain the original export and all raw fields.
const fs=require('node:fs'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const [input,output]=process.argv.slice(2);
assert.ok(input&&output);assert.ok(!fs.existsSync(output),'Do not overwrite a normalized copy');
const bytes=fs.readFileSync(input),d=JSON.parse(bytes);
assert.equal(d.run.config.date,'2026-10-20');
const format=new Intl.DateTimeFormat('en-GB',{timeZone:'America/New_York',hour:'2-digit',minute:'2-digit',hourCycle:'h23'});
for(const row of [...d.latest,...d.revisions]){
 const b=row.bucket;assert.ok(b&&/^0[23]:\d\d$/.test(b.start)&&/^0[23]:\d\d$/.test(b.end),'Unexpected wall time');
 const start=Date.parse('2026-10-21T'+b.start+':00+08:00'),end=Date.parse('2026-10-21T'+b.end+':00+08:00');
 assert.equal(end-start,300000);assert.equal(start-Date.parse(b.startUtc),12*3600000);
 row.originalBucket={...b};row.originalKey=row.key;
 row.bucket={...b,date:'2026-10-20',start:format.format(start),end:format.format(end),timezone:'America/New_York',startUtc:new Date(start).toISOString(),endUtc:new Date(end).toISOString()};
 const key=JSON.parse(row.key);key[key.length-2]=row.bucket.startUtc;key[key.length-1]=row.bucket.endUtc;row.key=JSON.stringify(key);
 row.timeState=Date.parse(row.pageReadUtc||row.capturedUtc)>=end?'ENDED_NOT_FINAL':'IN_PROGRESS';
}
d.timeNormalization={originalFile:input,originalSha256:crypto.createHash('sha256').update(bytes).digest('hex'),confirmedSourceTimezone:'Asia/Shanghai',sourceCalendarDate:'2026-10-21',tradingDate:'2026-10-20',note:'User confirmed page clock UTC+8. Original export wrongly interpreted it as New York on trading date. Raw fields and capture timestamps unchanged; normalized bucket instants are 12 hours later than original stored instants.'};
fs.writeFileSync(output,JSON.stringify(d));
console.log(JSON.stringify({latest:d.latest.length,revisions:d.revisions.length,normalization:d.timeNormalization}));

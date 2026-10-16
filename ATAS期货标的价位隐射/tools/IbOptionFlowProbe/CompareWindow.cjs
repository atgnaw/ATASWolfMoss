const assert=require('node:assert/strict');
const width=300000;
const nyTime=t=>new Intl.DateTimeFormat('en-GB',{timeZone:'America/New_York',hour:'2-digit',minute:'2-digit',hourCycle:'h23'}).format(new Date(t));
const nyDate=t=>new Intl.DateTimeFormat('en-CA',{timeZone:'America/New_York',year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date(t));
function comparisonWindow(uw,summary,requestedEndUtc){
 assert.equal(uw.run.config.timezone,'America/New_York');
 const date=uw.run.config.date;
 const valid=uw.latest.filter(r=>Number.isFinite(Date.parse(r.bucket?.startUtc))&&Date.parse(r.bucket.endUtc)-Date.parse(r.bucket.startUtc)===width&&nyDate(Date.parse(r.bucket.startUtc))===date);
 assert.ok(valid.length,'No dated five-minute UW buckets');
 const finalStart=Math.max(...valid.map(r=>Date.parse(r.bucket.startUtc)));
 const first=Math.max(Math.ceil(Date.parse(summary.capture_started_utc)/width)*width,Math.min(...valid.map(r=>Date.parse(r.bucket.startUtc))));
 const autoLast=Math.min(Math.floor(Date.parse(summary.ended_utc)/width)*width,finalStart);
 const requestedEnd=requestedEndUtc?Date.parse(requestedEndUtc):autoLast;
 assert.ok(Number.isFinite(requestedEnd)&&requestedEnd%width===0,'Requested end must be a valid five-minute boundary');
 const last=Math.min(autoLast,requestedEnd);
 assert.ok(Number.isFinite(first)&&Number.isFinite(last)&&first<last,'No complete overlapping buckets after final UW bucket exclusion');
 assert.equal(nyDate(first),date);assert.equal(nyDate(last-1),date);
 const split=first+Math.ceil((last-first)/width/2)*width;
 return {date,first,last,autoLast,requestedEndUtc:requestedEndUtc||null,split,finalStart,startLabel:nyTime(first),endLabel:nyTime(last),splitLabel:nyTime(split),excludedFinalLabel:nyTime(finalStart)+'–'+nyTime(finalStart+width)};
}
function exclusion(r,w,subscribed){
 const t=Date.parse(r.bucket?.startUtc),end=Date.parse(r.bucket?.endUtc),c=r.ba?.counts;
 if(!Number.isFinite(t)||end-t!==width||nyDate(t)!==w.date)return 'invalidBucket';
 if(t===w.finalStart)return 'finalUwBucket';
 if(r.ba?.status!=='RECONSTRUCTED'||!c||![c.bid,c.ask,c.neutral,r.volume].every(x=>Number.isSafeInteger(x)&&x>=0)||c.bid+c.ask+c.neutral!==r.volume||!r.volume)return 'invalidCounts';
 if(w.requestedEndUtc&&t>=w.last&&end<=w.autoLast)return 'requestedEnd';
 if(t<w.first||end>w.last)return 'outsideWindow';
 if(!subscribed)return 'unsubscribed';
 return null;
}
module.exports={comparisonWindow,exclusion};

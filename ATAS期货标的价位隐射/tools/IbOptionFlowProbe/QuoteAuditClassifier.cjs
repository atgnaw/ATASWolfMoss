'use strict';
// Decimal comparisons mirror the existing C# classifier; avoid binary midpoint rounding.
function integerParts(n){const [mantissa,exponent='0']=String(n).toLowerCase().split('e'),[whole,fraction='']=mantissa.split('.');return {value:BigInt(whole+fraction),scale:fraction.length-Number(exponent)};}
function decimals(values){const a=values.map(integerParts),scale=Math.max(...a.map(x=>x.scale));return a.map(x=>x.value*10n**BigInt(scale-x.scale));}
function classify(a,price,method='Midpoint'){
 const q=a?.quote,b=q?.bid,s=q?.ask;
 if(a?.error)return ['LOCKED_QUOTE','MIDPOINT_UNKNOWN','MID_TIME_AGREE'].includes(a.error)?2:3;
 if(!a||a.error||!q||q.data_type!==1||!b||!s||b.invalid||s.invalid||!(q.bid_size>0)||!(q.ask_size>0)||!Number.isFinite(price)||!Number.isFinite(b.price)||!Number.isFinite(s.price))return 3;
 if(b.price===s.price)return 2;
 if(b.price>s.price||!Number.isFinite(a.bid_age_ms)||!Number.isFinite(a.ask_age_ms)||a.bid_age_ms<0||a.ask_age_ms<0)return 3;
 const [p,bid,ask]=decimals([price,b.price,s.price]);
 if(p>=ask)return 0; // U15 ON: outside-above maps to buy.
 if(p<=bid)return 1;
 if(method==='AtQuote')return 3;
 return 2n*p>bid+ask?0:2n*p<bid+ask?1:2;
}
function utcNanos(utc){const m=String(utc).match(/^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d+))?Z$/);if(!m)return null;const base=Date.parse(m[1]+'Z');if(!Number.isFinite(base))return null;return BigInt(base)*1000000n+BigInt((m[2]||'').padEnd(9,'0').slice(0,9));}
function elapsedMs(before,after){const a=utcNanos(before),b=utcNanos(after);return a===null||b===null?null:Number(b-a)/1e6;}
module.exports={classify,elapsedMs};

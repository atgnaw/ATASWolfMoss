// Experimental bucket gate. Requires SAME scope, quotes, age limit, U15, bucket and volume units.
// No UW data, dates, strikes or future bucket observations are used.
(function(root,factory){const api=factory();if(typeof module==='object'&&module.exports)module.exports=api;else root.BuySellStability=api;})(typeof globalThis!=='undefined'?globalThis:this,()=>{
 'use strict';
 function assess(midpointCounts,atQuoteCounts,maximumGap=.01){
  if(!Number.isFinite(maximumGap)||maximumGap<0||maximumGap>1)throw Error('maximumGap must be between 0 and 1');
  const valid=c=>Array.isArray(c)&&c.length===4&&c.every(x=>Number.isFinite(x)&&x>=0);
  if(!valid(midpointCounts)||!valid(atQuoteCounts))return {keep:false,reason:'INVALID_COUNTS',gap:null};
  const sum=c=>c.reduce((s,x)=>s+x,0),total=sum(midpointCounts),otherTotal=sum(atQuoteCounts);
  if(Math.abs(total-otherTotal)>1e-8*Math.max(1,total,otherTotal))return {keep:false,reason:'INCOMPARABLE_TOTALS',gap:null};
  const directional=midpointCounts[0]+midpointCounts[1],otherDirectional=atQuoteCounts[0]+atQuoteCounts[1];
  if(!directional||!otherDirectional)return {keep:false,reason:'NO_BUY_SELL_REFERENCE',gap:null};
  const midpointBuy=midpointCounts[0]/directional,atQuoteBuy=atQuoteCounts[0]/otherDirectional,gap=Math.abs(midpointBuy-atQuoteBuy),keep=gap<=maximumGap+1e-12;
  return {keep,reason:keep?'METHODS_STABLE':'METHODS_DIVERGE',gap,maximumGap,midpointBuy,atQuoteBuy,midpointCoverage:directional/total,atQuoteCoverage:otherDirectional/total};
 }
 return {assess};
});

'use strict';
// IB-only candidate predicates. No reference labels, date, ticker or UW multi-leg data.
const stable={feature:'methodGap',op:'lte',value:.01};
const LABELS={methodGap:'中间价/贴价比例差',conditionalGap:'50毫秒对齐敏感度',scopeGap:'常规/全部成交比例差',outsideGap:'报价外成交处理敏感度',age1000Gap:'1秒报价年龄敏感度',age5000Gap:'5秒报价年龄敏感度',changed:'模拟报价改变分类占比',coverage:'买卖分类覆盖',largestShare:'最大单笔占比',top3Share:'前三笔占比',effectiveCallbacks:'有效成交笔数',outsideVolume:'报价外成交占比',old1000:'报价年龄超过1秒的量占比',sideAgeSkewWeightedMs:'两侧更新时间差',relativeSpread:'相对价差',insideDirectional:'价差内方向性成交占比',ambiguousLarge:'前后分类不同的大单占比',directionalConcentration:'最大方向性单笔占买卖量',largeUnconfirmed:'大单无有效后报价占比'};
function candidates(){
 const specs={conditionalGap:['lte',[.025,.05,.1,.2]],scopeGap:['lte',[.025,.05,.1,.2]],outsideGap:['lte',[.025,.05,.1]],age1000Gap:['lte',[.05,.1,.2]],age5000Gap:['lte',[.025,.05,.1]],changed:['lte',[.05,.1,.2]],coverage:['gte',[.7,.8,.9]],largestShare:['lte',[.2,.3,.5]],top3Share:['lte',[.3,.5,.7]],effectiveCallbacks:['gte',[10,20,30]],outsideVolume:['lte',[.1,.2,.3]],old1000:['lte',[.3,.5,.7]],sideAgeSkewWeightedMs:['lte',[500,1000,2000]],relativeSpread:['lte',[.02,.05,.1]],insideDirectional:['lte',[.05,.1,.2]],ambiguousLarge:['lte',[.1,.2,.3]],directionalConcentration:['lte',[.2,.3,.5]],largeUnconfirmed:['lte',[.3,.5,.7]]};
 const rules=Object.entries(specs).flatMap(([feature,[op,vs]])=>vs.map(value=>({feature,op,value})));
 const singles=rules.map(r=>({id:`stable+${r.feature}:${r.op}:${r.value}`,rules:[stable,r]}));
 // Only a prespecified two-risk family: timing sensitivity with scope OR outside sensitivity.
 const combos=rules.filter(r=>r.feature==='conditionalGap').flatMap(a=>rules.filter(r=>['scopeGap','outsideGap'].includes(r.feature)).map(b=>({id:`stable+${a.feature}:${a.value}+${b.feature}:${b.value}`,rules:[stable,a,b]})));
 return [{id:'incumbent',rules:[stable]},...singles,...combos];
}
function assess(features,policy){
 const failures=policy.rules.filter(r=>!Number.isFinite(features[r.feature])||(r.op==='gte'?features[r.feature]<r.value-1e-12:features[r.feature]>r.value+1e-12));
 return {keep:failures.length===0,reasons:failures.map(r=>Number.isFinite(features[r.feature])?r.feature:'missing:'+r.feature),waitMs:policy.rules.some(r=>['conditionalGap','changed','ambiguousLarge','largeUnconfirmed'].includes(r.feature))?50:0};
}
function name(p){return p.rules.map(r=>`${LABELS[r.feature]||r.feature}${r.op==='gte'?'≥':'≤'}${['effectiveCallbacks','sideAgeSkewWeightedMs'].includes(r.feature)?r.value:(r.value*100).toFixed(1)+'%'}`).join('；');}
module.exports={candidates,assess,name,LABELS};

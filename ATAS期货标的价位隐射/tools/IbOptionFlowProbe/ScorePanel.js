// Executed inside the comparison page's existing script scope.
function scoreSupported(){return q('config').value.split('|')[2]?.split('/')[0]==='Conditional'||q('config').value.split('|')[2]?.split('/')[0]==='Latest';}
function scoreSync(){const supported=scoreSupported();if(!supported)q('score-enable').checked=false;
 for(const id of ['score-enable','score-min','score-cover','score-events'])q(id).disabled=!supported;
 q('score-status').textContent=supported?'当前评分评价所选规则；最新报价采用50毫秒辅助检查，保留当前年龄限制，不替换成交分类。':'暂不支持评分：当前对齐规则尚无独立评分依据；评分过滤已停用，品种和观察区间筛选仍有效。';
 q('score-formula').hidden=!supported;q('score-analysis').hidden=!supported;}
function scoreConfigs(){if(!scoreSupported())return null;const [scope,toggle,scenario]=q('config').value.split('|'),method=scenario.split('/')[1];return {
 conditional:d.configs.find(x=>x.id===q('config').value),
 baseline:d.configs.find(x=>x.id===scope+'|'+toggle+'|Latest/'+method+'/-1')};}
function scoreFor(c,i){return ScoreResearch.score(c.rows[i],c.waits[i],c.waitCounts?.[i],c.mergeScores?.[i]);}
function scoreOptions(){return {enabled:q('score-enable').checked,minimum:Number(q('score-min').value),coverage:Number(q('score-cover').value)/100,events:Number(q('score-events').value)};}
function scoreValid(){return ['score-min','score-cover','score-events'].every(id=>q(id).value!==''&&q(id).checkValidity());}
function scoreInScope(p){const phase=q('score-phase').value;return (q('ticker').value==='全部'||p.ticker===q('ticker').value)&&(phase==='all'||(phase==='train'?p.t<d.result.windowDetails.split:p.t>=d.result.windowDetails.split));}
function scoreVisible(i){if(!scoreInScope(d.pairs[i]))return false;if(!scoreSupported()||!q('score-enable').checked)return true;return scoreValid()&&ScoreResearch.pass(scoreFor(scoreConfigs().conditional,i),scoreOptions());}
function scoreCell(i){if(!scoreSupported())return node('small','暂不支持评分');const s=scoreFor(scoreConfigs().conditional,i),box=node('div'),bar=node('div');bar.className='score-bar';
 for(const [v,color,name]of [[s.agree,'#168d89','一致 +1'],[s.merged,'#d57d32','合并改变分类 −1'],[s.neutral,'#cbd3db','不加减 0']]){const part=node('span');part.style.width=(s.total?v/s.total*100:0)+'%';part.style.background=color;part.title=name+'：'+v+'张';bar.append(part);}
 box.append(node('b',s.noLarge?'基础 0 分':s.timeoutOnly?'0 分 · 大单全部超时回退':s.neutralOnly?'0 分 · 合并未改变分类（可含超时）':s.value===null?(s.total?'大单无加减分依据':'无成交数据'):(s.value>0?'+':'')+s.value.toFixed(1)+'分'),bar,node('small',s.noLarge?'无大单 · 仅检查最低分门槛':'评分覆盖 '+pct(s.coverage)+' · '+s.events+'笔'));
 box.title='分数依据当前配置的评分检查；绿色一致，橙色合并改变分类，灰色不加减；不是准确率。';return box;}
function scoreFmt(x){return x===null||x===undefined?'不足以计算':pct(x);}
function scoreSummaryRow(label,s){const tr=node('tr');for(const text of [label,String(s.buckets),scoreFmt(s.tv.mean),scoreFmt(s.ba.mean)+(s.ba.missing?'（'+s.ba.missing+'桶无分母）':''),scoreFmt(s.net.mean),scoreFmt(s.tv.max)+' / '+scoreFmt(s.tv.p95),scoreFmt(s.directionalCoverage)])tr.append(node('td',text));return tr;}
function scoreRender(){
 if(!scoreSupported()){for(const id of ['score-results','score-groups'])q(id).replaceChildren();for(const id of ['score-error','score-retention'])q(id).textContent='';return;}
 const {conditional,baseline}=scoreConfigs(),all=d.pairs.map((p,i)=>i).filter(i=>scoreInScope(d.pairs[i])),kept=all.filter(scoreVisible),sr=ScoreResearch;
 const before=sr.summary(d.pairs,conditional,all),after=sr.summary(d.pairs,conditional,kept),base=sr.summary(d.pairs,baseline,kept);
 q('score-error').textContent=q('score-enable').checked&&!scoreValid()?'门槛输入无效，请检查范围；没有保留任何桶。':'';
 q('score-retention').textContent='保留 '+kept.length+' / '+all.length+' 桶；IB张数保留 '+scoreFmt(before.ibVolume?after.ibVolume/before.ibVolume:null)+'；UW张数保留 '+scoreFmt(before.uwVolume?after.uwVolume/before.uwVolume:null)+'。平均误差均按UW桶总量加权；最大/P95按桶等权。';
 q('score-results').replaceChildren(scoreSummaryRow('当前规则：过滤前',before),scoreSummaryRow('当前规则：保留桶',after),scoreSummaryRow('无年龄上限最新报价参考：同一批桶',base));
 q('score-groups').replaceChildren();for(const g of sr.study(d.pairs,conditional,baseline,all)){const s=g.conditional,tr=node('tr');for(const text of [g.name,String(s.buckets),String(s.uwVolume),scoreFmt(s.tv.mean),scoreFmt(g.baseline.tv.mean),scoreFmt(s.ba.mean),scoreFmt(s.net.mean)])tr.append(node('td',text));q('score-groups').append(tr);}
}
{const w=d.result.windowDetails,options=q('score-phase').options;options[0].textContent='全部 '+w.startLabel+'–'+w.endLabel;options[1].textContent='前段 '+w.startLabel+'–'+w.splitLabel+'（探索）';options[2].textContent='后段 '+w.splitLabel+'–'+w.endLabel+'（对照）';}
for(const id of ['score-phase','score-enable','score-min','score-cover','score-events'])q(id).addEventListener('change',update);
q('score-reset').onclick=()=>{q('score-enable').checked=false;q('score-min').value='0';q('score-cover').value='10';q('score-events').value='3';q('score-phase').value='all';update();};

// Auxiliary evidence only: never replaces the selected Latest classification.
const category=m=>m.sign>0?0:m.sign<0?1:['LOCKED_QUOTE','MIDPOINT_UNKNOWN','MID_TIME_AGREE'].includes(m.reason)?2:3;
function fresh(q,age){return !!q&&q.bid_age_ms!=null&&q.ask_age_ms!=null&&q.bid_age_ms>=0&&q.ask_age_ms>=0&&(age<0||(q.bid_age_ms<=age&&q.ask_age_ms<=age));}
function latestScore(m,e){
 const [,method,limit]=m.scenario_id.split('/'),age=Number(limit);
 const c=e.conditional_matches.find(x=>x.scenario_id==='Conditional/'+method+'/50');
 if(!c)throw Error('Missing conditional auxiliary evidence');
 if(!e.is_large_trade)return {...m,branch:'SMALL_LATEST'};
 // Unknown/over-age current results cannot borrow an unrestricted score.
 if(category(m)===3||!fresh(e.alignments.find(q=>q.id===m.alignment_id),age))return {...m,branch:'LARGE_CURRENT_UNSCORABLE'};
 if(['LARGE_AGREE','LARGE_MERGED'].includes(c.branch)){
  if(!fresh(e.conditional_next_quote,age))return {...m,branch:'LARGE_AUXILIARY_TOO_OLD'};
  if(c.branch==='LARGE_MERGED'&&!fresh(e.conditional_simulated_quote,age))return {...m,branch:'LARGE_AUXILIARY_TOO_OLD'};
 }
 return c;
}
module.exports={latestScore,category};

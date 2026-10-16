// Report choices only. Full analysis traces retain historical scenarios.
function allowedScenario(id){const [alignment,method]=id.split('/');return ['AtQuote','Midpoint'].includes(method)&&['Latest','Consensus','Conditional'].includes(alignment);}
module.exports={allowedScenario};

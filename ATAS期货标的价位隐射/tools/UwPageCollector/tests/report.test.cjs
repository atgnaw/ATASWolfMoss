const { test } = require('node:test');
const assert = require('node:assert/strict');
const { JSDOM } = require('jsdom');
require('../extension/quality.js');
const Report = require('../extension/report.js');
function fixture() {
  const row = { key: 'one', identity: { ticker: 'QQQ', strike: '740', right: 'PUT', expiry: '2026-09-21' },
    bucket: { date: '2026-09-21', start: '14:30', end: '14:35', startUtc: '2026-09-21T18:30:00Z' }, volume: 13538,
    ba: { status: 'RECONSTRUCTED', counts: { bid: 6401, ask: 7060, neutral: 77 } },
    raw: { label: '52%', fields: [{ name: 'Premium', text: '$250K' }] }, capturedUtc: '2026-09-21T18:35:00Z', revision: 1 };
  return { schema: 'uw-page-collector/1', run: { id: 'test', version: '0.1.4', status: 'STOPPED', config: { date: '2026-09-21', timezone: 'America/New_York' } }, exportedUtc: row.capturedUtc, latest: [row], revisions: [row, row] };
}
function open(data) { return new JSDOM(Report.build(data), { runScripts: 'dangerously' }); }
test('HTML 数量、比例、当地时间与最新版本一致，不累加修订', () => {
  const dom = open(fixture()), doc = dom.window.document;
  assert.equal(doc.querySelectorAll('tbody tr').length, 1);
  assert.match(doc.querySelector('tbody').textContent, /6,401/);
  assert.match(doc.querySelector('tbody').textContent, /7,060/);
  assert.match(doc.querySelector('tbody').textContent, /52.15%/);
  assert.match(doc.body.textContent, /14:35:00/);
  assert.equal(doc.querySelector('#known').textContent, '1');
  assert.equal(doc.querySelectorAll('script[src],link,img,iframe').length, 0);
  dom.window.close();
});
test('补算和中文未知原因、零量、联合筛选及空结果', () => {
  const data = fixture();
  const inferred = structuredClone(data.latest[0]); inferred.key='two'; inferred.identity.strike='741'; inferred.ba.inference='unique-residual-from-total';
  const unknown = structuredClone(inferred); unknown.key='three'; unknown.ba={ status:'UNVERIFIED', reason:'MISSING_SEGMENT', counts:null };
  const zero = structuredClone(inferred); zero.key='four'; zero.volume=0; zero.ba={ status:'RECONSTRUCTED', counts:{bid:0,ask:0,neutral:0} };
  data.latest.push(inferred,unknown,zero);
  const dom=open(data), doc=dom.window.document;
  assert.equal(doc.querySelector('#total').textContent,'4');
  assert.equal(doc.querySelector('#inferred').textContent,'1');
  assert.match(doc.body.textContent,/缺段且无法补算/);
  assert.match(doc.body.textContent,/零成交量/);
  const search=doc.querySelector('#search'); search.value='QQQ 740'; search.dispatchEvent(new dom.window.Event('input'));
  assert.equal(doc.querySelector('#total').textContent,'1');
  search.value='SPX'; search.dispatchEvent(new dom.window.Event('input'));
  assert.equal(doc.querySelector('#empty').hidden,false);
  search.value=''; search.dispatchEvent(new dom.window.Event('input'));
  const quality=doc.querySelector('#quality'); quality.value='inferred'; quality.dispatchEvent(new dom.window.Event('change'));
  assert.equal(doc.querySelector('#total').textContent,'1');
  dom.window.close();
});
test('导出不执行页面文本，运行提示与空轮次正确', () => {
  const data=fixture(); data.run.status='RUNNING';
  data.latest[0].raw.label='</p><script>globalThis.PWNED=1</script><img src=x onerror="alert(1)">';
  const dom=open(data);
  assert.equal(dom.window.PWNED,undefined);
  assert.equal(dom.window.document.querySelectorAll('img').length,0);
  assert.match(dom.window.document.body.textContent,/采集仍在运行/);
  dom.window.close(); data.latest=[];
  const empty=open(data); assert.equal(empty.window.document.querySelector('#total').textContent,'0'); empty.window.close();
  assert.throws(()=>Report.build({}),/有效/);
});

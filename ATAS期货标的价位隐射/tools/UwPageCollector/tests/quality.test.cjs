const { test } = require('node:test');
const assert = require('node:assert/strict');
const { JSDOM } = require('jsdom');
const Quality = require('../extension/quality.js');
const rows = [...Quality.reasons.map(([reason]) => ({ ba: { status: 'UNVERIFIED', reason } })),
  { ba: { status: 'RECONSTRUCTED', reason: null } }, { ba: { status: 'UNVERIFIED', reason: 'NEW_CODE' } }];
test('原因分类互斥、未知代码兜底、总数与未还原行数一致', () => {
  const counts = Quality.summarize(rows);
  assert.equal(counts.OTHER, 2);
  assert.equal(counts.MISSING_SEGMENT, 1);
  assert.equal(Object.values(counts).reduce((a,b) => a+b, 0), rows.length - 1);
  assert.deepEqual(Quality.summarize(rows), counts);
  assert.equal(Object.values(Quality.summarize([])).reduce((a,b) => a+b, 0), 0);
});
test('界面展示中文分类数量、旧数据不补零、切换无残留', () => {
  const doc = new JSDOM('<section id="reasons"></section>').window.document, el = doc.querySelector('section');
  Quality.render(el, { lastRows: 9, lastVerified: 1, lastUnverifiedReasons: Quality.summarize(rows) });
  assert.match(el.textContent, /本页未还原原因 · 8 行/);
  assert.equal(el.querySelectorAll('li').length, 7);
  assert.equal(el.querySelector('li').textContent, '缺段且无法补算1 行');
  assert.ok(el.querySelector('li').title.includes('不能擅自补零'));
  Quality.render(el, { lastRows: 150, lastVerified: 137 });
  assert.match(el.textContent, /旧版本未记录/);
  assert.equal(el.querySelectorAll('li').length, 0);
  Quality.render(el, null); assert.equal(el.textContent, '');
});

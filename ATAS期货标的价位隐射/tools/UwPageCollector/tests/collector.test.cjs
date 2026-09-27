const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');
const { indexedDB } = require('fake-indexeddb');
const Core = require('../extension/core.js');
const Reader = require('../extension/reader.js');
const Quality = require('../extension/quality.js');
const config = { date: '2026-09-21', timezone: 'America/New_York', pollSeconds: 5, confirmed: true };
const raw = { interval: '14:30 - 14:35', ticker: 'QQQ', contract: '740 put 2026-09-21', volume: '13,538', bidPercent: '47.2817%', neutralPercent: '0.568769%', askPercent: '52.1495%' };
const headers = ['Interval', 'Ticker', 'Contract', 'Interval Vol', 'Interval B/A', 'Total B/A'];
function fixture(order = headers, missing = '') {
  const values = { Interval: raw.interval, Ticker: raw.ticker, Contract: raw.contract, 'Interval Vol': raw.volume,
    'Interval B/A': `<div id="duplicate">${missing === 'bid' ? '' : '<div class="left-side" style="width:47.2817%"></div>'}<div class="mid-side" style="width:0.568769%"></div><div class="right-side" style="width:52.1495%">52%</div></div>`,
    'Total B/A': '<div id="duplicate"><div class="left-side" style="width:90%"></div></div>' };
  return new JSDOM(`<button>Mon, Sep 21</button><table><tr>${order.map(h => `<td>${h}</td>`).join('')}</tr><tr><td colspan="6">Powered by unusualwhales.com</td></tr><tr>${order.map(h => `<td>${values[h]}</td>`).join('')}</tr></table>`).window.document;
}
const read = () => Reader.readPage(fixture(), 'https://unusualwhales.com/interval-flow?interval=10');
test('日期控件兼容真实 UW span[role=button] 和原生 button', () => {
  for (const markup of ['<span role="button" aria-expanded="false">Mon, Sep 21</span>', '<button role="button">Mon, Sep 21</button>', '<div role="button">Mon,\n Sep 21</div>']) {
    const doc = fixture(); doc.querySelector('button').outerHTML = markup;
    const page = Reader.readPage(doc, 'url');
    assert.equal(page.dateLabel, 'Mon, Sep 21');
    assert.equal(Core.dateLabelMatches(page.dateLabel, config.date), true);
    assert.equal(Core.dateLabelMatches(page.dateLabel, '2026-09-22'), false);
    assert.equal(Core.parseRow(page.rows[0], config).ba.counts.bid, 6401);
  }
});
test('日期控件缺失与多日期控件仍拒绝，给出不同原因', () => {
  const missing = fixture(); missing.querySelector('button').remove();
  assert.throws(() => Reader.readPage(missing, 'url'), /未识别到页面日期控件/);
  const multiple = fixture(); multiple.body.insertAdjacentHTML('beforeend', '<span role="button">Tue, Sep 22</span>');
  assert.throws(() => Reader.readPage(multiple, 'url'), /多个页面日期控件/);
});
test('用户手工核验样本：6401 / 7060 / 77', () => {
  assert.deepEqual(Core.parseRow(raw, config).ba.counts, { bid: 6401, ask: 7060, neutral: 77 });
});
test('另外四条页面样本的数学一致性（非人工真值）', () => {
  for (const [v, b, n, a, expected] of [
    [12977, '67.3037%', '1.79548%', '30.9008%', [8734, 4010, 233]],
    [9381, '45.3257%', '7.99488%', '46.6795%', [4252, 4379, 750]],
    [5142, '10.0739%', '1.53637%', '88.3897%', [518, 4545, 79]],
    [4884, '45.3931%', '11.0156%', '43.5913%', [2217, 2129, 538]]]) {
    const result = Core.reconstruct(v, [b, n, a]);
    assert.deepEqual(result.counts, { bid: expected[0], ask: expected[1], neutral: expected[2] });
  }
});
test('缺段且其他比例精度不足时仍保留未知', () => {
  for (let i = 0; i < 3; i++) { const widths = ['50%', '0%', '50%']; widths[i] = null;
    assert.equal(Core.reconstruct(100, widths).reason, 'MISSING_SEGMENT'); }
});
test('单侧 DOM 满宽支持纯红、纯绿、纯灰，并标明规则假设', () => {
  for (let i = 0; i < 3; i++) {
    const widths = [null, null, null]; widths[i] = '100%';
    const r = Core.reconstruct(10000, widths);
    assert.equal(r.status, 'RECONSTRUCTED');
    assert.equal(r.counts[['bid','neutral','ask'][i]], 10000);
    assert.equal(Object.values(r.counts).reduce((a,b)=>a+b, 0), 10000);
    assert.equal(r.inferredSides.length, 2);
    assert.equal(r.inference, 'single-full-width-assumes-omitted-sides-zero');
  }
});
test('缺灰色零数量、非零剩余均可补算；整条缺失或残余多解不猜测', () => {
  assert.deepEqual(Core.reconstruct(1000, ['40.000%', null, '60.000%']).counts, {bid:400, neutral:0, ask:600});
  assert.deepEqual(Core.reconstruct(1000, ['40.000%', null, '59.000%']).counts, {bid:400, neutral:10, ask:590});
  assert.equal(Core.reconstruct(1000, [null,null,null]).reason, 'MISSING_SEGMENT');
  assert.equal(Core.reconstruct(1000, ['99%',null,null]).counts, null);
  assert.equal(Core.reconstruct(1000, ['70%',null,'60%']).reason, 'INCONSISTENT_SUM');
});
test('低精度、多解、不一致、非法数字和极大数量', () => {
  assert.equal(Core.reconstruct(250, ['38%', '26%', '36%']).reason, 'AMBIGUOUS_INTEGER');
  assert.equal(Core.reconstruct(100, ['80%', '30%', '50%']).reason, 'INCONSISTENT_SUM');
  assert.equal(Core.reconstruct(100, ['101%', '0%', '0%']).reason, 'INVALID_PERCENT');
  assert.equal(Core.reconstruct(100, ['NaN%', '0%', '0%']).counts, null);
  assert.equal(Core.reconstruct(1e10, ['50%', '0%', '50%']).counts, null);
  for (const input of ['1K', '-1', '', '1,2', 'Infinity']) assert.equal(Core.integer(input), null);
});
test('零成交只有三个显式百分比才接受；不产生除零', () => {
  assert.deepEqual(Core.reconstruct(0, ['0%', '0%', '0%']).counts, { bid: 0, ask: 0, neutral: 0 });
});
test('合计约束能缩小整数范围，不舍入强凑', () => {
  const result = Core.reconstruct(100, ['33.333%', '33.333%', '33.334%']);
  assert.equal(result.counts, null);
});
test('实际桶边界不取 URL interval 参数', () => {
  const row = Core.parseRow(read().rows[0], config);
  assert.equal(row.bucket.minutes, 5); assert.equal(row.bucket.startUtc, '2026-09-21T18:30:00.000Z');
});
test('纽约夏令时与冬令时、UTC+8', () => {
  assert.equal(Core.toUtc('2026-01-12', '09:30', 'America/New_York'), '2026-01-12T14:30:00.000Z');
  assert.equal(Core.toUtc('2026-09-21', '09:30', 'America/New_York'), '2026-09-21T13:30:00.000Z');
  assert.equal(Core.toUtc('2026-09-21', '21:30', 'Asia/Singapore'), '2026-09-21T13:30:00.000Z');
});
test('非法日期、跨午夜、DST 重复或不存在时间拒绝猜测', () => {
  assert.equal(Core.validDate('2026-02-30'), false);
  assert.throws(() => Core.bucket(config.date, '23:55 - 00:00', config.timezone));
  assert.throws(() => Core.toUtc('2026-11-01', '01:30', 'America/New_York'));
  assert.throws(() => Core.toUtc('2026-03-08', '02:30', 'America/New_York'));
});
test('日期按钮核对；expiry 不作为交易日期', () => {
  assert.equal(Core.dateLabelMatches('Mon, Sep 21', config.date), true);
  assert.equal(Core.dateLabelMatches('Tue, Sep 22', config.date), false);
  assert.equal(Core.parseRow({ ...raw, contract: '740 put 2026-09-25' }, config).bucket.date, config.date);
});
test('合约/到期日/方向/桶作为独立身份，SPXW 不混为 SPX', () => {
  const base = Core.parseRow(raw, config).key;
  for (const patch of [{ ticker: 'SPXW' }, { contract: '740 call 2026-09-21' }, { contract: '740 put 2026-09-22' }, { interval: '14:35 - 14:40' }]) assert.notEqual(Core.parseRow({ ...raw, ...patch }, config).key, base);
});
test('DOM 表头重排可读，Total B/A 重复 ID 不污染 Interval B/A', () => {
  for (const order of [headers, [...headers].reverse()]) {
    const page = Reader.readPage(fixture(order), 'https://unusualwhales.com/interval-flow');
    assert.equal(page.rows.length, 1); assert.equal(page.rows[0].bidPercent, raw.bidPercent);
    assert.equal(Core.parseRow(page.rows[0], config).ba.counts.bid, 6401);
  }
});
test('DOM 缺少色段保留整行，必需列丢失直接报错', () => {
  const page = Reader.readPage(fixture(headers, 'bid'), 'url');
  assert.equal(page.rows.length, 1); assert.equal(Core.parseRow(page.rows[0], config).ba.counts.bid, 6401);
  assert.throws(() => Reader.readPage(fixture(headers.filter(h => h !== 'Interval B/A')), 'url'));
});
test('不读取页面账户文本', () => {
  const doc = fixture(); doc.body.insertAdjacentHTML('beforeend', '<aside>ACCOUNT SECRET</aside>');
  assert.ok(!JSON.stringify(Reader.readPage(doc, 'url')).includes('SECRET'));
});
test('IndexedDB：重复不累加，修订保留、导出一致、事务失败不破坏旧记录', async () => {
  global.indexedDB = indexedDB; require('../extension/store.js');
  const store = global.UwStore, page = read();
  const run = { id: 'test', status: 'RUNNING', config, url: page.url, dateLabel: page.dateLabel, headers: page.headers, startedUtc: '2026-09-21T18:30:00Z', bytes: 0, sequence: 0, uniqueRows: 0, polls: 0, gaps: 0, maxGapMs: 0, emptyPolls: 0 };
  await store.put(run);
  await store.append(run.id, page, '2026-09-21T18:31:00Z');
  let result = await store.append(run.id, page, '2026-09-21T18:31:05Z');
  assert.equal(result.sequence, 1); assert.equal(result.uniqueRows, 1);
  const changed = structuredClone(page); changed.rows[0].volume = '13,000';
  await store.append(run.id, changed, '2026-09-21T18:31:10Z');
  let exported = await store.exported(run.id);
  assert.equal(exported.latest[0].volume, 13000); assert.equal(exported.revisions.length, 2);
  assert.equal(Object.values(exported.run.lastUnverifiedReasons).reduce((a,b) => a+b, 0), exported.run.lastRows - exported.run.lastVerified);
  const repeat = await store.append(run.id, changed, '2026-09-21T18:31:15Z');
  assert.deepEqual(repeat.lastUnverifiedReasons, exported.run.lastUnverifiedReasons);
  assert.equal(exported.latest[0].volumeDecreased, true);
  await store.append(run.id, changed, '2026-09-21T18:35:05Z');
  exported = await store.exported(run.id);
  assert.equal(exported.latest[0].timeState, 'ENDED_NOT_FINAL'); assert.equal(exported.revisions.length, 3);
  const invalid = structuredClone(page); invalid.rows.push({ ...raw, contract: 'broken' });
  await assert.rejects(store.append(run.id, invalid, '2026-09-21T18:35:10Z'));
  assert.equal((await store.exported(run.id)).revisions.length, 3);
  await assert.rejects(store.append(run.id, { ...page, dateLabel: 'Tue, Sep 22' }, '2026-09-21T18:35:10Z'));
  await assert.rejects(store.append(run.id, { ...page, url: page.url + '&x=1' }, '2026-09-21T18:35:10Z'));
  const full = await store.get(run.id); full.bytes = 64 * 1024 * 1024; await store.put(full);
  await assert.rejects(store.append(run.id, page, '2026-09-21T18:35:10Z'), /64 MiB/);
  await assert.rejects(store.remove(run.id), /停止/);
  full.status = 'STOPPED'; await store.put(full); await store.remove(run.id);
  assert.equal((await store.exported(run.id)).latest.length, 0);
});
test('所有 JS 语法有效，扩展无远程脚本、无 cookies/debugger/webRequest 权限', () => {
  const dir = path.join(__dirname, '../extension');
  for (const file of fs.readdirSync(dir).filter(f => f.endsWith('.js'))) new vm.Script(fs.readFileSync(path.join(dir, file), 'utf8'));
  const manifest = JSON.parse(fs.readFileSync(path.join(dir, 'manifest.json')));
  assert.equal(manifest.manifest_version, 3);
  assert.deepEqual(manifest.permissions || [], []);
  assert.equal(manifest.content_scripts[0].matches.length, 1);
  assert.ok(manifest.content_security_policy.extension_pages.includes("connect-src 'none'"));
});

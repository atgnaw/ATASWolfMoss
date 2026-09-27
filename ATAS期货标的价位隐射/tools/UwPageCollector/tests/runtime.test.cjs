const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const { indexedDB } = require('fake-indexeddb');
const { webcrypto } = require('node:crypto');
const config = { date: '2026-09-21', timezone: 'America/New_York', pollSeconds: 5, confirmed: true };
const page = { url: 'https://unusualwhales.com/interval-flow?interval=10', dateLabel: 'Mon, Sep 21', headers: ['Interval', 'Ticker', 'Contract', 'Interval Vol', 'Interval B/A'], warnings: [], rows: [{ interval: '14:30 - 14:35', ticker: 'QQQ', contract: '740 put 2026-09-21', volume: '13,538', bidPercent: '47.2817%', neutralPercent: '0.568769%', askPercent: '52.1495%' }] };
function harness() {
  let listener, removed, startup, sent = [];
  const context = vm.createContext({ indexedDB, Intl, TextEncoder, URL, crypto: webcrypto, console,
    chrome: {
      runtime: { getURL: file => 'chrome-extension://test/' + file, onMessage: { addListener: fn => listener = fn }, onStartup: { addListener: fn => startup = fn } },
      action: { onClicked: { addListener() {} } },
      tabs: { query: async () => [{ id: 1, url: page.url, title: 'UW' }], get: async () => ({ id: 1, url: page.url }),
        sendMessage: async (id, message) => { sent.push(message); return message.type === 'inspect' ? { ok: true, page: structuredClone(page), documentId: 'doc' } : { ok: true }; },
        onRemoved: { addListener: fn => removed = fn } }
    }
  });
  context.importScripts = (...names) => names.forEach(n => vm.runInContext(fs.readFileSync(path.join(__dirname, '../extension', n), 'utf8'), context));
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../extension/background.js'), 'utf8'), context);
  const ui = { url: 'chrome-extension://test/console.html', tab: { id: 9 } };
  const content = { url: page.url, tab: { id: 1 }, frameId: 0 };
  const send = (m, sender = ui) => new Promise(resolve => listener(m, sender, resolve));
  return { send, content, sent, context, removed: () => removed(1), startup: () => startup() };
}
test('运行生命周期、来源校验、worker 重建、刷新、关闭、重复启动与删除', async () => {
  let h = harness();
  let r = await h.send({ type: 'start', tabId: 1, config }); assert.equal(r.ok, true, r.error);
  const id = r.value.id;
  assert.equal(r.value.uniqueRows, 1); assert.equal(h.sent.at(-1).type, 'activate');
  assert.equal((await h.send({ type: 'start', tabId: 1, config })).ok, false);
  assert.equal((await h.send({ type: 'list' }, { url: 'https://evil.example' })).ok, false);
  assert.equal((await h.send({ type: 'capture', runId: id, documentId: 'doc', page }, { ...h.content, tab: { id: 2 } })).ok, false);
  assert.equal((await h.send({ type: 'capture', runId: id, documentId: 'doc', page }, { ...h.content, frameId: 1 })).ok, false);
  h = harness(); // Simulated service-worker suspension: no in-memory run state carried over.
  r = await h.send({ type: 'capture', runId: id, documentId: 'doc', page }, h.content);
  assert.equal(r.ok, true, r.error); assert.equal(r.value.sequence, 1);
  await h.send({ type: 'hello', documentId: 'new-doc' }, h.content);
  r = await h.send({ type: 'list' }); assert.equal(r.value[0].status, 'STOPPED');
  assert.equal((await h.send({ type: 'capture', runId: id, documentId: 'doc', page }, h.content)).ok, false);
  await h.send({ type: 'delete', id });
  r = await h.send({ type: 'start', tabId: 1, config }); const closed = r.value.id;
  h.removed(); await h.send({ type: 'list' });
  assert.equal((await h.send({ type: 'list' })).value[0].status, 'STOPPED');
  await h.send({ type: 'delete', id: closed });
  r = await h.send({ type: 'start', tabId: 1, config }); const restart = r.value.id;
  h.startup(); await h.send({ type: 'list' });
  assert.equal((await h.send({ type: 'list' })).value[0].stopReason, '浏览器重新启动，不自动恢复');
  await h.send({ type: 'delete', id: restart });
});
test('读取失败与 URL 改变停止采集、保留已有快照', async () => {
  const h = harness();
  for (const message of [{ type: 'pageError', error: '找不到表格' }, { type: 'capture', page: { ...page, url: page.url + '&changed=true' } }]) {
    const started = await h.send({ type: 'start', tabId: 1, config }); const id = started.value.id;
    const response = await h.send({ ...message, runId: id, documentId: 'doc' }, h.content);
    assert.equal(response.ok, false);
    const stored = await h.send({ type: 'list' }); assert.equal(stored.value[0].status, 'STOPPED'); assert.equal(stored.value[0].uniqueRows, 1);
    await h.send({ type: 'delete', id });
  }
});

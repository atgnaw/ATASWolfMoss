importScripts('core.js', 'quality.js', 'store.js');
'use strict';
let serial = Promise.resolve();
function enqueue(work) { const next = serial.then(work); serial = next.catch(() => {}); return next; }
const now = () => new Date().toISOString();
const isUw = url => { try { const u = new URL(url); return u.origin === 'https://unusualwhales.com' && u.pathname === '/interval-flow'; } catch { return false; } };
async function stop(id, reason = '用户停止') {
  const run = await UwStore.get(id);
  if (!run || run.status !== 'RUNNING') return run;
  run.status = 'STOPPED'; run.stopReason = reason; run.stoppedUtc = now();
  await UwStore.put(run);
  await chrome.tabs.sendMessage(run.tabId, { type: 'stop', runId: id }).catch(() => {});
  return run;
}
async function handle(message, sender) {
  const fromUi = sender.url === chrome.runtime.getURL('console.html');
  if (message.type === 'capture' || message.type === 'pageError' || message.type === 'hello') {
    if (!sender.tab || !isUw(sender.url) || sender.frameId !== 0) throw Error('未授权的页面来源');
    if (message.type === 'hello') {
      for (const run of await UwStore.list()) if (run.status === 'RUNNING' && run.tabId === sender.tab.id && run.documentId !== message.documentId) await stop(run.id, '页面刷新或导航，需重新开始');
      return {};
    }
    const run = await UwStore.get(message.runId);
    if (!run || run.status !== 'RUNNING' || run.tabId !== sender.tab.id || run.documentId !== message.documentId) throw Error('采集绑定已失效');
    try {
      if (message.type === 'pageError') throw Error(String(message.error).slice(0, 500));
      if (JSON.stringify(message.page).length > 2 * 1024 * 1024) throw Error('页面快照过大');
      return await UwStore.append(run.id, message.page, now());
    } catch (e) { await stop(run.id, e.message); throw e; }
  }
  if (!fromUi) throw Error('只允许扩展控制台操作');
  switch (message.type) {
    case 'tabs': return (await chrome.tabs.query({ url: 'https://unusualwhales.com/interval-flow*' })).filter(t => isUw(t.url)).map(t => ({ id: t.id, title: t.title, url: t.url }));
    case 'list': return (await UwStore.list()).sort((a, b) => b.startedUtc.localeCompare(a.startedUtc));
    case 'stop': return stop(message.id);
    case 'delete': return UwStore.remove(message.id);
    case 'start': {
      const all = await UwStore.list();
      if (all.some(r => r.status === 'RUNNING')) throw Error('已有一轮运行中，请先停止；一次仅采集一个页面');
      if (all.length >= 100 || all.reduce((s, r) => s + r.bytes, 0) >= 256 * 1024 * 1024) throw Error('本地存储达到保护上限；先导出并删除旧轮次');
      const config = message.config;
      if (!config?.confirmed || !UwCore.validDate(config.date) || ![2, 5, 10, 30].includes(config.pollSeconds) || !['America/New_York', 'Asia/Singapore', 'UTC'].includes(config.timezone)) throw Error('请确认日期、时区和读取间隔');
      UwCore.toUtc(config.date, '12:00', config.timezone);
      const tab = await chrome.tabs.get(message.tabId);
      if (!isUw(tab.url)) throw Error('请选择 UW Interval Flow 页面');
      const inspected = await chrome.tabs.sendMessage(tab.id, { type: 'inspect' }).catch(() => { throw Error('页面读取脚本未就绪，请刷新 UW 页面后重试'); });
      if (!inspected.ok) throw Error(inspected.error);
      const page = inspected.page;
      if (JSON.stringify(page).length > 2 * 1024 * 1024) throw Error('页面快照过大');
      if (!UwCore.dateLabelMatches(page.dateLabel, config.date)) throw Error(`页面显示 ${page.dateLabel}，与输入日期不一致`);
      if (!page.rows.length) throw Error('页面没有合约行，请检查登录、筛选与加载状态');
      const id = crypto.randomUUID();
      const run = { id, version: '0.1.4', tabId: tab.id, documentId: inspected.documentId, url: page.url, dateLabel: page.dateLabel,
        headers: page.headers, config, status: 'RUNNING', startedUtc: now(), sequence: 0, uniqueRows: 0, bytes: 0, polls: 0,
        gaps: 0, maxGapMs: 0, emptyPolls: 0, lastMessage: '启动中' };
      await UwStore.put(run);
      try {
        await UwStore.append(id, page, now());
        const activation = await chrome.tabs.sendMessage(tab.id, { type: 'activate', runId: id, documentId: inspected.documentId, pollSeconds: config.pollSeconds });
        if (!activation?.ok) throw Error(activation?.error || '页面采集器启动失败');
      } catch (e) { await stop(id, e.message); throw e; }
      return await UwStore.get(id);
    }
    default: throw Error('未知操作');
  }
}
chrome.runtime.onMessage.addListener((message, sender, respond) => {
  enqueue(() => handle(message, sender)).then(value => respond({ ok: true, value }), e => respond({ ok: false, error: e.message }));
  return true;
});
chrome.action.onClicked.addListener(() => chrome.tabs.create({ url: chrome.runtime.getURL('console.html') }));
chrome.tabs.onRemoved.addListener(tabId => enqueue(async () => { for (const run of await UwStore.list()) if (run.tabId === tabId) await stop(run.id, '目标标签页已关闭'); }).catch(() => {}));
chrome.runtime.onStartup.addListener(() => enqueue(async () => { for (const run of await UwStore.list()) await stop(run.id, '浏览器重新启动，不自动恢复'); }).catch(() => {}));

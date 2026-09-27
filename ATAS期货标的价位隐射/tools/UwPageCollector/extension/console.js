'use strict';
const $ = id => document.getElementById(id);
let runs = [], updating = false;
const error = e => { $('error').textContent = e?.message || String(e); $('error').hidden = false; };
async function send(type, extra = {}) {
  const r = await chrome.runtime.sendMessage({ type, ...extra });
  if (!r?.ok) throw Error(r?.error || '扩展后台没有响应');
  return r.value;
}
async function targets() {
  const tabs = await send('tabs'), current = $('target').value;
  $('target').replaceChildren();
  for (const tab of tabs) $('target').add(new Option(`#${tab.id} · ${tab.title}`, tab.id));
  if (!tabs.length) $('target').add(new Option('请先登录并打开 UW Interval Flow 页面', ''));
  if (tabs.some(t => String(t.id) === current)) $('target').value = current;
}
function selected() { return runs.find(r => r.id === $('runs').value); }
function display() {
  const r = selected();
  UwQuality.render($('reasons'), r);
  $('stop').disabled = !r || r.status !== 'RUNNING'; $('export').disabled = !r; $('delete').disabled = !r || r.status === 'RUNNING';
  $('exportHtml').disabled = !r;
  $('start').disabled = runs.some(x => x.status === 'RUNNING');
  if (!r) { $('status').textContent = '暂无记录'; for (const id of ['unique','revisions','quality','size']) $(id).textContent = '—'; $('detail').textContent = ''; return; }
  const stale = r.status === 'RUNNING' && Date.now() - Date.parse(r.lastPollUtc || r.startedUtc) > Math.max(20000, r.config.pollSeconds * 4000);
  $('status').textContent = `${r.status === 'RUNNING' ? (stale ? '⚠ 读取已间断，请检查页面（必要时停止并重新开始）' : '● 正在读取页面') : '■ 已停止：' + r.stopReason}\n${r.lastMessage || ''}`;
  $('unique').textContent = r.uniqueRows.toLocaleString(); $('revisions').textContent = r.sequence.toLocaleString();
  $('quality').textContent = `${r.lastVerified ?? 0} / ${r.lastRows ?? 0}`;
  $('size').textContent = `${(r.bytes / 1048576).toFixed(2)} MiB`;
  const time = s => s ? new Date(s).toLocaleString() : '尚无';
  $('detail').textContent = `页面日期 ${r.config.date} · ${r.config.timezone}｜最后读取 ${time(r.lastPollUtc)}｜最后内容变化 ${time(r.lastChangeUtc)}｜间断 ${r.gaps} 次，最长 ${(r.maxGapMs / 1000).toFixed(1)} 秒｜${r.pageHidden ? 'UW 在后台，可能降频' : 'UW 在前台'}。以上接收时间按本机时区显示。`;
}
async function refresh(prefer) {
  if (updating) return; updating = true;
  try {
    const current = prefer || $('runs').value; runs = await send('list'); $('runs').replaceChildren();
    for (const r of runs) $('runs').add(new Option(`${r.config.date} · ${new Date(r.startedUtc).toLocaleTimeString()} · ${r.status === 'RUNNING' ? '运行中' : '已停止'} · ${r.uniqueRows} 桶`, r.id));
    if (runs.some(r => r.id === current)) $('runs').value = current;
    display();
  } finally { updating = false; }
}
function action(id, work) { $(id).addEventListener('click', async () => { $('error').hidden = true; try { await work(); } catch (e) { error(e); } }); }
$('form').addEventListener('submit', async e => {
  e.preventDefault(); $('error').hidden = true; $('start').disabled = true;
  try {
    const r = await send('start', { tabId: Number($('target').value), config: { date: $('date').value, timezone: $('zone').value, pollSeconds: Number($('seconds').value), confirmed: $('confirmed').checked } });
    await refresh(r.id);
  } catch (e) { error(e); await refresh(); }
});
action('refresh', targets); action('reload', () => refresh());
action('stop', async () => { await send('stop', { id: selected().id }); await refresh(); });
action('exportHtml', async () => {
  const r = selected(), data = await UwStore.exported(r.id);
  const url = URL.createObjectURL(new Blob([UwReport.build(data)], { type: 'text/html;charset=utf-8' }));
  const a = document.createElement('a'); a.href = url; a.download = `uw-${r.config.date}-${r.id}.html`; a.click();
  setTimeout(() => URL.revokeObjectURL(url), 60000);
});
action('export', async () => {
  // Read in the extension page, not a huge runtime message. All three stores share a consistent transaction.
  const r = selected(), data = await UwStore.exported(r.id);
  const blob = new Blob([JSON.stringify(data)], { type: 'application/json' }), url = URL.createObjectURL(blob);
  const a = document.createElement('a'); a.href = url; a.download = `uw-${r.config.date}-${r.id}.json`; a.click();
  setTimeout(() => URL.revokeObjectURL(url), 60000);
});
action('delete', async () => {
  const r = selected();
  if (!confirm(`永久删除本轮 ${r.config.date} 的 ${r.uniqueRows} 个桶及全部修订？请先确认已导出，删除无法撤销。`)) return;
  await send('delete', { id: r.id }); await refresh();
});
$('runs').addEventListener('change', display);
Promise.all([targets(), refresh()]).catch(error);
setInterval(() => refresh().catch(error), 3000);

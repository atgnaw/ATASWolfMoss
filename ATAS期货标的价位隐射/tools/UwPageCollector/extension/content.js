(() => {
  'use strict';
  const documentId = crypto.randomUUID();
  let run = null, timer = null, busy = false;
  const page = () => ({ ...UwReader.readPage(document, location.href), hidden: document.hidden, readUtc: new Date().toISOString() });
  function halt() { clearTimeout(timer); timer = null; run = null; }
  async function tick() {
    if (!run) return;
    if (busy) { timer = setTimeout(tick, 1000); return; }
    const current = run; busy = true;
    try {
      let message;
      try { message = { type: 'capture', page: page() }; }
      catch (e) { message = { type: 'pageError', error: e.message }; }
      const response = await chrome.runtime.sendMessage({ ...message, runId: current.id, documentId });
      if (!response?.ok && run === current) halt();
    } catch { if (run === current) halt(); } // Do not let a late reply stop a newly started run.
    finally { busy = false; if (run === current) timer = setTimeout(tick, current.seconds * 1000); }
  }
  chrome.runtime.onMessage.addListener((message, sender, respond) => {
    if (sender.id !== chrome.runtime.id) return;
    try {
      if (message.type === 'inspect') respond({ ok: true, documentId, page: page() });
      if (message.type === 'activate') {
        if (message.documentId !== documentId) throw Error('页面已重载');
        halt(); run = { id: message.runId, seconds: message.pollSeconds };
        timer = setTimeout(tick, run.seconds * 1000); respond({ ok: true });
      }
      if (message.type === 'stop' && run?.id === message.runId) { halt(); respond({ ok: true }); }
    } catch (e) { respond({ ok: false, error: e.message }); }
  });
  chrome.runtime.sendMessage({ type: 'hello', documentId }).catch(() => {});
})();

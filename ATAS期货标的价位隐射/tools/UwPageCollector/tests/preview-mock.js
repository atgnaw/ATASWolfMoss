// Mock UI transport. No UW page access and no real capture.
let previewRuns = [];
window.chrome = { runtime: { sendMessage: async m => {
  if (m.type === 'tabs') return { ok: true, value: [{ id: 1, title: '模拟页面 · 不采集真实数据' }] };
  if (m.type === 'list') return { ok: true, value: previewRuns };
  if (m.type === 'start') {
    const at = new Date().toISOString();
    const run = { id: 'preview', config: m.config, status: 'RUNNING', startedUtc: at, lastPollUtc: at, lastChangeUtc: at, lastMessage: '模拟状态：本页 150 行；121 行可唯一还原', uniqueRows: 150, sequence: 223, lastVerified: 121, lastRows: 150, bytes: 137200, gaps: 0, maxGapMs: 0, pageHidden: false };
    previewRuns = [run]; return { ok: true, value: run };
  }
  if (m.type === 'stop') { previewRuns[0].status = 'STOPPED'; previewRuns[0].stopReason = '用户停止（预览）'; return { ok: true }; }
  return { ok: false, error: '仅用于界面预览；无真实采集、导出或删除' };
} } };

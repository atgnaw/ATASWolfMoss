(function (root) {
  'use strict';
  function readPage(doc, url) {
    const norm = root.UwCore.normalize;
    const required = ['Interval', 'Ticker', 'Contract', 'Interval Vol', 'Interval B/A'];
    const candidates = [];
    for (const table of doc.querySelectorAll('table')) {
      for (const row of table.rows) {
        const headers = Array.from(row.cells, c => norm(c.innerText ?? c.textContent));
        if (required.every(h => headers.includes(h))) candidates.push({ table, header: row, headers });
      }
    }
    if (candidates.length !== 1) throw Error('未找到唯一的 Interval Flow 表格；请检查登录状态与列设置');
    const { table, header, headers } = candidates[0];
    if (required.some(h => headers.filter(x => x === h).length !== 1)) throw Error('必要列名重复');
    // UW uses both native buttons and semantic buttons (e.g. span[role=button]).
    const dateLabels = Array.from(doc.querySelectorAll('button, [role="button"]'), b => norm(b.innerText ?? b.textContent))
      .filter(t => /^(Mon|Tue|Wed|Thu|Fri|Sat|Sun),?\s+[A-Z][a-z]{2}\s+\d{1,2}$/.test(t));
    if (dateLabels.length === 0) throw Error('未识别到页面日期控件；请检查 UW 日期是否已加载，或页面日期格式是否变化');
    if (dateLabels.length > 1) throw Error('发现多个页面日期控件，无法唯一确认日期；请关闭日期弹窗后重试');
    const rows = [], warnings = [];
    for (const tr of table.rows) {
      if (tr === header || tr.cells.length <= 1) continue;
      if (tr.cells.length < headers.length) { warnings.push('列数不足，已跳过一行'); continue; }
      const cell = h => tr.cells[headers.indexOf(h)];
      const txt = h => norm(cell(h)?.innerText ?? cell(h)?.textContent);
      const bar = cell('Interval B/A');
      const width = cls => {
        const nodes = bar.querySelectorAll(cls);
        return nodes.length === 1 ? nodes[0].style.width || null : null;
      };
      rows.push({ interval: txt('Interval'), ticker: txt('Ticker'), contract: txt('Contract'), volume: txt('Interval Vol'),
        bidPercent: width('.left-side'), neutralPercent: width('.mid-side'), askPercent: width('.right-side'),
        label: norm(bar.innerText ?? bar.textContent),
        // Only market table cells: never read account menus, cookies, or framework state.
        fields: headers.map((name, i) => ({ name, text: norm(tr.cells[i]?.innerText ?? tr.cells[i]?.textContent) })) });
    }
    if (rows.length > 2000) throw Error('单页超过 2000 行，停止以保护内存');
    return { url, dateLabel: dateLabels[0], headers, rows, warnings };
  }
  root.UwReader = { readPage };
  if (typeof module !== 'undefined') module.exports = { readPage };
})(globalThis);

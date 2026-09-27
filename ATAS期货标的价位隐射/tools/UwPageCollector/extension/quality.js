(function (root) {
  'use strict';
  const reasons = [
    ['MISSING_SEGMENT', '缺段且无法补算', '没有可用色段，或剩余比例与总量仍不能唯一确定缺失侧；不能擅自补零。旧版记录可能仅因缺段而归入此类。'],
    ['AMBIGUOUS_INTEGER', '比例精度不足', '百分比对应多个可能的整数数量，无法唯一还原。'],
    ['INCONSISTENT_SUM', '数量合计不一致', '百分比允许的数量范围无法与区间总成交量对齐。'],
    ['INVALID_VOLUME', '成交量格式异常', '区间成交量无法解析为支持范围内的非负整数。'],
    ['INVALID_PERCENT', '百分比格式异常', '色条百分比无法解析或超出 0%–100%。'],
    ['INVALID_SEGMENTS', '色段结构异常', '传入的色段数量不符合三侧结构。'],
    ['OTHER', '其他原因', '出现未识别的原因代码，原始记录仍保留。']
  ];
  function summarize(rows) {
    const counts = Object.fromEntries(reasons.map(([code]) => [code, 0]));
    for (const row of rows) {
      if (row.ba.status === 'RECONSTRUCTED') continue;
      const code = Object.hasOwn(counts, row.ba.reason) ? row.ba.reason : 'OTHER';
      counts[code]++;
    }
    return counts;
  }
  function render(container, run) {
    container.replaceChildren();
    if (!run) return;
    const doc = container.ownerDocument, heading = doc.createElement('h3');
    const total = (run.lastRows ?? 0) - (run.lastVerified ?? 0);
    heading.textContent = `本页未还原原因 · ${total} 行`;
    container.append(heading);
    const note = doc.createElement('p'); note.className = 'muted';
    if (!run.lastUnverifiedReasons) {
      note.textContent = '此轮旧版本未记录本页原因分类；新建采集后显示。不会将未记录的分类填成零。';
      container.append(note); return;
    }
    note.textContent = `按最后一次成功读取的页面统计，不累计轮询次数；每行只计入一个主要原因。已还原中通过缺段补算 ${run.lastInferredRows ?? '未记录'} 行（含单色满宽规则）。鼠标停在分类上可查看说明。`;
    container.append(note);
    const list = doc.createElement('ul'); list.className = 'reason-list';
    for (const [code, label, description] of reasons) {
      const item = doc.createElement('li'); item.title = description;
      const name = doc.createElement('span'); name.textContent = label;
      const value = doc.createElement('strong'); value.textContent = `${run.lastUnverifiedReasons[code] ?? 0} 行`;
      item.append(name, value); list.append(item);
    }
    container.append(list);
  }
  root.UwQuality = { reasons, summarize, render };
  if (typeof module !== 'undefined') module.exports = root.UwQuality;
})(globalThis);

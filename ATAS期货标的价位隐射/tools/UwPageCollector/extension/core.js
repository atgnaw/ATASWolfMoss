/* Pure, shared parsing. No network, browser state, or floating-point count reconstruction. */
(function (root) {
  'use strict';
  const normalize = s => String(s ?? '').replace(/\s+/g, ' ').trim();
  function validDate(s) {
    return /^\d{4}-\d{2}-\d{2}$/.test(s) && !isNaN(Date.parse(s)) && new Date(s).toISOString().slice(0, 10) === s;
  }
  function integer(s) {
    s = normalize(s);
    if (!/^(?:\d+|\d{1,3}(?:,\d{3})+)$/.test(s)) return null;
    const n = Number(s.replaceAll(',', ''));
    return Number.isSafeInteger(n) && n >= 0 && n <= 1e9 ? n : null;
  }
  function countRange(volume, percent) {
    const m = /^(\d+)(?:\.(\d{1,12}))?%$/.exec(percent ?? '');
    if (!m) return null;
    const scale = 10n ** BigInt((m[2] || '').length);
    const p = BigInt(m[1] + (m[2] || ''));
    if (p > 100n * scale) return null;
    // Conservative ± one last displayed decimal unit: includes rounding/truncation uncertainty.
    const v = BigInt(volume), denominator = 100n * scale;
    const lo = p > 0n ? (v * (p - 1n) + denominator - 1n) / denominator : 0n;
    const hi = v * (p + 1n) / denominator;
    return [Number(lo), Number(hi > v ? v : hi)];
  }
  function reconstruct(volume, widths) {
    const result = { status: 'UNVERIFIED', counts: null, ranges: null,
      method: 'css-percent-integer-sum-v2', tolerance: '±1 last displayed decimal unit; not tooltip-verified' };
    if (volume === null || !Number.isSafeInteger(volume) || volume < 0 || volume > 1e9) return { ...result, reason: 'INVALID_VOLUME' };
    if (widths.length !== 3) return { ...result, reason: 'INVALID_SEGMENTS' };
    const missing = widths.map(w => w == null || w === '');
    const inferredSides = ['bid', 'neutral', 'ask'].filter((_, i) => missing[i]);
    result.inferredSides = inferredSides;
    if (missing.every(Boolean)) return { ...result, reason: 'MISSING_SEGMENT' };
    // A single DOM segment explicitly filling the bar is treated as the site's
    // full-side display convention, not as an integer solution from a rounded label.
    const full = widths.findIndex(w => /^100(?:\.0+)?%$/.test(w ?? ''));
    if (inferredSides.length === 2 && full >= 0) {
      const values = widths.map((_, i) => i === full ? volume : 0);
      return { ...result, status: 'RECONSTRUCTED', reason: null,
        inference: 'single-full-width-assumes-omitted-sides-zero',
        ranges: values.map(v => [v, v]), counts: { bid: values[0], neutral: values[1], ask: values[2] } };
    }
    const ranges = widths.map((w, i) => missing[i] ? [0, volume] : countRange(volume, w));
    if (ranges.some(r => !r)) return { ...result, reason: 'INVALID_PERCENT' };
    const low = ranges.reduce((s, r) => s + r[0], 0), high = ranges.reduce((s, r) => s + r[1], 0);
    if (ranges.some(r => r[0] > r[1]) || low > volume || high < volume) return { ...result, ranges, reason: 'INCONSISTENT_SUM' };
    const tightened = ranges.map(r => [Math.max(r[0], volume - (high - r[1])), Math.min(r[1], volume - (low - r[0]))]);
    if (tightened.some(r => r[0] !== r[1])) return { ...result, ranges: tightened, reason: inferredSides.length ? 'MISSING_SEGMENT' : 'AMBIGUOUS_INTEGER' };
    const [bid, neutral, ask] = tightened.map(r => r[0]);
    return { ...result, status: 'RECONSTRUCTED', reason: null, inference: inferredSides.length ? 'unique-residual-from-total' : null, ranges: tightened, counts: { bid, ask, neutral } };
  }
  function contract(text, ticker) {
    const m = /^(\d+(?:\.\d+)?)\s*(call|put)\s*(\d{4}-\d{2}-\d{2})$/i.exec(normalize(text));
    ticker = normalize(ticker).toUpperCase();
    if (!m || !/^[A-Z][A-Z0-9.]{0,9}$/.test(ticker) || !validDate(m[3])) throw Error('无法识别合约身份');
    return { ticker, strike: String(Number(m[1])), right: m[2].toUpperCase(), expiry: m[3] };
  }
  const formatters = new Map();
  function localParts(ms, zone) {
    if (!formatters.has(zone)) {
      if (formatters.size >= 8) formatters.clear();
      formatters.set(zone, new Intl.DateTimeFormat('en-CA', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }));
    }
    const parts = formatters.get(zone).formatToParts(new Date(ms));
    return Object.fromEntries(parts.filter(p => p.type !== 'literal').map(p => [p.type, p.value]));
  }
  function toUtc(date, time, zone) {
    if (!validDate(date) || !/^([01]\d|2[0-3]):[0-5]\d$/.test(time)) throw Error('无效日期或时间');
    const nominal = Date.parse(`${date}T${time}:00Z`), matches = new Set();
    for (const hours of [-24, -12, 0, 12, 24]) {
      const probe = nominal + hours * 3600000, p = localParts(probe, zone);
      const offset = Date.parse(`${p.year}-${p.month}-${p.day}T${p.hour}:${p.minute}:${p.second}Z`) - probe;
      const candidate = nominal - offset, q = localParts(candidate, zone);
      if (`${q.year}-${q.month}-${q.day}` === date && `${q.hour}:${q.minute}` === time) matches.add(candidate);
    }
    if (matches.size !== 1) throw Error('该本地时间不存在或存在夏令时歧义，请更换采集时段');
    return new Date([...matches][0]).toISOString();
  }
  function bucket(date, interval, zone) {
    const m = /^(\d{1,2}:\d{2})\s*[-–—]\s*(\d{1,2}:\d{2})$/.exec(normalize(interval));
    if (!m) throw Error('无法识别时间桶');
    const start = m[1].padStart(5, '0'), end = m[2].padStart(5, '0');
    if (end <= start) throw Error('第一版不猜测跨午夜桶的所属日期');
    const startUtc = toUtc(date, start, zone), endUtc = toUtc(date, end, zone);
    const minutes = (Date.parse(endUtc) - Date.parse(startUtc)) / 60000;
    if (minutes <= 0 || minutes > 120) throw Error('时间桶跨度异常');
    return { date, start, end, timezone: zone, startUtc, endUtc, minutes };
  }
  function dateLabelMatches(label, date) {
    if (!validDate(date)) return false;
    const expected = new Intl.DateTimeFormat('en-US', { timeZone: 'UTC', weekday: 'short', month: 'short', day: 'numeric' }).format(new Date(date));
    return normalize(label).replace(/,/g, '') === normalize(expected).replace(/,/g, '');
  }
  function parseRow(raw, config, bucketCache) {
    const identity = contract(raw.contract, raw.ticker);
    const cacheKey = JSON.stringify([config.date, config.timezone, raw.interval]);
    const window = bucketCache?.get(cacheKey) || bucket(config.date, raw.interval, config.timezone);
    bucketCache?.set(cacheKey, window);
    const volume = integer(raw.volume);
    const ba = reconstruct(volume, [raw.bidPercent, raw.neutralPercent, raw.askPercent]);
    const key = JSON.stringify([identity.ticker, identity.expiry, identity.right, identity.strike, window.startUtc, window.endUtc]);
    return { key, identity, bucket: window, volume, ba, raw };
  }
  const api = { normalize, validDate, integer, countRange, reconstruct, contract, toUtc, bucket, dateLabelMatches, parseRow };
  root.UwCore = api;
  if (typeof module !== 'undefined') module.exports = api;
})(globalThis);

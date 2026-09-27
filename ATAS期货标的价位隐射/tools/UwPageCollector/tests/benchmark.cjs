// Reproducible microbenchmark only. Not a browser/market-data completeness benchmark.
const { performance } = require('node:perf_hooks');
const os = require('node:os');
const Core = require('../extension/core.js');
const rows = Array.from({ length: 150 }, (_, i) => ({ interval: '14:30 - 14:35', ticker: 'QQQ', contract: `${600 + i} put 2026-09-21`, volume: '13,538', bidPercent: '47.2817%', neutralPercent: '0.568769%', askPercent: '52.1495%' }));
const config = { date: '2026-09-21', timezone: 'America/New_York' };
const durations = [];
for (let i = 0; i < 500; i++) {
  const start = performance.now(), cache = new Map();
  for (const row of rows) Core.parseRow(row, config, cache);
  durations.push(performance.now() - start);
}
durations.sort((a,b) => a-b);
console.log(JSON.stringify({ scope: 'parse-only / 500 snapshots x 150 rows; excludes DOM, IndexedDB and real market', node: process.version, cpu: os.cpus()[0].model, averageMs: durations.reduce((a,b) => a+b, 0) / durations.length, p95Ms: durations[Math.floor(durations.length * .95)], rssMiB: process.memoryUsage().rss / 1048576 }, null, 2));

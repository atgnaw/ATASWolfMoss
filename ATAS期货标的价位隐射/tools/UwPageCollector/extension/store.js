/* IndexedDB transactions persist across service-worker suspension. */
(function (root) {
  'use strict';
  const request = r => new Promise((resolve, reject) => { r.onsuccess = () => resolve(r.result); r.onerror = () => reject(r.error); });
  let database;
  async function open() {
    if (!database) database = new Promise((resolve, reject) => {
      const r = indexedDB.open('uw-page-collector-v1', 1);
      r.onupgradeneeded = () => {
        const db = r.result;
        db.createObjectStore('runs', { keyPath: 'id' });
        db.createObjectStore('latest', { keyPath: ['runId', 'key'] }).createIndex('runId', 'runId');
        db.createObjectStore('revisions', { keyPath: ['runId', 'sequence'] }).createIndex('runId', 'runId');
      };
      r.onsuccess = () => resolve(r.result); r.onerror = () => reject(r.error);
    });
    return database;
  }
  async function transaction(names, mode, work) {
    const db = await open(), tx = db.transaction(names, mode);
    const done = new Promise((resolve, reject) => { tx.oncomplete = resolve; tx.onerror = () => reject(tx.error); tx.onabort = () => reject(tx.error || Error('数据库事务取消')); });
    // Attach immediately: aborted transactions must not cause unhandled rejections.
    done.catch(() => {});
    try { const result = await work(tx); await done; return result; }
    catch (e) { try { tx.abort(); } catch {} await done.catch(() => {}); throw e; }
  }
  const get = id => transaction(['runs'], 'readonly', tx => request(tx.objectStore('runs').get(id)));
  const list = () => transaction(['runs'], 'readonly', tx => request(tx.objectStore('runs').getAll()));
  const put = run => transaction(['runs'], 'readwrite', tx => request(tx.objectStore('runs').put(run)));
  async function append(id, page, at) {
    return transaction(['runs', 'latest', 'revisions'], 'readwrite', async tx => {
      const runs = tx.objectStore('runs'), latest = tx.objectStore('latest'), revisions = tx.objectStore('revisions');
      const run = await request(runs.get(id));
      if (!run || run.status !== 'RUNNING') throw Error('采集已停止');
      if (run.url !== page.url || run.dateLabel !== page.dateLabel || JSON.stringify(run.headers) !== JSON.stringify(page.headers)) throw Error('页面日期、筛选 URL 或列设置已变化，请重新开始一轮');
      if (!root.UwCore.dateLabelMatches(page.dateLabel, run.config.date)) throw Error('页面日期与确认日期不一致');
      const parsed = [], errors = [...page.warnings], keys = new Set(), bucketCache = new Map();
      for (const raw of page.rows) {
        try {
          const row = root.UwCore.parseRow(raw, run.config, bucketCache);
          if (keys.has(row.key)) throw Error('同页存在重复合约时间桶');
          keys.add(row.key); parsed.push(row);
        } catch (e) { errors.push(`${raw.ticker} ${raw.contract}: ${e.message}`); }
      }
      // Reject the whole snapshot instead of silently accepting possibly shifted columns.
      if (errors.length) throw Error(errors.slice(0, 3).join('；'));
      const writes = [];
      let bytes = 0, verified = 0;
      for (const row of parsed) {
        if (row.ba.status === 'RECONSTRUCTED') verified++;
        const old = await request(latest.get([id, row.key]));
        const signature = JSON.stringify(row.raw);
        const timeState = Date.parse(at) >= Date.parse(row.bucket.endUtc) ? 'ENDED_NOT_FINAL' : 'FORMING';
        if (old?.signature === signature && old.timeState === timeState) continue;
        const record = { ...row, runId: id, signature, capturedUtc: at, pageReadUtc: page.readUtc || null, firstSeenUtc: old?.firstSeenUtc || at,
          revision: (old?.revision || 0) + 1, timeState, volumeDecreased: old?.volume != null && row.volume != null && row.volume < old.volume };
        bytes += new TextEncoder().encode(JSON.stringify(record)).length;
        writes.push(record);
      }
      if (run.bytes + bytes > 64 * 1024 * 1024) throw Error('本轮达到 64 MiB 上限，请导出后新建采集');
      for (const record of writes) {
        run.sequence++;
        revisions.put({ ...record, sequence: run.sequence }); latest.put(record);
        if (record.revision === 1) run.uniqueRows++;
      }
      const previous = run.lastPollUtc ? Date.parse(run.lastPollUtc) : Date.parse(run.startedUtc);
      const gap = Date.parse(at) - previous;
      if (gap > Math.max(15000, run.config.pollSeconds * 3000)) { run.gaps++; run.maxGapMs = Math.max(run.maxGapMs, gap); }
      run.bytes += bytes; run.polls++; run.lastPollUtc = at; run.lastRows = parsed.length;
      run.lastVerified = verified; run.lastUnverifiedReasons = root.UwQuality.summarize(parsed);
      run.lastInferredRows = parsed.filter(r => r.ba.status === 'RECONSTRUCTED' && r.ba.inferredSides?.length).length;
      run.emptyPolls += parsed.length === 0 ? 1 : 0;
      if (writes.length) run.lastChangeUtc = at;
      run.pageHidden = page.hidden; run.lastMessage = parsed.length ? `本页 ${parsed.length} 行；${verified} 行可唯一还原` : '空表：可能无结果或登录失效，不视为零成交';
      runs.put(run);
      return run;
    });
  }
  async function exported(id) {
    return transaction(['runs', 'latest', 'revisions'], 'readonly', async tx => {
      const runPromise = request(tx.objectStore('runs').get(id));
      const latestPromise = request(tx.objectStore('latest').index('runId').getAll(id));
      const revisionsPromise = request(tx.objectStore('revisions').index('runId').getAll(id));
      const [run, latest, revisions] = await Promise.all([runPromise, latestPromise, revisionsPromise]);
      return { schema: 'uw-page-collector/1', exportedUtc: new Date().toISOString(), run, latest, revisions,
        limitations: ['Only currently rendered table rows; missing rows are not zero.', 'Counts reconstructed from CSS percentages, not an official API or direct tooltip.', 'Ended buckets can be revised; not guaranteed final.', 'Premium text is rounded; B/A counts are volume weighted.', 'Date and timezone are user-confirmed; browser polling gaps may occur.'] };
    });
  }
  async function remove(id) {
    return transaction(['runs', 'latest', 'revisions'], 'readwrite', async tx => {
      const run = await request(tx.objectStore('runs').get(id));
      if (run?.status === 'RUNNING') throw Error('请先停止采集');
      for (const name of ['latest', 'revisions']) {
        const store = tx.objectStore(name), keys = await request(store.index('runId').getAllKeys(id));
        keys.forEach(key => store.delete(key));
      }
      tx.objectStore('runs').delete(id);
    });
  }
  root.UwStore = { get, list, put, append, exported, remove };
})(globalThis);

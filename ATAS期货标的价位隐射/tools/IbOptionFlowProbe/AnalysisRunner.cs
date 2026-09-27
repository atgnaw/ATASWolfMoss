using System.Text.Json;

namespace IbOptionFlowProbe;

internal static class AnalysisRunner
{
    public static async Task<(string Directory, AnalysisReport Report)> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        var manifest = await ReadAsync<ProbeManifest>(Path.Combine(directory, "manifest.json"))
            ?? throw new ArgumentException("缺少运行清单，不能确认时钟频率和合约身份。");
        if (manifest.SchemaVersion != 1 || manifest.MonotonicFrequency <= 0) throw new ArgumentException("不支持的原始格式或无效时钟频率。");
        var contracts = await ReadAsync<ProbeContract[]>(Path.Combine(directory, "contracts.json")) ?? manifest.Contracts.ToArray();
        // Two supported underlyings, ATM plus up to 20 strikes on each side, Call and Put.
        // Preserve identity validation without imposing the former three-strike capture limit.
        if (contracts.Length > 164 || contracts.Any(x => x.Contract.ConId <= 0)
            || contracts.Select(x => x.Contract.ConId).Distinct().Count() != contracts.Length)
            throw new ArgumentException("合约清单无效。");
        var known = contracts.ToDictionary(x => x.Contract.ConId, x => x.Contract);
        var summary = await ReadAsync<CaptureSummary>(Path.Combine(directory, "capture-summary.json"));
        var integrity = await CaptureInspector.InspectAsync(directory);
        var end = summary?.EndedUtc ?? manifest.CreatedUtc;
        // If interrupted, recover an observed end from the raw log in the same streaming pass;
        // all bucket completions remain conservatively partial without a final summary.
        var engine = new ProbeAnalyzer(manifest.MonotonicFrequency, summary?.CaptureStartedUtc ?? manifest.CreatedUtc, end);
        var direction = new DirectionAnalyzer(manifest.MonotonicFrequency, summary?.CaptureStartedUtc ?? manifest.CreatedUtc, end);
        var outside = new DirectionAnalyzer(manifest.MonotonicFrequency, summary?.CaptureStartedUtc ?? manifest.CreatedUtc, end, true);
        void BreakDirection(string reason, DateTime? at = null) { direction.Break(reason, at); outside.Break(reason, at); }
        var output = Path.Combine(directory, "analysis-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        await using var trace = new TraceWriter(output);
        long lastSequence = 0, lastMonotonic = 0;
        foreach (var file in Directory.GetFiles(directory, "events-*.jsonl").Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(file);
            while (await reader.ReadLineAsync() is { } line)
            {
                ProbeEvent? item;
                try { item = JsonSerializer.Deserialize<ProbeEvent>(line, ProbeJson.Options); }
                catch (JsonException) { engine.Break("CORRUPT_INPUT"); BreakDirection("CORRUPT_INPUT"); continue; }
                if (item == null || item.SchemaVersion != 1 || item.RunId != manifest.RunId || item.ConnectionEpoch <= 0
                    || item.Raw == null || item.Raw.Sequence <= lastSequence)
                { engine.Break("INVALID_IDENTITY_OR_SEQUENCE"); BreakDirection("INVALID_IDENTITY_OR_SEQUENCE"); continue; }
                if (lastSequence != 0 && item.Raw.Sequence != lastSequence + 1) { engine.Break("MISSING_RECORDS"); BreakDirection("MISSING_RECORDS", item.Raw.ReceivedUtc); }
                if (lastMonotonic != 0 && item.Raw.MonotonicTicks < lastMonotonic) { engine.Break("ARRIVAL_CLOCK_REGRESSION"); BreakDirection("ARRIVAL_CLOCK_REGRESSION", item.Raw.ReceivedUtc); }
                lastSequence = item.Raw.Sequence; lastMonotonic = item.Raw.MonotonicTicks;
                if (item.Raw.Contract is { } c && (!known.TryGetValue(c.ConId, out var expected) || c != expected))
                { engine.Break("UNVERIFIED_CONTRACT"); BreakDirection("UNVERIFIED_CONTRACT", item.Raw.ReceivedUtc); continue; }
                // Derive parsed RT fields from original payload, not a possibly edited derived field.
                var reparsed = RawEventParser.Convert(item.RunId, item.Raw) with { ConnectionEpoch = item.ConnectionEpoch };
                var decision = engine.Process(reparsed);
                var evidence = direction.Process(reparsed, decision, summary?.Complete == true && item.Raw.ReceivedUtc >= end.AddSeconds(-1));
                var outsideEvidence = outside.Process(reparsed, decision, summary?.Complete == true && item.Raw.ReceivedUtc >= end.AddSeconds(-1));
                if (decision != null) await trace.WriteAsync(decision with { DirectionEvidence = evidence, OutsideQuoteEvidence = outsideEvidence });
            }
        }
        engine.AddMissingRows(known.Values);
        var totals = engine.Rows(0);
        var warnings = new List<string>
        {
            "仅验证接收顺序下的报价匹配，不是交易所成交时刻 BBO，也不是真实主动买卖方向。",
            "233 与 375 分开统计，不能相加。报价年龄阈值是实验参数，不是官方有效期。",
            "桶内对账只包含起止采样均位于桶内的比较对；跨桶增量不分摊。缺失成交明细不按最后一笔方向补齐。",
            "少于 30 个可识别成交事件标记证据不足，这是本工具的样本提示，不是行业准确率标准。"
        };
        if (integrity.AbnormalEnd) warnings.Add("本地采集不完整、异常结束或文件缺失；不可据此作全量方向判断。");
        if (engine.Diagnostics.TryGetValue("IB_10090", out var permissionWarnings))
            warnings.Add($"IB 10090 部分行情字段权限警告 {permissionWarnings} 次：其他字段可能仍可用；请分别检查报价、233 与 375 的覆盖。不能仅凭此错误码确定缺失字段，也不能将本地采集完整等同于上游数据完整。");
        if (totals.Any(x => x.FutureSourceEvents > 0)) warnings.Add("存在源时间晚于本机接收时间：原始时间未修正，详见每笔 source_lag_ms 和各行提前量。");
        if (totals.Any(x => x.DelayedEvents > 0)) warnings.Add("存在延迟/冻结数据；对应事件不提供实时方向分类。");
        if (totals.Any(x => x.UnexplainedVolume != 0 && x.UnexplainedVolume != null)) warnings.Add("成交明细与累计成交量不能完全核对；差额不代表可直接推断方向的成交。");
        if (totals.Any(x => x.SensitivityChanges > 0)) warnings.Add("部分分类随报价年龄阈值变化，请同时查看可分类覆盖率。");
        if (manifest.Synthetic) warnings.Insert(0, "这是模拟数据演示，不是 IB 实盘验证报告。");
        var unsuitable = integrity.AbnormalEnd || totals.Any(x => x.OverReconciled || x.DelayedEvents > 0 || x.UnexplainedVolume > 0)
            || engine.Diagnostics.Keys.Any(x => x is "CORRUPT_INPUT" or "INVALID_IDENTITY_OR_SEQUENCE" or "MISSING_RECORDS" or "UNVERIFIED_CONTRACT");
        if (totals.All(x => x.Scenarios.All(s => s.ClassifiedVolumeFraction is null or 0)))
            warnings.Add("没有足够可分类的实时成交；不能据此推断桶内方向。");
        var conclusion = unsuitable ? "UNSUITABLE_FOR_FULL_FLOW_INFERENCE" : totals.All(x => x.Events < 30
            || x.Scenarios.All(s => s.ClassifiedVolumeFraction is null or 0))
            ? "INSUFFICIENT_SAMPLE" : "RECEIVE_ORDER_ESTIMATE_ONLY";
        warnings.Add("新增 2000/5000 ms 与无年龄上限对照（maximum_age_ms=-1）。无上限不表示报价始终有效，也不证明方向准确；原四组分类规则保持不变，阈值变化事件现按七组计算。");
        warnings.Add("总体稳健度仅是条件性压力测试，不是方向正确概率；源时间偏移为预设实验假设，不是测得的网络延迟。报价一致可能来自同一状态，不算独立证据。");
        var report = new AnalysisReport(3, "0.6.0", manifest.RunId, manifest.Synthetic, integrity, conclusion,
            warnings.ToArray(), engine.RawEvents, engine.TradeCallbacks, engine.AcceptedTrades, engine.Boundaries,
            engine.Diagnostics, totals, engine.Rows().Where(x => x.IntervalMinutes > 0).ToArray())
            { Robustness = direction.Finish(engine.Rows(), integrity), OutsideQuoteRobustness = outside.Finish(engine.Rows(), integrity) };
        await trace.DisposeAsync();
        await CaptureRunner.SaveNewAsync(Path.Combine(output, "report.json"), report);
        await File.WriteAllTextAsync(Path.Combine(output, "report.html"), ProbeHtml.Render(report));
        Console.WriteLine($"离线报告：{Path.Combine(output, "report.html")}");
        return (output, report);
    }
    internal static async Task<T?> ReadAsync<T>(string path)
        => File.Exists(path) ? JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path), ProbeJson.Options) : default;

    private sealed class TraceWriter(string directory) : IAsyncDisposable
    {
        private FileStream? _file;
        private int _part;
        private long _bytes, _partBytes;
        private static readonly byte[] Newline = [(byte)'\n'];
        public async Task WriteAsync(TradeDecision decision)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(decision, ProbeJson.Options);
            if (_bytes + bytes.Length + 1 > 2L * 1024 * 1024 * 1024) throw new IOException("Analysis trace size limit reached.");
            if (_file == null || _partBytes + bytes.Length + 1 > 128L * 1024 * 1024)
            {
                if (_file != null) await _file.DisposeAsync();
                _file = new FileStream(Path.Combine(directory, $"decisions-{++_part:00000}.jsonl"), FileMode.CreateNew,
                    FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
                _partBytes = 0;
            }
            await _file.WriteAsync(bytes); await _file.WriteAsync(Newline);
            _partBytes += bytes.Length + 1; _bytes += bytes.Length + 1;
        }
        public async ValueTask DisposeAsync() { if (_file != null) { await _file.DisposeAsync(); _file = null; } }
    }
}

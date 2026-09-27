using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe;

// Parallel offline pipeline: legacy reconciliation and the seven original scenarios stay unchanged.
internal sealed class DirectionAnalyzer(long frequency, DateTime start, DateTime end, bool classifyOutside = false)
{
    private sealed class ContractState(long frequency)
    {
        public QuoteHistory History { get; } = new(frequency);
        public int Request;
        public string Segment = "";
    }
    private sealed class TickState
    {
        public decimal? Price;
        public DateTime Previous, LastChange;
        public int Sign;
        public string UnknownReason = "TICK_NO_PREVIOUS";
    }
    private readonly DirectionSettings _settings = new() { ClassifyOutsideQuote = classifyOutside };
    private readonly Dictionary<long, ContractState> _states = new();
    private readonly Dictionary<(long, string), TickState> _ticks = new();
    private readonly HashSet<(long, string)> _interruptedTicks = new();
    private readonly Dictionary<long, OptionTradingSegment[]> _segments = new();
    private readonly Dictionary<(long, string, string, int, DateTime), DirectionBucket> _rows = new();
    private readonly List<(DateTime Time, string Reason)> _gaps = new();
    private readonly SortedDictionary<string, long> _diagnostics = new(StringComparer.Ordinal);
    private IbRawMarketEvent? _lastRaw;
    private int _epoch, _peak;
    private long _evictions;
    private void Count(string reason) => _diagnostics[reason] = _diagnostics.GetValueOrDefault(reason) + 1;
    public void Break(string reason, DateTime? time = null)
    {
        foreach (var state in _states.Values) Remember(state);
        foreach (var key in _ticks.Keys) _interruptedTicks.Add(key);
        _states.Clear(); _ticks.Clear();
        _gaps.Add((time ?? _lastRaw?.ReceivedUtc ?? start, reason));
        Count(reason);
    }
    private void Remember(ContractState state)
    { _peak = Math.Max(_peak, state.History.Peak); _evictions += state.History.Evictions; }
    private OptionTradingSegment Segment(OptionContractDescriptor c, DateTime utc)
    {
        if (!_segments.TryGetValue(c.ConId, out var segments))
            _segments[c.ConId] = segments = IbTradingHoursParser.Parse(c.TradingHours, c.TradingTimeZoneId, c.Ticker == "SPX").ToArray();
        return segments.FirstOrDefault(x => x.Contains(utc));
    }
    private static string Session(DateTime utc) => NyseTradingCalendar.IsRegularTradingHours(utc) ? "RTH" : "GTH";
    private void ResetTicks(long conId)
    {
        foreach (var scope in new[] { "RegularTrades", "AllTimeAndSales" })
            if (_ticks.Remove((conId, scope))) _interruptedTicks.Add((conId, scope));
    }

    public DirectionEvidence? Process(ProbeEvent item, TradeDecision? legacy, bool cleanup = false)
    {
        var raw = item.Raw;
        if (_lastRaw != null)
        {
            var elapsed = (raw.MonotonicTicks - (double)_lastRaw.MonotonicTicks) * 1000 / frequency;
            if (elapsed < 0 || Math.Abs((raw.ReceivedUtc - _lastRaw.ReceivedUtc).TotalMilliseconds - elapsed) > _settings.ClockJumpThresholdMs)
                Break("CLOCK_JUMP", raw.ReceivedUtc);
        }
        _lastRaw = raw;
        if (_epoch != 0 && _epoch != item.ConnectionEpoch) Break("CONNECTION_EPOCH", raw.ReceivedUtc);
        _epoch = item.ConnectionEpoch;
        if (raw.Kind == "connectionClosed" || raw.ErrorCode is 1100 or 1101 or 1300 or 2103)
        {
            if (cleanup && raw.Kind == "connectionClosed") Count("CLEANUP_DISCONNECT");
            else Break("OBSERVATION_GAP", raw.ReceivedUtc);
            return null;
        }
        if (raw.Contract is not { } c || c.ConId <= 0) return null;
        if (raw.Kind is not ("tickPrice" or "tickSize" or "tickString" or "marketDataType")) return null;
        if (!_states.TryGetValue(c.ConId, out var state)) _states[c.ConId] = state = new(frequency);
        var currentSegment = Segment(c, raw.ReceivedUtc);
        var segmentKey = $"{currentSegment.StartUtc:O}/{currentSegment.EndUtc:O}/{Session(raw.ReceivedUtc)}";
        if (state.Request != raw.RequestId || state.Segment != segmentKey
            || (raw.Kind == "marketDataType" && state.History.Latest?.DataType != raw.MarketDataType))
        {
            Remember(state);
            state = new(frequency) { Request = raw.RequestId, Segment = segmentKey }; _states[c.ConId] = state;
            ResetTicks(c.ConId);
        }
        if (raw.Kind is "tickPrice" or "tickSize" or "marketDataType") { state.History.Add(raw); return null; }
        if (legacy == null) return null;
        var trade = item.Trade;
        var accepted = legacy.Disposition == "ACCEPTED";
        var tickKey = (c.ConId, legacy.Scope);
        if (!_ticks.TryGetValue(tickKey, out var tick)) _ticks[tickKey] = tick = new()
            { UnknownReason = _interruptedTicks.Remove(tickKey) ? "TICK_INTERRUPTED" : "TICK_NO_PREVIOUS" };
        var tickSign = 0;
        if (trade != null && legacy.Disposition is "ACCEPTED" or "BASELINE")
        {
            var continuous = accepted && tick.Price.HasValue && trade.SourceUtc >= tick.Previous
                && (trade.SourceUtc - tick.Previous).TotalMilliseconds <= _settings.TickMaximumAgeMs;
            if (!continuous)
            {
                if (accepted && tick.Price.HasValue) tick.UnknownReason = "TICK_PREVIOUS_TOO_OLD";
                else if (!accepted && tick.Price.HasValue) tick.UnknownReason = "TICK_INTERRUPTED";
                tick.Sign = 0; tick.LastChange = default;
            }
            else if (trade.Price != tick.Price)
            { tick.Sign = trade.Price > tick.Price ? 1 : -1; tick.LastChange = trade.SourceUtc; }
            else if (tick.Sign == 0 && tick.UnknownReason != "TICK_INTERRUPTED") tick.UnknownReason = "TICK_NO_CHANGE";
            if (continuous && (trade.SourceUtc - tick.LastChange).TotalMilliseconds <= _settings.TickMaximumAgeMs)
                tickSign = tick.Sign;
            else if (continuous && tick.Sign != 0) tick.UnknownReason = "TICK_DIRECTION_TOO_OLD";
            tick.Price = trade.Price; tick.Previous = trade.SourceUtc;
        }
        else if (legacy.Disposition != "DUPLICATE") { _ticks.Remove(tickKey); _interruptedTicks.Add(tickKey); }
        if (trade == null || !accepted)
            return new(raw.Sequence, legacy.Disposition, 0, "EXCLUDED_EVENT", 0, 0, 0, [], []);
        var sourceSegment = Segment(c, trade.SourceUtc);
        var alignments = new List<AlignmentEvidence>();
        alignments.Add(Evidence("Latest", raw.ReceivedUtc, raw.MonotonicTicks, state.History.Latest, null));
        foreach (var offset in _settings.SourceOffsetsMs)
        {
            var targetUtc = trade.SourceUtc.AddMilliseconds(offset);
            var target = raw.MonotonicTicks + (targetUtc - raw.ReceivedUtc).TotalSeconds * frequency;
            var lookup = state.History.At(target, raw.MonotonicTicks);
            var error = lookup.Error;
            if (error == null && (sourceSegment == default || !sourceSegment.Contains(targetUtc) || Session(targetUtc) != Session(trade.SourceUtc)))
                error = "OUTSIDE_TRADING_SEGMENT";
            alignments.Add(Evidence($"Source{offset:+0;-0;0}", targetUtc, target, lookup.State, error));
        }
        if (sourceSegment != currentSegment || Session(trade.SourceUtc) != Session(raw.ReceivedUtc))
            alignments = alignments.Select(a => a with { Error = a.Error ?? "TRADING_SEGMENT_MISMATCH" }).ToList();
        var matches = _settings.Scenarios.Select(s =>
        {
            var id = s.OffsetMs.HasValue ? $"Source{s.OffsetMs.Value:+0;-0;0}" : "Latest";
            var evidence = alignments.Single(a => a.Id == id);
            var result = DirectionClassifier.Classify(evidence, trade.Price, s.Method, s.MaximumAgeMs, tickSign, tick.UnknownReason, classifyOutside);
            return new DirectionMatch(s.Id, id, result.Sign, result.Reason);
        }).ToArray();
        var feasible = alignments.Skip(1).Where(a => a.Feasible).ToArray();
        var methods = new[] { "AtQuote", "Midpoint", "MidpointTick" };
        var consensus = methods.Select(method =>
        {
            var relevant = matches.Where(m => m.ScenarioId == $"Latest/{method}/-1"
                || feasible.Any(a => m.ScenarioId == a.Id + "/" + method)).ToArray();
            var conflict = relevant.Any(x => x.Sign > 0) && relevant.Any(x => x.Sign < 0);
            var why = feasible.Length < 2 ? "INSUFFICIENT_TIME_CANDIDATES"
                : conflict ? "DIRECTION_CONFLICT"
                : relevant.Any(x => x.Sign == 0 && !DirectionClassifier.IsMid(x.Reason)) ? "UNRESOLVED_CANDIDATE"
                : relevant.All(x => DirectionClassifier.IsMid(x.Reason)) ? "MID_TIME_AGREE"
                : relevant.Any(x => DirectionClassifier.IsMid(x.Reason)) ? "MID_DIRECTION_DISAGREEMENT" : "TIME_SCENARIOS_AGREE";
            return new DirectionMatch(method, "Consensus", why == "TIME_SCENARIOS_AGREE" ? relevant[0].Sign : 0, why);
        }).ToArray();
        var primary = consensus.Single(x => x.ScenarioId == "Midpoint");
        var sign = primary.Sign; var reason = primary.Reason;
        var resultEvidence = new DirectionEvidence(raw.Sequence, legacy.Disposition, sign, reason, feasible.Length,
            alignments.Where(a => a.Error == null && a.Quote != null).Select(a => (a.Quote!.Bid?.Sequence, a.Quote.Ask?.Sequence)).Distinct().Count(),
            tickSign, alignments.ToArray(), matches) { Consensus = consensus };
        Count(reason);
        if (sourceSegment != default)
        {
            var premium = trade.Price * trade.Size * c.Multiplier;
            foreach (var minutes in new[] { 0, 1, 3, 5, 10 })
            {
                var bucketStart = minutes == 0 ? DateTime.MinValue : sourceSegment.StartUtc.AddTicks(
                    (trade.SourceUtc - sourceSegment.StartUtc).Ticks / TimeSpan.FromMinutes(minutes).Ticks * TimeSpan.FromMinutes(minutes).Ticks);
                var row = Row(c, legacy.Scope, Session(trade.SourceUtc), minutes, bucketStart);
                row.Events++; row.Volume += trade.Size; row.TrackLargest(premium); row.Primary.Add(sign, premium, reason);
                foreach (var match in consensus)
                {
                    if (!row.Consensus.TryGetValue(match.ScenarioId, out var amounts)) row.Consensus[match.ScenarioId] = amounts = new();
                    amounts.Add(match.Sign, premium, match.Reason);
                    var relevant = matches.Where(m => m.ScenarioId == $"Latest/{match.ScenarioId}/-1"
                        || feasible.Any(a => m.ScenarioId == a.Id + "/" + match.ScenarioId)).ToArray();
                    if (match.Reason == "UNRESOLVED_CANDIDATE")
                    {
                        var causes = relevant.Where(m => m.Sign == 0 && !DirectionClassifier.IsMid(m.Reason)).Select(m => m.Reason).ToArray();
                        UnknownTradeExample Example() => new(raw.Sequence, trade.SourceUtc, raw.ReceivedUtc,
                            trade.Price, premium, relevant.Select(m =>
                            {
                                var a = alignments.Single(a => a.Id == m.AlignmentId);
                                return new UnknownCandidate(a.Id, a.TargetUtc, a.Quote?.Bid?.Price, a.Quote?.Ask?.Price,
                                    a.Quote?.Bid?.Sequence, a.Quote?.Ask?.Sequence, m.Sign, m.Reason);
                            }).ToArray());
                        amounts.AddUnresolved(premium, causes, Example);
                        if (match.ScenarioId == "Midpoint") row.Primary.AddUnresolved(premium, causes, Example);
                    }
                    // Independent diagnostic: count conflicts even if another candidate is unknown,
                    // or too few source candidates exist to admit the conservative classification.
                    if (relevant.Any(m => m.Sign > 0) && relevant.Any(m => m.Sign < 0))
                        row.TimeConflictPremiums[match.ScenarioId] = row.TimeConflictPremiums.GetValueOrDefault(match.ScenarioId) + premium;
                }
                foreach (var match in matches)
                {
                    if (!row.Scenarios.TryGetValue(match.ScenarioId, out var amounts)) row.Scenarios[match.ScenarioId] = amounts = new();
                    amounts.Add(match.Sign, premium, match.Reason);
                }
                if (state.History.Latest?.DataType != 1) row.Quality.Add("NON_REALTIME");
            }
        }
        return resultEvidence;
    }
    private AlignmentEvidence Evidence(string id, DateTime targetUtc, double ticks, QuoteState? q, string? error)
        => new(id, targetUtc, ticks, error != "FUTURE_CANDIDATE", q, error,
            q?.Bid == null ? null : (ticks - q.Bid.Ticks) * 1000 / frequency,
            q?.Ask == null ? null : (ticks - q.Ask.Ticks) * 1000 / frequency);
    private DirectionBucket Row(OptionContractDescriptor c, string scope, string session, int minutes, DateTime at)
    {
        var key = (c.ConId, scope, session, minutes, at);
        if (_rows.TryGetValue(key, out var row)) return row;
        if (_rows.Count >= 30000) throw new ArgumentException("稳健度桶数量超过保护上限。");
        return _rows[key] = new() { Contract = c, Scope = scope, Session = session, IntervalMinutes = minutes,
            StartUtc = minutes == 0 ? null : at, EndUtc = minutes == 0 ? null : at.AddMinutes(minutes) };
    }
    public DirectionReport Finish(AnalysisRow[] legacyRows, Inspection integrity)
    {
        foreach (var old in legacyRows)
        {
            var row = Row(old.Contract, old.Scope, old.Session, old.IntervalMinutes, old.BucketStartUtc ?? DateTime.MinValue);
            if (integrity.AbnormalEnd) row.Quality.Add("LOCAL_INCOMPLETE");
            // A cross-bucket cumulative comparison is not a gap in observed trade callbacks.
            // Keep it as a reconciliation limitation without disqualifying every full minute.
            var segment = Segment(old.Contract, old.BucketStartUtc ?? start);
            if (old.IntervalMinutes > 0 && (old.BucketStartUtc < start || old.BucketEndUtc > end
                || segment == default || old.BucketEndUtc > segment.EndUtc
                || old.Reasons.Keys.Any(x => x is "BASELINE" or "COUNTER_RESET" or "PREMIUM_RESET" or "NUMERIC_OVERFLOW")))
                row.Quality.Add("PARTIAL_BUCKET");
            if (old.DelayedEvents > 0) row.Quality.Add("NON_REALTIME");
            if (old.BoundaryCrossings > 0) row.Quality.Add("RECONCILIATION_BOUNDARY");
            if (old.UnexplainedVolume != null && old.UnexplainedVolume != 0) row.Quality.Add("RECONCILIATION_DIFFERENCE");
            if (old.PremiumDifference != null && old.PremiumDifference != 0) row.Quality.Add("PREMIUM_DIFFERENCE");
            if (old.ComparisonPairs == 0) row.Quality.Add("NO_COMPARISON_BASELINE");
            if (row.Events < _settings.MinimumEvents) row.Quality.Add("INSUFFICIENT_SAMPLE");
            row.ReconciliationVolumeCoverage = old.VolumeCoverage;
            row.UnexplainedVolume = old.UnexplainedVolume; row.PremiumDifference = old.PremiumDifference;
            foreach (var gap in _gaps.Where(g => g.Time >= (row.StartUtc ?? start) && g.Time < (row.EndUtc ?? end)))
                row.Quality.Add(gap.Reason == "CLOCK_JUMP" ? "CLOCK_JUMP" : "OBSERVATION_GAP");
        }
        var rows = _rows.Values.OrderBy(x => x.Contract.Ticker, StringComparer.Ordinal).ThenBy(x => x.Contract.ConId)
            .ThenBy(x => x.Scope, StringComparer.Ordinal).ThenBy(x => x.Session, StringComparer.Ordinal)
            .ThenBy(x => x.IntervalMinutes).ThenBy(x => x.StartUtc).ToArray();
        return new(_settings, _diagnostics, rows.Where(x => x.IntervalMinutes == 0).ToArray(), rows.Where(x => x.IntervalMinutes > 0).ToArray(),
            Math.Max(_peak, _states.Count == 0 ? 0 : _states.Values.Max(x => x.History.Peak)),
            _evictions + _states.Values.Sum(x => x.History.Evictions));
    }
}

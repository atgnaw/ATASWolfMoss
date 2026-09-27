using System.Globalization;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe;

internal sealed class ProbeAnalyzer(long monotonicFrequency, DateTime observationStart, DateTime observationEnd)
{
    private sealed class Quotes
    {
        public QuoteReference? Bid, Ask;
        public decimal? BidSize, AskSize;
        public long? BidSizeSequence, AskSizeSequence;
        public int DataType;
        public int RequestId;
    }
    private sealed class Feed
    {
        public ProbeTrade? Last;
        public string? LastPayload;
        public DateTime SegmentStart;
        public string Session = "";
        public int RequestId, Epoch;
    }
    private readonly Dictionary<long, Quotes> _quotes = new();
    private readonly Dictionary<(long, int), Feed> _feeds = new();
    private readonly Dictionary<long, OptionTradingSegment[]> _segments = new();
    private readonly Dictionary<(long, int, string, int, DateTime), AnalysisRow> _rows = new();
    public SortedDictionary<string, long> Diagnostics { get; } = new(StringComparer.Ordinal);
    public long RawEvents { get; private set; }
    public long TradeCallbacks { get; private set; }
    public long AcceptedTrades { get; private set; }
    public long Boundaries { get; private set; }
    private int _epoch;
    public void Break(string reason)
    {
        _quotes.Clear(); _feeds.Clear(); Boundaries++; Count(reason);
    }
    private void Count(string name) => Diagnostics[name] = Diagnostics.GetValueOrDefault(name) + 1;
    public TradeDecision? Process(ProbeEvent item)
    {
        RawEvents++;
        var raw = item.Raw;
        if (_epoch != 0 && _epoch != item.ConnectionEpoch) Break("CONNECTION_EPOCH");
        _epoch = item.ConnectionEpoch;
        if (raw.Kind == "connectionClosed" || raw.ErrorCode is 1100 or 1101 or 1300 or 2103)
        { Break(raw.ErrorCode is { } e ? $"IB_{e}" : "DISCONNECT"); return null; }
        if (raw.ErrorCode.HasValue) Count($"IB_{raw.ErrorCode}");
        if (raw.Contract is not { } contract) return NoContract(item);
        if (contract.ConId <= 0 || contract.Multiplier <= 0) { Count("INVALID_CONTRACT"); return NoContract(item); }
        if (!_quotes.TryGetValue(contract.ConId, out var quotes)) _quotes[contract.ConId] = quotes = new();
        if (quotes.RequestId != 0 && quotes.RequestId != raw.RequestId)
        {
            quotes = new(); _quotes[contract.ConId] = quotes;
            _feeds.Remove((contract.ConId, 48)); _feeds.Remove((contract.ConId, 77)); Count("SUBSCRIPTION_CHANGED");
        }
        quotes.RequestId = raw.RequestId;
        if (raw.Kind == "marketDataType")
        {
            quotes.DataType = raw.MarketDataType ?? 0;
            quotes.Bid = quotes.Ask = null; quotes.BidSize = quotes.AskSize = null;
            _feeds.Remove((contract.ConId, 48)); _feeds.Remove((contract.ConId, 77));
            return null;
        }
        if (raw.Kind is "tickPrice" or "tickSize")
        {
            var valid = decimal.TryParse(raw.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value);
            if (raw.Field is 66 or 67 or 69 or 70) quotes.DataType = 3;
            if (raw.Kind == "tickPrice")
            {
                var quote = valid ? new QuoteReference(raw.Sequence, value, raw.MonotonicTicks, raw.ReceivedUtc,
                    value <= 0 || raw.PastLimit == true || raw.PreOpen == true) : null;
                if (raw.Field is 1 or 66) quotes.Bid = quote;
                if (raw.Field is 2 or 67) quotes.Ask = quote;
            }
            else
            {
                if (raw.Field is 0 or 69) { quotes.BidSize = valid ? value : null; quotes.BidSizeSequence = raw.Sequence; }
                if (raw.Field is 3 or 70) { quotes.AskSize = valid ? value : null; quotes.AskSizeSequence = raw.Sequence; }
            }
            return null;
        }
        if (raw.Kind != "tickString" || raw.Field is not (48 or 77)) return null;
        TradeCallbacks++;
        var scope = raw.Field == 77 ? "RegularTrades" : "AllTimeAndSales";
        var key = (contract.ConId, raw.Field.Value);
        if (!_feeds.TryGetValue(key, out var feed)) _feeds[key] = feed = new();
        var trade = item.Trade;
        string disposition;
        OptionTradingSegment segment = default;
        var session = "UNKNOWN";
        decimal deltaVolume = 0, deltaPremium = 0, premium = 0;
        var previous = feed.Last;
        var comparable = false;
        if (trade == null) { disposition = item.ParseError ?? "INVALID_RT_FIELDS"; feed.Last = null; }
        else
        {
            if (!_segments.TryGetValue(contract.ConId, out var segments))
                _segments[contract.ConId] = segments = IbTradingHoursParser.Parse(contract.TradingHours,
                    contract.TradingTimeZoneId, contract.Ticker == "SPX").ToArray();
            segment = segments.FirstOrDefault(x => x.Contains(trade.SourceUtc));
            var et = UsMarketClock.ToEastern(trade.SourceUtc);
            session = NyseTradingCalendar.IsRegularTradingHours(trade.SourceUtc)
                && et.TimeOfDay < NyseTradingCalendar.GetRegularClose(et.Date) ? "RTH" : "GTH";
            if (segment == default) { disposition = "OUTSIDE_SESSION"; feed.Last = null; }
            else if (previous == null || feed.Epoch != item.ConnectionEpoch || feed.RequestId != raw.RequestId
                || feed.SegmentStart != segment.StartUtc || feed.Session != session)
                disposition = "BASELINE";
            else if (feed.LastPayload == raw.Value) disposition = "DUPLICATE";
            else if (trade.SourceUtc < previous.SourceUtc) disposition = "OUT_OF_ORDER";
            else if (trade.TotalVolume < previous.TotalVolume) disposition = "COUNTER_RESET";
            else
            {
                try
                {
                    deltaVolume = trade.TotalVolume - previous.TotalVolume;
                    deltaPremium = trade.Vwap * trade.TotalVolume * contract.Multiplier
                        - previous.Vwap * previous.TotalVolume * contract.Multiplier;
                    premium = trade.Price * trade.Size * contract.Multiplier;
                    comparable = deltaPremium >= 0;
                    disposition = deltaPremium < 0 ? "PREMIUM_RESET" : deltaVolume == 0
                        ? "REVISION_OR_REPEAT" : trade.Size <= 0 ? "NO_PRINT_SIZE"
                        : trade.SourceUtc == previous.SourceUtc && trade.Price == previous.Price && trade.Size == previous.Size
                            ? "AMBIGUOUS_SAME_PRINT" : "ACCEPTED";
                }
                catch (OverflowException) { disposition = "NUMERIC_OVERFLOW"; comparable = false; }
            }
            if (disposition is not ("OUT_OF_ORDER" or "DUPLICATE" or "OUTSIDE_SESSION"))
            {
                feed.Last = trade; feed.LastPayload = raw.Value; feed.Epoch = item.ConnectionEpoch;
                feed.RequestId = raw.RequestId; feed.SegmentStart = segment.StartUtc; feed.Session = session;
            }
        }
        Count(disposition);
        double? bidAge = Age(quotes.Bid, raw.MonotonicTicks), askAge = Age(quotes.Ask, raw.MonotonicTicks);
        var matches = QuoteAgeThresholds.Values.Select(age => Match(quotes, bidAge, askAge,
            trade?.Price, age, disposition)).ToArray();
        if (disposition == "ACCEPTED") AcceptedTrades++;
        if (trade != null && segment != default)
        {
            foreach (var interval in new[] { 0, 1, 3, 5, 10 })
            {
                var start = interval == 0 ? DateTime.MinValue : segment.StartUtc.AddTicks(
                    (trade.SourceUtc - segment.StartUtc).Ticks / TimeSpan.FromMinutes(interval).Ticks * TimeSpan.FromMinutes(interval).Ticks);
                var rowKey = (contract.ConId, raw.Field.Value, session, interval, start);
                if (!_rows.TryGetValue(rowKey, out var row))
                {
                    var end = interval == 0 ? (DateTime?)null : start.AddMinutes(interval);
                    _rows[rowKey] = row = new AnalysisRow { Contract = contract, Scope = scope, Session = session,
                        IntervalMinutes = interval, BucketStartUtc = interval == 0 ? null : start, BucketEndUtc = end,
                        Partial = interval > 0 && (start < observationStart || end > observationEnd || end > segment.EndUtc) };
                }
                row.Count(disposition);
                row.ObservedFromUtc = row.ObservedFromUtc is null || trade.SourceUtc < row.ObservedFromUtc ? trade.SourceUtc : row.ObservedFromUtc;
                row.ObservedToUtc = row.ObservedToUtc is null || trade.SourceUtc > row.ObservedToUtc ? trade.SourceUtc : row.ObservedToUtc;
                if (disposition is "BASELINE" or "COUNTER_RESET" or "PREMIUM_RESET" or "NUMERIC_OVERFLOW") row.Partial = true;
                var lag = (raw.ReceivedUtc - trade.SourceUtc).TotalMilliseconds;
                var lagBin = lag < 0 ? "SOURCE_AHEAD" : lag <= 100 ? "0-100ms" : lag <= 500 ? "100-500ms"
                    : lag <= 1000 ? "500-1000ms" : ">1000ms";
                row.SourceLagHistogram[lagBin] = row.SourceLagHistogram.GetValueOrDefault(lagBin) + 1;
                if (lag < 0) { row.FutureSourceEvents++; row.MaximumSourceLeadMs = Math.Max(row.MaximumSourceLeadMs, -lag); }
                if (quotes.DataType is 2 or 3 or 4) row.DelayedEvents++;
                if (comparable && previous != null)
                {
                    if (interval == 0 || previous.SourceUtc >= start)
                    {
                        row.ComparisonPairs++; row.CumulativeVolumeChange += deltaVolume; row.CumulativePremiumChange += deltaPremium;
                        if (disposition == "ACCEPTED") { row.ComparableObservedVolume += trade.Size; row.ComparableObservedPremium += premium; }
                    }
                    else { row.BoundaryCrossings++; row.Partial = true; }
                }
                if (disposition != "ACCEPTED") continue;
                row.Events++; row.ObservedVolume += trade.Size; row.ObservedPremium += premium;
                for (var i = 0; i < matches.Length; i++)
                {
                    row.Scenarios[i].Categories[matches[i].Category].Add(trade.Size, premium);
                    var reasons = row.Scenarios[i].Reasons;
                    reasons[matches[i].Reason] = reasons.GetValueOrDefault(matches[i].Reason) + 1;
                }
                if (matches.Select(x => x.Category).Distinct().Count() > 1) row.SensitivityChanges++;
                if (bidAge is >= 0 and <= 100 || askAge is >= 0 and <= 100) row.NearQuoteChangeEvents++;
                var label = bidAge == null || askAge == null ? "MISSING" : AgeBin(Math.Max(bidAge.Value, askAge.Value));
                row.QuoteAgeHistogram[label] = row.QuoteAgeHistogram.GetValueOrDefault(label) + 1;
            }
        }
        return new(item.RunId, item.ConnectionEpoch, raw.Sequence, raw.RequestId, contract.ConId, scope,
            trade?.SourceUtc, raw.ReceivedUtc, disposition, quotes.Bid?.Sequence, quotes.Ask?.Sequence,
            quotes.BidSizeSequence, quotes.AskSizeSequence, bidAge, askAge,
            trade is null ? null : (raw.ReceivedUtc - trade.SourceUtc).TotalMilliseconds, matches);
    }
    private TradeDecision? NoContract(ProbeEvent item)
    {
        if (item.Raw.Kind != "tickString") return null;
        TradeCallbacks++; Count("MISSING_CONTRACT");
        return new(item.RunId, item.ConnectionEpoch, item.Raw.Sequence, item.Raw.RequestId, null,
            item.Raw.Field == 77 ? "RegularTrades" : "AllTimeAndSales", item.Trade?.SourceUtc,
            item.Raw.ReceivedUtc, "MISSING_CONTRACT", null, null, null, null, null, null, null, []);
    }
    private double? Age(QuoteReference? quote, long now)
        => quote == null || monotonicFrequency <= 0 ? null : ((double)now - quote.MonotonicTicks) * 1000 / monotonicFrequency;
    private static string AgeBin(double age) => age < 0 ? "INVALID" : age <= 100 ? "0-100ms"
        : age <= 250 ? "100-250ms" : age <= 500 ? "250-500ms" : age <= 1000 ? "500-1000ms" : ">1000ms";
    internal static MatchDecision MatchForTest(decimal bid, decimal ask, decimal price, double bidAge, double askAge, int maxAge, int dataType = 1)
        => Match(new Quotes { Bid = new(1, bid, 0, default), Ask = new(2, ask, 0, default), DataType = dataType }, bidAge, askAge, price, maxAge, "ACCEPTED");
    private static MatchDecision Match(Quotes q, double? bidAge, double? askAge, decimal? price, int maxAge, string disposition)
    {
        string? error = disposition != "ACCEPTED" ? disposition : q.DataType != 1 ? "NOT_CONFIRMED_REALTIME"
            : q.Bid == null || q.Ask == null ? "MISSING_QUOTE"
            : q.Bid.Unusable || q.Ask.Unusable || q.Bid.Price <= 0 || q.Ask.Price <= 0 || q.BidSize <= 0 || q.AskSize <= 0 ? "INVALID_QUOTE"
            : q.Bid.Price >= q.Ask.Price ? "LOCKED_OR_CROSSED"
            : bidAge == null || askAge == null || bidAge < 0 || askAge < 0 ? "INVALID_ARRIVAL_CLOCK"
            : maxAge != -1 && (bidAge > maxAge || askAge > maxAge) ? "QUOTE_TOO_OLD" : null;
        if (error != null) return new(maxAge, "UNKNOWN", error);
        var category = price == q.Ask!.Price ? "AT_ASK" : price == q.Bid!.Price ? "AT_BID"
            : price > q.Bid!.Price && price < q.Ask.Price ? "INSIDE_SPREAD" : "OUTSIDE_QUOTE";
        return new(maxAge, category, "RECEIVE_ORDER_ESTIMATE");
    }
    public void AddMissingRows(IEnumerable<OptionContractDescriptor> contracts)
    {
        // Quiet contracts/buckets remain explicitly unknown, never evidence of zero market flow.
        foreach (var contract in contracts)
        foreach (var field in new[] { 48, 77 })
        foreach (var segment in IbTradingHoursParser.Parse(contract.TradingHours, contract.TradingTimeZoneId, contract.Ticker == "SPX"))
        {
            var left = segment.StartUtc > observationStart ? segment.StartUtc : observationStart;
            var right = segment.EndUtc < observationEnd ? segment.EndUtc : observationEnd;
            if (right <= left) continue;
            foreach (var minutes in new[] { 0, 1, 3, 5, 10 })
            {
                var step = TimeSpan.FromMinutes(minutes == 0 ? 1 : minutes);
                var start = segment.StartUtc.AddTicks((left - segment.StartUtc).Ticks / step.Ticks * step.Ticks);
                for (var at = start; at < right; at += step)
                {
                    var et = UsMarketClock.ToEastern(at);
                    var session = NyseTradingCalendar.IsRegularTradingHours(at)
                        && et.TimeOfDay < NyseTradingCalendar.GetRegularClose(et.Date) ? "RTH" : "GTH";
                    var key = (contract.ConId, field, session, minutes, minutes == 0 ? DateTime.MinValue : at);
                    if (_rows.ContainsKey(key)) continue;
                    if (_rows.Count >= 30000) throw new ArgumentException("分析区间过长：桶数量超过保护上限。");
                    var row = new AnalysisRow { Contract = contract, Scope = field == 77 ? "RegularTrades" : "AllTimeAndSales",
                        Session = session, IntervalMinutes = minutes, BucketStartUtc = minutes == 0 ? null : at,
                        BucketEndUtc = minutes == 0 ? null : at + step,
                        Partial = minutes > 0 && (at < left || at + step > right) };
                    row.Count("NO_EVENTS"); _rows[key] = row;
                }
            }
        }
    }
    public AnalysisRow[] Rows(int interval = -1) => _rows.Values.Where(x => interval < 0 || x.IntervalMinutes == interval)
        .OrderBy(x => x.Contract.Ticker, StringComparer.Ordinal).ThenBy(x => x.Contract.ConId)
        .ThenBy(x => x.Scope, StringComparer.Ordinal).ThenBy(x => x.Session, StringComparer.Ordinal)
        .ThenBy(x => x.IntervalMinutes).ThenBy(x => x.BucketStartUtc).ToArray();
}

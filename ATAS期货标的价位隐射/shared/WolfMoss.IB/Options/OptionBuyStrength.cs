namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;

public enum OptionDirection { Buy, Sell, Mid, Unknown }
public enum BuyStrengthStatus { Ready, Filtered, Unavailable, Partial }
public enum DirectionUnknownReason { None, MarketTypeUnconfirmed, NonRealtime, MissingQuote, InvalidPrice, QuoteFlagged, InvalidSize, CrossedQuote, SessionMismatch, InsideSpread }
// Fixed-size counters: no per-trade dictionary, history or allocation.
public readonly record struct DirectionUnknownCounts(decimal MarketTypeUnconfirmed, decimal NonRealtime,
    decimal MissingQuote, decimal InvalidPrice, decimal QuoteFlagged, decimal InvalidSize,
    decimal CrossedQuote, decimal SessionMismatch, decimal InsideSpread)
{
    public decimal this[DirectionUnknownReason reason] => reason switch
    {
        DirectionUnknownReason.MarketTypeUnconfirmed => MarketTypeUnconfirmed,
        DirectionUnknownReason.NonRealtime => NonRealtime,
        DirectionUnknownReason.MissingQuote => MissingQuote,
        DirectionUnknownReason.InvalidPrice => InvalidPrice,
        DirectionUnknownReason.QuoteFlagged => QuoteFlagged,
        DirectionUnknownReason.InvalidSize => InvalidSize,
        DirectionUnknownReason.CrossedQuote => CrossedQuote,
        DirectionUnknownReason.SessionMismatch => SessionMismatch,
        DirectionUnknownReason.InsideSpread => InsideSpread, _ => 0
    };
    public DirectionUnknownCounts Add(DirectionUnknownReason reason, decimal size) => reason switch
    {
        DirectionUnknownReason.MarketTypeUnconfirmed => this with { MarketTypeUnconfirmed = checked(MarketTypeUnconfirmed + size) },
        DirectionUnknownReason.NonRealtime => this with { NonRealtime = checked(NonRealtime + size) },
        DirectionUnknownReason.MissingQuote => this with { MissingQuote = checked(MissingQuote + size) },
        DirectionUnknownReason.InvalidPrice => this with { InvalidPrice = checked(InvalidPrice + size) },
        DirectionUnknownReason.QuoteFlagged => this with { QuoteFlagged = checked(QuoteFlagged + size) },
        DirectionUnknownReason.InvalidSize => this with { InvalidSize = checked(InvalidSize + size) },
        DirectionUnknownReason.CrossedQuote => this with { CrossedQuote = checked(CrossedQuote + size) },
        DirectionUnknownReason.SessionMismatch => this with { SessionMismatch = checked(SessionMismatch + size) },
        DirectionUnknownReason.InsideSpread => this with { InsideSpread = checked(InsideSpread + size) }, _ => this
    };
}
public readonly record struct DirectionCounts(decimal Buy, decimal Sell, decimal Mid, decimal Unknown)
{
    public decimal Total => Buy + Sell + Mid + Unknown;
    public decimal? BuyShare => Buy + Sell > 0 ? Buy / (Buy + Sell) : null;
    public decimal Coverage => Total > 0 ? (Buy + Sell) / Total : 0;
    public DirectionCounts Add(OptionDirection direction, decimal size) => direction switch
    {
        OptionDirection.Buy => this with { Buy = checked(Buy + size) },
        OptionDirection.Sell => this with { Sell = checked(Sell + size) },
        OptionDirection.Mid => this with { Mid = checked(Mid + size) },
        _ => this with { Unknown = checked(Unknown + size) }
    };
}
public sealed record OptionBuyStrength(DirectionCounts Midpoint, DirectionCounts AtQuote,
    BuyStrengthStatus Status, decimal? Gap)
{
    public static readonly OptionBuyStrength Missing = new(default, default, BuyStrengthStatus.Unavailable, null);
    public DirectionUnknownCounts MidpointUnknownReasons { get; init; }
    public DirectionUnknownCounts AtQuoteUnknownReasons { get; init; }
    public string Marker => Status == BuyStrengthStatus.Filtered ? "×" : Status == BuyStrengthStatus.Ready ? "" : "?";
    public static OptionBuyStrength Create(DirectionCounts mid, DirectionCounts at, bool partial = false)
    {
        var gap = mid.BuyShare is { } m && at.BuyShare is { } a ? Math.Abs(m - a) : (decimal?)null;
        return new(mid, at, partial ? BuyStrengthStatus.Partial : gap == null || mid.Total != at.Total
            ? BuyStrengthStatus.Unavailable : gap <= 0.01m + 0.000000000001m ? BuyStrengthStatus.Ready : BuyStrengthStatus.Filtered, gap);
    }
}

internal readonly record struct DirectionPrint(DateTime SourceUtc, decimal Size,
    OptionDirection Midpoint, OptionDirection AtQuote, string Disposition);
internal sealed record OptionBuyStrengthCalendar(OptionTradingSegment[] Segments, OptionTradingSegment[] RegularSegments);

// One instance per shared contract reception, not per chart. Latest quote only;
// four supported intervals, at most three buckets each. No historical quote list.
internal sealed class OptionBuyStrengthState
{
    private readonly object _sync = new();
    private readonly OptionContractDescriptor _contract;
    private readonly OptionTradingSegment[] _segments;
    private readonly OptionTradingSegment[] _regularSegments;
    private static readonly int[] Intervals = [1, 3, 5, 10];
    private readonly Dictionary<(int Minutes, DateTime Start), Bucket> _buckets = new();
    private sealed class Bucket { public DirectionCounts Mid, At; public DirectionUnknownCounts MidReasons, AtReasons; public bool Partial; public OptionBuyStrength? Sealed; }
    private int _request, _dataType;
    private bool _enabled;
    private decimal? _bid, _ask, _bidSize, _askSize;
    private bool _bidInvalid, _askInvalid;
    private DateTime _since, _quoteSegment;
    private bool _quoteRth;
    private Print? _previous;
    private string? _payload;
    private DateTime _lastReceived;
    private long _lastTicks;
    private readonly long _frequency;
    private readonly record struct Print(decimal Price, decimal Size, DateTime Source, decimal Total, decimal Vwap);
    public OptionBuyStrengthState(OptionContractDescriptor contract, OptionBuyStrengthCalendar calendar, long frequency = 0)
    {
        _contract = contract;
        _frequency = frequency == 0 ? System.Diagnostics.Stopwatch.Frequency : frequency;
        _segments = calendar.Segments;
        _regularSegments = calendar.RegularSegments;
    }
    public int CachedBuckets { get { lock (_sync) return _buckets.Count; } }
    public bool AwaitingMarketType { get { lock (_sync) return _enabled && _dataType == 0; } }
    public bool NeedsMarketTypeRefresh(DateTime now)
    {
        lock (_sync) return _enabled && _dataType == 0 && now >= _since.AddSeconds(5);
    }
    public void Start(int request, DateTime now)
    {
        lock (_sync) { Reset(now); _enabled = true; _request = request; }
    }
    public void Stop(DateTime now) { lock (_sync) { Reset(now); _enabled = false; } }
    private void Reset(DateTime now, bool preserveMarketType = false)
    {
        foreach (var pair in _buckets)
            if (pair.Value.Sealed == null && pair.Key.Start.AddMinutes(pair.Key.Minutes).AddSeconds(3) > now) pair.Value.Partial = true;
        _since = now; _bid = _ask = _bidSize = _askSize = null;
        _bidInvalid = _askInvalid = false;
        if (!preserveMarketType) _dataType = 0;
        _previous = null; _payload = null; _lastTicks = 0; _lastReceived = default;
    }
    private OptionTradingSegment Segment(DateTime utc)
    {
        foreach (var s in _segments) if (s.Contains(utc)) return s;
        return default;
    }
    private bool Rth(DateTime utc)
    {
        foreach (var segment in _regularSegments) if (segment.Contains(utc)) return true;
        return false;
    }
    private void AdvanceClock(DateTime received, long ticks)
    {
        if (_lastTicks != 0 && (ticks < _lastTicks || Math.Abs((received - _lastReceived).TotalMilliseconds
            - (ticks - (double)_lastTicks) * 1000 / _frequency) > 250)) Reset(received, preserveMarketType: true);
        _lastReceived = received; _lastTicks = ticks;
        var segment = Segment(received).StartUtc;
        var rth = Rth(received);
        if (segment != _quoteSegment || rth != _quoteRth)
        { _bid = _ask = _bidSize = _askSize = null; _quoteSegment = segment; _quoteRth = rth; }
    }
    public void Quote(int request, int field, decimal? value, bool invalid, bool size, DateTime received, long ticks)
    {
        lock (_sync)
        {
            if (!_enabled || request != _request) return;
            AdvanceClock(received, ticks);
            if (field is 66 or 67 or 69 or 70) _dataType = 3;
            if (size) { if (field is 0 or 69) _bidSize = value; else if (field is 3 or 70) _askSize = value; }
            else if (field is 1 or 66) { _bid = value; _bidInvalid = invalid || value is null or <= 0; }
            else if (field is 2 or 67) { _ask = value; _askInvalid = invalid || value is null or <= 0; }
        }
    }
    public void MarketType(int request, int type, DateTime received, long ticks)
    {
        lock (_sync)
        {
            if (!_enabled || request != _request) return;
            AdvanceClock(received, ticks);
            // Probe resets the feed baseline on every marketDataType callback.
            _previous = null; _payload = null;
            if (_dataType != type) { _bid = _ask = _bidSize = _askSize = null; }
            _dataType = type;
        }
    }
    internal static (OptionDirection Mid, OptionDirection At) Classify(decimal price,
        decimal? bid, decimal? ask, decimal? bidSize, decimal? askSize, int type, bool flagged = false)
    {
        if (UnknownReason(bid, ask, bidSize, askSize, type, flagged) != DirectionUnknownReason.None)
            return (OptionDirection.Unknown, OptionDirection.Unknown);
        if (bid == ask) return (OptionDirection.Mid, OptionDirection.Mid);
        if (price >= ask) return (OptionDirection.Buy, OptionDirection.Buy);
        if (price <= bid) return (OptionDirection.Sell, OptionDirection.Sell);
        var mid = bid.GetValueOrDefault() + (ask.GetValueOrDefault() - bid.GetValueOrDefault()) / 2;
        return (price > mid ? OptionDirection.Buy : price < mid ? OptionDirection.Sell : OptionDirection.Mid, OptionDirection.Unknown);
    }
    internal static DirectionUnknownReason UnknownReason(decimal? bid, decimal? ask,
        decimal? bidSize, decimal? askSize, int type, bool flagged = false)
    {
        if (type == 0) return DirectionUnknownReason.MarketTypeUnconfirmed;
        if (type != 1) return DirectionUnknownReason.NonRealtime;
        if (bid == null || ask == null) return DirectionUnknownReason.MissingQuote;
        if (bid <= 0 || ask <= 0) return DirectionUnknownReason.InvalidPrice;
        if (flagged) return DirectionUnknownReason.QuoteFlagged;
        if (bidSize is null or <= 0 || askSize is null or <= 0) return DirectionUnknownReason.InvalidSize;
        if (bid > ask) return DirectionUnknownReason.CrossedQuote;
        return DirectionUnknownReason.None;
    }
    public DirectionPrint? Trade(int request, string payload, DateTime received, long ticks)
    {
        lock (_sync)
        {
            if (!_enabled || request != _request) return null;
            AdvanceClock(received, ticks);
            Prune();
            var p = Parse(payload);
            if (p == null) { _previous = null; Invalidate(received); return new(default, 0, default, default, "INVALID_RT_FIELDS"); }
            var trade = p.Value; var segment = Segment(trade.Source); var previous = _previous;
            var reason = "ACCEPTED";
            if (segment == default) { _previous = null; reason = "OUTSIDE_SESSION"; }
            else if (previous == null || Segment(previous.Value.Source).StartUtc != segment.StartUtc || Rth(previous.Value.Source) != Rth(trade.Source)) reason = "BASELINE";
            else if (_payload == payload) reason = "DUPLICATE";
            else if (trade.Source < previous.Value.Source) reason = "OUT_OF_ORDER";
            else if (trade.Total < previous.Value.Total) reason = "COUNTER_RESET";
            else
            {
                try
                {
                    var delta = checked((trade.Vwap * trade.Total - previous.Value.Vwap * previous.Value.Total) * _contract.Multiplier);
                    _ = checked(trade.Price * trade.Size * _contract.Multiplier);
                    reason = delta < 0 ? "PREMIUM_RESET" : trade.Total == previous.Value.Total ? "REVISION_OR_REPEAT"
                        : trade.Size <= 0 ? "NO_PRINT_SIZE" : trade.Source == previous.Value.Source && trade.Price == previous.Value.Price
                            && trade.Size == previous.Value.Size ? "AMBIGUOUS_SAME_PRINT" : "ACCEPTED";
                }
                catch (OverflowException) { reason = "NUMERIC_OVERFLOW"; }
            }
            if (reason is not ("DUPLICATE" or "OUT_OF_ORDER" or "OUTSIDE_SESSION")) { _previous = trade; _payload = payload; }
            var classified = Classify(trade.Price, _bid, _ask, _bidSize, _askSize, _dataType, _bidInvalid || _askInvalid);
            var unknown = UnknownReason(_bid, _ask, _bidSize, _askSize, _dataType, _bidInvalid || _askInvalid);
            if (segment != Segment(received) || Rth(trade.Source) != Rth(received))
            {
                classified = (OptionDirection.Unknown, OptionDirection.Unknown);
                unknown = DirectionUnknownReason.SessionMismatch;
            }
            if (segment != default)
            {
                foreach (var minutes in Intervals)
                {
                    var start = segment.StartUtc.AddTicks((trade.Source - segment.StartUtc).Ticks / TimeSpan.FromMinutes(minutes).Ticks * TimeSpan.FromMinutes(minutes).Ticks);
                    var end = start.AddMinutes(minutes);
                    // Match existing fixed publication: late callbacks cannot rewrite the displayed bucket.
                    if (received >= end.AddSeconds(3) || trade.Source > received.AddSeconds(3)) continue;
                    var key = (minutes, start);
                    if (!_buckets.TryGetValue(key, out var bucket)) _buckets[key] = bucket = new() { Partial = start < _since || end > segment.EndUtc };
                    if (bucket.Sealed != null) continue;
                    if (reason is "BASELINE" or "COUNTER_RESET" or "PREMIUM_RESET" or "NUMERIC_OVERFLOW") bucket.Partial = true;
                    if (reason != "ACCEPTED") continue;
                    try
                    {
                        var midCounts = bucket.Mid.Add(classified.Mid, trade.Size);
                        var atCounts = bucket.At.Add(classified.At, trade.Size);
                        var midReasons = bucket.MidReasons.Add(unknown, trade.Size);
                        var atReasons = bucket.AtReasons.Add(unknown != DirectionUnknownReason.None ? unknown
                            : classified.At == OptionDirection.Unknown ? DirectionUnknownReason.InsideSpread : DirectionUnknownReason.None, trade.Size);
                        bucket.Mid = midCounts; bucket.At = atCounts; bucket.MidReasons = midReasons; bucket.AtReasons = atReasons;
                    }
                    catch (OverflowException) { bucket.Partial = true; }
                }
            }
            return new(trade.Source, trade.Size, classified.Mid, classified.At, reason);
        }
    }
    private void Invalidate(DateTime now)
    {
        foreach (var pair in _buckets) if (pair.Key.Start.AddMinutes(pair.Key.Minutes) > now) pair.Value.Partial = true;
        _since = now;
    }
    private void Prune()
    {
        // Constant bound even after long sessions and source clock jumps.
        foreach (var minutes in Intervals)
        {
            int count; DateTime oldest;
            do
            {
                count = 0; oldest = DateTime.MaxValue;
                foreach (var k in _buckets.Keys)
                    if (k.Minutes == minutes) { count++; if (k.Start < oldest) oldest = k.Start; }
                if (count > 2) _buckets.Remove((minutes, oldest));
            } while (count > 2);
        }
    }
    public OptionBuyStrength Read(DateTime start, DateTime end, DateTime now)
    {
        lock (_sync)
        {
            if (now < end.AddSeconds(3) || !_buckets.TryGetValue(((int)(end - start).TotalMinutes, start), out var b)) return OptionBuyStrength.Missing;
            return b.Sealed ??= OptionBuyStrength.Create(b.Mid, b.At, b.Partial) with
                { MidpointUnknownReasons = b.MidReasons, AtQuoteUnknownReasons = b.AtReasons };
        }
    }
    private static Print? Parse(string text)
    {
        var span = text.AsSpan();
        Span<Range> parts = stackalloc Range[7];
        static bool N(ReadOnlySpan<char> s, out decimal n) => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n);
        if (span.Split(parts, ';') != 6 || !N(span[parts[0]], out var price) || price <= 0 || !N(span[parts[1]], out var size) || size < 0
            || !long.TryParse(span[parts[2]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            || !N(span[parts[3]], out var total) || total < 0 || !N(span[parts[4]], out var vwap) || vwap < 0 || !bool.TryParse(span[parts[5]], out _)) return null;
        try { return new(price, size, DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, total, vwap); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}

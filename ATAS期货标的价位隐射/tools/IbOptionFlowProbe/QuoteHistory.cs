using System.Globalization;
using WolfMoss.ATAS.PriceMapping;

namespace IbOptionFlowProbe;

internal sealed record HistoricalQuote(long Sequence, decimal Price, long Ticks, DateTime Utc, bool Invalid);
internal sealed record QuoteState(long Ticks, long Sequence, HistoricalQuote? Bid, HistoricalQuote? Ask,
    decimal? BidSize, decimal? AskSize, long? BidSizeSequence, long? AskSizeSequence, int DataType);
internal sealed record QuoteLookup(QuoteState? State, string? Error);

// A bounded chronological deque with binary search. Checkpoint retains the state at the horizon.
internal sealed class QuoteHistory(long frequency, int capacity = 65536, int seconds = 60)
{
    private readonly List<QuoteState> _states = new();
    private int _head;
    private double _floor = double.NegativeInfinity;
    public int Count => _states.Count - _head;
    public int Peak { get; private set; }
    public long Evictions { get; private set; }
    public QuoteState? Latest => Count == 0 ? null : _states[^1];
    public void Clear() { _states.Clear(); _head = 0; _floor = double.NegativeInfinity; }
    public void Add(IbRawMarketEvent raw)
    {
        if (capacity < 2 || frequency <= 0 || seconds <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        var old = Latest ?? new QuoteState(raw.MonotonicTicks, raw.Sequence, null, null, null, null, null, null, 0);
        if (raw.MonotonicTicks < old.Ticks) { Clear(); old = new(raw.MonotonicTicks, raw.Sequence, null, null, null, null, null, null, 0); }
        var state = old with { Ticks = raw.MonotonicTicks, Sequence = raw.Sequence };
        if (raw.Kind == "marketDataType")
        {
            if (old.DataType == raw.MarketDataType) return;
            state = state with { DataType = raw.MarketDataType ?? 0, Bid = null, Ask = null, BidSize = null, AskSize = null, BidSizeSequence = null, AskSizeSequence = null };
        }
        else
        {
            if (raw.Field is 66 or 67 or 69 or 70) state = state with { DataType = 3 };
            var valid = decimal.TryParse(raw.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value);
            if (raw.Kind == "tickPrice")
            {
                var quote = new HistoricalQuote(raw.Sequence, value, raw.MonotonicTicks, raw.ReceivedUtc,
                    !valid || value <= 0 || raw.PreOpen == true || raw.PastLimit == true);
                if (raw.Field is 1 or 66) state = state with { Bid = quote };
                else if (raw.Field is 2 or 67) state = state with { Ask = quote };
                else return;
            }
            else if (raw.Kind == "tickSize")
            {
                if (raw.Field is 0 or 69) state = state with { BidSize = valid ? value : 0, BidSizeSequence = raw.Sequence };
                else if (raw.Field is 3 or 70) state = state with { AskSize = valid ? value : 0, AskSizeSequence = raw.Sequence };
                else return;
            }
            else return;
        }
        _states.Add(state);
        Trim(raw.MonotonicTicks);
        while (Count > capacity) { _head++; Evictions++; _floor = Math.Max(_floor, _states[_head].Ticks); }
        Peak = Math.Max(Peak, Count);
        if (_head >= 4096 && _head * 2 >= _states.Count) { _states.RemoveRange(0, _head); _head = 0; }
    }
    private void Trim(long now)
    {
        var horizon = now - (double)seconds * frequency;
        _floor = Math.Max(_floor, horizon);
        while (Count > 1 && _states[_head + 1].Ticks <= horizon) _head++;
    }
    public QuoteLookup At(double target, long receiveTicks)
    {
        if (target > receiveTicks) return new(null, "FUTURE_CANDIDATE");
        Trim(receiveTicks);
        if (target < _floor) return new(null, "HISTORY_UNAVAILABLE");
        var low = _head; var high = _states.Count - 1; var found = -1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (_states[mid].Ticks <= target) { found = mid; low = mid + 1; } else high = mid - 1;
        }
        return found < 0 ? new(null, "MISSING_QUOTE_HISTORY") : new(_states[found], null);
    }
}

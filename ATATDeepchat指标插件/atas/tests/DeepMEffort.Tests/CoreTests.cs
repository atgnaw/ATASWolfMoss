using WolfMoss.ATAS.DeepMEffort.Core;

internal static class CoreTests
{
    internal static int Count;
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Count++;
        Console.WriteLine("PASS " + message);
    }

    internal static EffortBar Bar(int i, double open = 100, double close = 110,
        double high = 112, double low = 98, long ask = 600, long bid = 400,
        long elapsed = 30_000_000) => new(i, open, high, low, close, (long)(open * 4),
            (long)(close * 4), ask, bid, ask + bid, elapsed);

    internal static EffortEngine Bull(EffortConfig? config = null)
    {
        var e = new EffortEngine(config ?? new());
        e.CloseBar(Bar(0));
        e.CloseBar(Bar(1, 110, 120, 122, 108));
        return e;
    }

    internal static EffortBar Break(int i, double price = 119) => Bar(i, price, price, price + 1, price - 1, 500, 500);

    internal static void Run()
    {
        var e = new EffortEngine(new());
        var m = e.CloseBar(Bar(0, 100, 102.25, 103, 99, 449, 300));
        Check(m.Effort == 14 && m.IsEligible, "149 / 10 is eligible integer effort 14");
        m = e.CloseBar(Bar(1, 102.25, 100, 103, 99, 300, 449));
        Check(m.Effort == -14 && m.IsEligible, "Negative effort truncates toward zero");
        m = e.CloseBar(Bar(2, elapsed: 45_999_999));
        Check(m.ElapsedSeconds == 45 && m.IsEligible, "45.999999 seconds passes integer-second threshold");
        Check(!e.CloseBar(Bar(3, elapsed: 46_000_000)).IsEligible, "46 seconds fails threshold");
        Check(!e.CloseBar(Bar(4, 100, 100, ask: 400, bid: 600)).IsEligible, "Negative delta on a doji is ineligible");
        Check(!e.CloseBar(Bar(5, ask: 950, bid: 50)).IsEligible, "Excessive delta percent is filtered");

        var zeroDelta = new EffortEngine(new() { MinDeltaPercent = 0 });
        Check(zeroDelta.CloseBar(Break(0)).IsEligible, "Zero delta is nonnegative when the minimum filter is disabled");
        e = Bull();
        Check(e.Zones.Count == 0 && e.Pending is not null, "Two qualifying bars do not publish before interruption");
        e.CloseBar(Break(2));
        var z = e.Zones.Single();
        Check(z.CreatedIndex == 2 && z.Lower == 98 && z.Upper == 110 && z.IsBullish && z.IsActive,
            "Bullish zone starts on interruption and retains lower half of 98..122");
        e.CloseBar(Bar(3, 100, 100, 125, 90, 500, 500));
        Check(e.Zones[0].IsActive, "Wick below lower boundary does not stop a bullish zone");
        e.CloseBar(Break(4, 98));
        Check(e.Zones[0].IsActive, "Exact body touch does not stop a zone");
        e.CloseBar(Break(5, 97.75));
        Check(!e.Zones[0].IsActive && e.Zones[0].EndIndex == 6 && e.Zones[0].StopReason == StopReason.Boundary,
            "Strict body breach ends at next bar coordinate");

        e = Bull(); e.CloseBar(Break(2, 97));
        Check(!e.Zones[0].IsActive && e.Zones[0].EndIndex == 3, "Creation bar can stop its own new zone");
        e = Bull(new() { MaxZoneExtensions = 2 }); e.CloseBar(Break(2)); e.CloseBar(Break(3)); e.CloseBar(Break(4));
        Check(e.Zones[0].EndIndex == 5 && e.Zones[0].StopReason == StopReason.Age, "Age is measured from creation with a +1 right endpoint");
        e = Bull(new() { FullRangeMaxBars = 2 }); e.CloseBar(Break(2));
        Check(e.Zones[0].Lower == 98 && e.Zones[0].Upper == 122, "Short segments retain the full high/low range");
        e = Bull(new() { EntryZonePercent = 100 }); e.CloseBar(Break(2));
        Check(e.Zones[0].Lower == e.Zones[0].Upper, "100 percent trimming produces a drawable boundary line");

        foreach (var startsAbove in new[] { false, true })
        {
            e = new(new());
            e.CloseBar(Bar(0, 120, 110, 122, 108, 400, 600));
            e.CloseBar(Bar(1, 110, 100, 112, 98, 400, 600));
            e.CloseBar(Break(2, startsAbove ? 123 : 105));
            Check(e.Zones[0].Lower == 110 && e.Zones[0].Upper == 122
                && e.Zones[0].StopsOnAbove != startsAbove, "Bearish boundary mode follows registration close: " + startsAbove);
            e.CloseBar(Break(3, 122));
            Check(e.Zones[0].IsActive, "Bearish exact boundary touch remains active: " + startsAbove);
            e.CloseBar(Break(4, startsAbove ? 121.75 : 122.25));
            Check(!e.Zones[0].IsActive, "Both asymmetric bearish breach modes work: " + startsAbove);
        }

        e = new(new());
        e.CloseBar(Bar(0, 100, 110, 120, 90));
        e.CloseBar(Bar(1, 110, 110, 140, 80, 0, 0));
        e.CloseBar(Bar(2, 110, 120, 130, 100));
        Check(e.ConsecutiveBars == 2 && e.Pending is { High: 140, Low: 80 }, "Zero volume preserves count but participates in physical range scan");
        e.CloseBar(Break(3));
        Check(e.Zones[0].Upper == 110, "Physical-bar range determines zone midpoint");

        e = Bull(new() { EmaLength = 1 });
        e.CloseBar(Break(2) with { HasKnownAggressor = false });
        Check(e.Zones.Count == 0 && e.Pending is null && e.Ema == 119, "Unknown direction discards pending signal but advances EMA");
        e = Bull(); e.CloseBar(Break(2) with { HasValidElapsed = false });
        Check(e.Zones.Count == 0 && e.Pending is null, "Invalid elapsed time does not confirm a pending segment");
        e = new(new() { MinimumBars = 1 });
        e.CloseBar(Bar(0)); e.CloseBar(Bar(1, 110, 100, 112, 98, 400, 600));
        Check(e.Zones.Count == 0 && e.Pending is { IsBullish: false }, "MinimumBars=1 retains pending range across a direction switch");

        e = new(new());
        for (var i = 0; i < 21; i++) e.CloseBar(Break(i, 100 + i));
        Check(e.Ema is null, "Default EMA has no output on the first 21 closed bars");
        e.CloseBar(Break(21, 121));
        Check(e.Ema == 121, "EMA is seeded with close of bar 22, not SMA");
        e.CloseBar(Break(22, 142));
        Check(e.Ema == 123, "EMA recurrence uses alpha 2/21 exactly once per close");
        var prior = e.Ema;
        try { e.CloseBar(Break(22)); throw new Exception("Expected duplicate rejection"); }
        catch (ArgumentException) { }
        Check(e.Ema == prior && e.NextBarIndex == 23, "Repeated closed index is rejected before changing state");
        e = Bull(); e.CloseBar(Break(2)); e.Freeze(3, StopReason.Session);
        Check(!e.Zones[0].IsActive && e.Zones[0].EndIndex == 3, "Session freeze uses new session's first bar coordinate");
        e = Bull(); e.Freeze(2, StopReason.Session);
        Check(e.Pending is not null && e.ConsecutiveBars == 2, "Session freeze preserves unconfirmed segment");
        e.CloseBar(Break(2));
        Check(e.Zones.Count == 1, "A preserved segment can confirm after session boundary");

        InputTests();
    }

    private static void InputTests()
    {
        var t = new DateTime(2026, 10, 19, 10, 0, 0);
        var input = new CandleInput(100, 112, 98, 110, 600, 400, 1000, 0, t, t.AddSeconds(20));
        var bar = input.ToBar(0, .25m, t.AddSeconds(45).AddTicks(9_999_990), out var issues);
        Check(issues == InputIssue.None && bar.OpenTicks == 400 && bar.CloseTicks == 440 && bar.ElapsedMicroseconds == 45_999_999,
            "ATAS decimal prices convert to exact tick coordinates and elapsed microseconds");
        _ = (input with { Betweens = 1, Volume = 1001 }).ToBar(0, .25m, t.AddSeconds(30), out issues);
        Check(issues == InputIssue.Volume, "Unclassified between trades are not guessed into Ask/Bid");
        bar = (input with { Ask = 600.5m, Volume = 1000.5m }).ToBar(0, .25m, t.AddSeconds(30), out issues);
        Check(issues == InputIssue.Volume && !bar.HasKnownAggressor, "Fractional lots are explicitly rejected instead of truncated");
        bar = input.ToBar(0, .25m, t.AddSeconds(10), out issues);
        Check(issues == InputIssue.Time && !bar.HasValidElapsed, "Next candle preceding previous last trade invalidates timing");
        _ = input.ToBar(0, .3m, t.AddSeconds(30), out issues);
        Check(issues == InputIssue.TickGrid, "Off-grid price does not silently round tick distance");
        _ = input.ToBar(0, 0, t.AddSeconds(30), out issues);
        Check(issues == InputIssue.TickGrid, "Missing tick size is explicitly flagged");
    }
}

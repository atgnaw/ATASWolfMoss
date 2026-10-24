using WolfMoss.ATAS.DeepVTracker;

internal static class CoreTests
{
    internal static int Count;
    internal static void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); Count++; Console.WriteLine("PASS " + name); }
    internal static long Epoch => new DateTime(2026, 10, 19, 10, 0, 0).Ticks;
    internal static FlowBar Bar(int i, long ask = 60, long bid = 40, long close = 100, long high = 102, long low = 98) =>
        new(i, Epoch + i * TimeSpan.TicksPerSecond * 2, Epoch + (i + 1) * TimeSpan.TicksPerSecond * 2,
            100, high, low, close, ask, bid, ask + bid, 6, 4, [new(100, ask, bid, ask + bid)]);
    internal static TradeTick Tick(long time, long price = 100, long volume = 10, TradeSide side = TradeSide.Buy) => new(Epoch + time, price, volume, side);
    internal static ChartBar Chart(int i, params TradeTick[] ticks)
    {
        var rows = ticks.GroupBy(t => t.Price).OrderBy(g => g.Key).Select(g => new PriceLevel(g.Key,
            g.Where(t => t.Side == TradeSide.Buy).Sum(t => t.Volume), g.Where(t => t.Side == TradeSide.Sell).Sum(t => t.Volume), g.Sum(t => t.Volume))).ToArray();
        return new(new(i, ticks[0].Time, ticks[^1].Time + 1, ticks[0].Price, ticks.Max(t => t.Price), ticks.Min(t => t.Price), ticks[^1].Price,
            rows.Sum(p => p.Ask), rows.Sum(p => p.Bid), rows.Sum(p => p.Total),
            ticks.Count(t => t.Side == TradeSide.Buy), ticks.Count(t => t.Side == TradeSide.Sell), rows, ticks.All(t => t.Side != TradeSide.Unknown)), ticks[^1].Time, ticks.Length);
    }
    internal static void Run()
    {
        var samples = new Samples(3); samples.Add(1); samples.Add(2); samples.Add(3);
        Check(samples.Mean == 2 && samples.Deviation == 1, "Sample standard deviation uses n-1");
        samples.Add(4); Check(samples.Mean == 3 && samples.Deviation == 1 && samples.Added == 4, "Rolling Welford removes oldest sample");
        var cloned = samples.Clone(); cloned.Add(100);
        Check(samples.Mean == 3 && cloned.Mean > 30, "Statistics checkpoints do not share mutable queues");
        var zeros = new Samples(3); zeros.Add(0); Check(zeros.Threshold(6) == 0, "Zero mean preserves source threshold shortcut");
        AccelerationTests(); ExhaustionTests(); SlowdownTests(); LevelTests(); ReplayTests(); LifetimeTests();
    }
    private static void AccelerationTests()
    {
        var a = new Acceleration(); a.Open();
        Check(a.Trade(Tick(0), 0, SignalStrength.Medium) is null, "Acceleration waits for next bucket boundary");
        Check(a.Trade(Tick(TimeSpan.TicksPerSecond - 1, 101), 0, SignalStrength.Medium) is null, "One tick before deadline stays in same bucket");
        var s = a.Trade(Tick(TimeSpan.TicksPerSecond, 103), 0, SignalStrength.Medium);
        Check(s is { Lower: 100, Upper: 101, Up: true, StartOffset: .5, Lifetime: 12 }, "Boundary trade belongs to new bucket; prior bucket draws its own range");
        Check(a.Trade(Tick(2 * TimeSpan.TicksPerSecond), 0, SignalStrength.Weak) is null, "Only one acceleration sample is permitted per opened chart bar");
        var first = new Acceleration(); first.Open(); first.Trade(Tick(0, 100, 1, TradeSide.Sell), 0, SignalStrength.Strong);
        s = first.Trade(Tick(TimeSpan.TicksPerSecond), 0, SignalStrength.Strong);
        Check(s is { Up: true }, "First bucket retains price-based volume and delta seed");
        var spike = new Acceleration(); spike.Open(); spike.Trade(Tick(0, 100, 10), 0, SignalStrength.Medium);
        for (var i = 1; i <= 55; i++) { spike.Open(); spike.Trade(Tick(i * TimeSpan.TicksPerSecond, 100, 110), i, SignalStrength.Medium); }
        spike.Open(); spike.Trade(Tick(56 * TimeSpan.TicksPerSecond, 100, 100_000), 56, SignalStrength.Medium);
        spike.Open(); s = spike.Trade(Tick(57 * TimeSpan.TicksPerSecond), 57, SignalStrength.Medium);
        Check(s is { Created: 56 }, "Extreme volume spike passes candidate-inclusive 50-sample threshold");
        var fork = spike.Clone();
        Check(fork.Trade(Tick(58 * TimeSpan.TicksPerSecond), 58, SignalStrength.Medium)
            == spike.Trade(Tick(58 * TimeSpan.TicksPerSecond), 58, SignalStrength.Medium), "Acceleration clone follows identical future stream");
    }
    private static void ExhaustionTests()
    {
        var volume = new Exhaustion(); var count = new Exhaustion();
        for (var i = 0; i < 4; i++)
        {
            Check(volume.Close(Bar(i, 40, 60), SignalStrength.Medium, false) is null, "Negative rolling segment has not flipped " + i);
            count.Close(Bar(i, 40, 60), SignalStrength.Medium, true);
        }
        var flip = Bar(4, 1000, 0);
        var s = volume.Close(flip, SignalStrength.Medium, false);
        Check(s is { Up: true, Lower: 95, Kind: SignalKind.ExhaustionVolume }, "Four-bar negative segment confirms upward volume exhaustion on rolling flip");
        Check(count.Close(flip, SignalStrength.Medium, true) is null, "Trade-count detector is independent from contract volume");
        var weak = new Exhaustion(); var medium = new Exhaustion();
        for (var i = 0; i < 2; i++) { weak.Close(Bar(i, 40, 60), SignalStrength.Weak, false); medium.Close(Bar(i, 40, 60), SignalStrength.Medium, false); }
        Check(weak.Close(Bar(2, 1000, 0), SignalStrength.Weak, false) is not null
            && medium.Close(Bar(2, 1000, 0), SignalStrength.Medium, false) is null, "Strength changes minimum segment length");
        var zeroFlip = new Exhaustion(); for (var i = 0; i < 4; i++) zeroFlip.Close(Bar(i, 40, 60), SignalStrength.Medium, false);
        Check(zeroFlip.Close(Bar(4, 80, 0), SignalStrength.Medium, false) is { Up: true }, "Rolling delta zero is on the nonnegative side");
    }
    private static void SlowdownTests()
    {
        var slow = new Slowdown();
        slow.Trade(Tick(0, 100, 100, TradeSide.Buy), 8, SignalStrength.Medium);
        Check(slow.Evaluate(Bar(0), new(true, 0, 5, 90, 120)) is null, "Slowdown needs a learned threshold and minimum span");
        slow.Trade(Tick(1, 110, 1, TradeSide.Sell), 8, SignalStrength.Medium);
        slow.Trade(Tick(2, 111, 2, TradeSide.Sell), 8, SignalStrength.Medium);
        slow.Trade(Tick(3, 112, 3, TradeSide.Sell), 8, SignalStrength.Medium);
        slow.Trade(Tick(4, 113, 50, TradeSide.Buy), 8, SignalStrength.Medium);
        var visibility = new Visibility(true, 0, 5, 90, 120);
        var s = slow.Evaluate(Bar(1), visibility);
        Check(s is { Up: true, Lower: 110, Visible: true }, "Low-end bid taper 1,2,3 produces upward slowdown");
        var id = s!.Id;
        Check(slow.Evaluate(Bar(2), visibility) is null && slow.Marker!.Start == 1, "Unchanged extreme preserves old marker X instead of reprojecting");
        slow.Trade(Tick(5, 109, 1, TradeSide.Sell), 8, SignalStrength.Medium);
        s = slow.Evaluate(Bar(2), visibility);
        Check(s is { Lower: 109, Start: 2 } && s.Id == id, "Moving extreme reuses existing slowdown identity");
        slow.Trade(Tick(6, 109, 200, TradeSide.Sell), 8, SignalStrength.Medium);
        Check(slow.Evaluate(Bar(2), visibility) is { Visible: false }, "Broken taper hides the previous marker");
        Check(slow.Reveal() is { Visible: true }, "New compression may reveal marker without retesting taper");
        var fork = slow.Clone(); fork.ResetRange(200, SignalStrength.Strong);
        Check(slow.Marker!.Id == id, "Slowdown clone retains independent mutable price state");

        var empty = new Slowdown(); empty.Trade(Tick(0, 100, 100), 8, SignalStrength.Medium);
        empty.Trade(Tick(1, 110, 1), 8, SignalStrength.Medium); empty.Trade(Tick(2, 113, 1), 8, SignalStrength.Medium);
        s = empty.Evaluate(Bar(1), new(false, 0, 5, 100, 105));
        Check(s is { Up: true, Visible: false }, "Untraded bid levels count as zero but range visibility can hide marker");
    }
    private static void LevelTests()
    {
        var cfg = new TrackerConfig(); var levels = new Levels();
        for (var i = 0; i <= 5; i++) levels.Close(Bar(i), cfg, 4);
        Check(levels.Visibility(4).Active, "Range compression activates after required source warm-up");
        var clone = levels.Clone();
        var equal = clone.Close(Bar(6, close: 101), cfg, 4);
        Check(equal.Signals.Length == 0 && clone.Visibility(4).Active, "Exit distance equality does not confirm levels");
        var result = levels.Close(Bar(6, close: 102), cfg, 4);
        Check(result.Signals.Length == 2, "Compression exit publishes a control and an extreme line");
        Check(result.Signals[0] is { Lower: 100, Kind: SignalKind.Control, Ask: true, Absorption: false }
            && result.Signals[1].Lower == 100, "Conservative lines use POC and initial body bounds, not initial wicks");
        var bearishExit = new Levels(); for (var i = 0; i <= 5; i++) bearishExit.Close(Bar(i), cfg, 4);
        result = bearishExit.Close(Bar(6, close: 98, high: 100), cfg, 4);
        Check(result.Signals[0] is { Ask: true, Absorption: true }, "Positive delta with downward-only exit is absorption");
        var both = new Levels(); for (var i = 0; i <= 5; i++) both.Close(Bar(i), cfg, 4);
        result = both.Close(Bar(6, close: 98, high: 101), cfg, 4);
        Check(!result.Signals[0].Absorption, "Source classification uses High above body even on downward close");
        foreach (var strength in new[] { SignalStrength.Medium, SignalStrength.Strong })
        {
            var l = new Levels(); var c = cfg with { Absorption = strength };
            for (var i = 0; i <= 5; i++) l.Close(Bar(i), c, 4);
            var output = l.Close(Bar(6, high: 103), c, 4).Signals;
            Check((output.Length == 2) == (strength == SignalStrength.Strong), "Only Strong accepts wick exit: " + strength);
        }
        var other = new Levels();
        for (var i = 0; i < 21; i++) other.Close(Bar(i), cfg, 0);
        Check(!other.Visibility(0).Active, "Non-Range compression waits beyond 21 true-range writes");
        other.Close(Bar(21), cfg, 0);
        Check(other.Visibility(0).Active && other.Visibility(0).Lookback == 5, "Non-Range compression becomes eligible on write 22");
        var area = ProfileArea.Calculate([Bar(0) with { Profile = [new(99, 30, 0, 30), new(100, 100, 0, 100), new(101, 20, 0, 20)] }]);
        Check(area == new ProfileArea(100, 98, 101), "Value-area expansion excludes POC and preserves missing-level final step");
        area = ProfileArea.Calculate([Bar(0) with { Profile = [new(100, 100, 0, 100), new(102, 50, 0, 50)] }]);
        Check(area.High == 100, "Sparse profile stops at missing immediate neighbors rather than jumping a gap");
        area = ProfileArea.Calculate([Bar(0) with { Open = 101, Profile = [new(100, 50, 0, 50), new(101, 50, 0, 50)] }]);
        Check(area.Poc == 101, "Equal-volume POC follows original open-up-then-down visitation order");
    }
    private static void LifetimeTests()
    {
        var config = new TrackerConfig { EnableAcceleration = false, EnableExhaustion = false, EnableSlowdown = false, ProjectionBars = 3 };
        var engine = new TrackerEngine(config, 4);
        var output = new Dictionary<long, Signal>();
        void Emit(Signal signal) => output[signal.Id] = signal;
        void Close(TrackerEngine target, FlowBar bar) { target.Open(bar, Emit); target.Close(bar, Emit); }
        for (var i = 0; i < 6; i++) Close(engine, Bar(i));
        Close(engine, Bar(6, close: 102));
        var id = output.Values.Single(s => s.Kind == SignalKind.Control).Id;
        Check(output[id].Active && output[id].Created == 6, "New levels remain active in their creation callback");
        Close(engine, Bar(7, close: 100, low: 90));
        Check(output[id].Active, "Wick below stop and body equality do not stop a level");
        var down = engine.Clone(); Close(down, Bar(8, close: 99));
        Check(!output[id].Active && output[id].End == 8, "Strict body break freezes line at the triggering bar");
        Close(engine, Bar(8, close: 100));
        Check(output[id].Active, "Independent engine checkpoint survives other branch stop");
        Close(engine, Bar(9, close: 100));
        Check(!output[id].Active && output[id].End == 9, "Line expires at configured index span including equality");

        engine = new TrackerEngine(config, 4); output.Clear();
        for (var i = 0; i < 6; i++) Close(engine, Bar(i));
        Close(engine, Bar(6, close: 98, high: 100));
        id = output.Values.Single(s => s.Kind == SignalKind.Control).Id;
        Close(engine, Bar(7, close: 100, high: 110));
        Check(output[id].Active && output[id].StopsAbove, "Opposite registration monitors upper body with strict equality");
        Close(engine, Bar(8, close: 101));
        Check(!output[id].Active, "Upper body crossing stops the opposite registration");
    }
    private static void ReplayTests()
    {
        var ticks = new[] { Tick(0), Tick(TimeSpan.TicksPerSecond, 101), Tick(TimeSpan.TicksPerSecond + 1, 100, 5, TradeSide.Sell) };
        var chart = Chart(0, ticks);
        var replay = new TradeReplay(new(), 4, 0);
        foreach (var t in ticks) replay.Tick(chart, t);
        Check(replay.Signals.Count > 0, "Live transaction exposes provisional acceleration");
        Check(!replay.Close(chart with { Flow = chart.Flow with { Volume = 99 } }) && replay.Signals.Count == 0 && replay.NextIndex == 1,
            "Failed reconciliation rolls back preview signals and advances through an explicit gap");
        replay = new(new(), 4, 0); foreach (var t in ticks) replay.Tick(chart, t);
        Check(replay.Close(chart) && replay.Signals.Count > 0, "Complete OHLC, quantity, count and footprint commit signals");
        var expected = replay.Signals.Values.ToArray();
        var historic = new TradeReplay(new(), 4, 0); historic.History([chart], ticks);
        Check(historic.Signals.Values.SequenceEqual(expected), "Historical replay and incremental live events produce identical committed output");
        var duplicate = Tick(0);
        historic = new(new(), 4, 0);
        historic.History([Chart(0, duplicate, duplicate), Chart(1, duplicate, duplicate)], [duplicate, duplicate, duplicate, duplicate]);
        Check(historic.NextIndex == 2 && historic.Issues.Count == 0, "Identical same-time prints remain distinct; bar volume disambiguates shared timestamps");
        var wrongProfile = chart with { Flow = chart.Flow with { Profile = [new(100, 20, 5, 25)] } };
        historic = new(new(), 4, 0); historic.History([wrongProfile], ticks);
        Check(historic.Issues.Count == 1 && historic.Signals.Count == 0, "Matching totals cannot hide a wrong footprint distribution");
        var unknown = new[] { Tick(0, side: TradeSide.Unknown), Tick(TimeSpan.TicksPerSecond, side: TradeSide.Unknown) };
        historic = new(new(), 4, 0); historic.History([Chart(0, unknown)], unknown);
        Check(historic.Signals.Values.All(s => s.Kind == SignalKind.Acceleration) && historic.Issues.Count == 1,
            "Unknown aggressors continue total-volume acceleration but disable flow-dependent detectors");
        historic = new(new(), 4, 0); historic.History([chart], []);
        Check(historic.Issues.Count == 1 && historic.Signals.Count == 0, "Missing raw history is not synthesized from OHLC");
        replay = new(new(), 4, 0); foreach (var t in ticks) replay.Tick(chart, t); replay.AbortPreview();
        Check(replay.Signals.Count == 0 && replay.Volume == 0 && replay.NextIndex == 0, "Aborting a failing callback restores bar checkpoint and drawings");
        foreach (var t in ticks) replay.Tick(chart, t); replay.Close(chart);
        Check(replay.Signals.Values.SequenceEqual(expected), "Replay after abort reuses deterministic state without leaked statistics");
    }
}

namespace WolfMoss.ATAS.PriceMapping.Core;

// Connection-local control plane; no network or per-trade processing.
internal sealed class OptionTickerCoordinator
{
    internal sealed record Plan(string Ticker, DateOnly Expiration, long Revision,
        decimal Spot, int CadenceMinutes, DateTime NextSwitchUtc, long Master,
        bool MasterStale, int Consumers, int RequestedLevels);
    private sealed record Demand(string Ticker, DateOnly Expiration, decimal Spot,
        DateTime QuoteUtc, int Minutes, bool Flow, int Levels);
    private sealed class State
    {
        public decimal Spot;
        public long Master, Revision;
        public int Minutes;
        public DateTime Next, Anchor, LastNow;
    }
    private readonly object _sync = new();
    private readonly Dictionary<long, Demand> _owners = new();
    private readonly Dictionary<(string, DateOnly), State> _states = new();
    // Shared Flow books survive socket replacement. A new connection must never
    // restart revisions at 1 and look older than the retained observation book.
    private static long _nextRevision;

    public Plan Update(long owner, string ticker, DateOnly expiration, decimal spot,
        DateTime quoteUtc, int minutes, bool flow, DateTime now, DateTime anchor, int levels = 21)
    {
        lock (_sync)
        {
            ticker = ticker.ToUpperInvariant();
            _owners[owner] = new(ticker, expiration, spot, quoteUtc, minutes, flow, levels);
            Prune();
            var key = (ticker, expiration);
            if (!_states.TryGetValue(key, out var state)) _states.Add(key, state = new State());
            // Ignore sub-second capture/lock inversions between chart loops.
            if (now < state.LastNow && state.LastNow - now < TimeSpan.FromSeconds(2)) now = state.LastNow;
            var consumers = _owners.Where(p => p.Value.Ticker == ticker && p.Value.Expiration == expiration).OrderBy(p => p.Key).ToArray();
            bool Fresh(Demand d) => d.Spot > 0 && d.QuoteUtc <= now && now - d.QuoteUtc <= TimeSpan.FromSeconds(30);
            var master = consumers.FirstOrDefault(p => p.Key == state.Master);
            if (master.Key == 0 || !Fresh(master.Value))
            {
                var fresh = consumers.FirstOrDefault(p => Fresh(p.Value));
                if (fresh.Key != 0) master = fresh;
            }
            if (master.Key == 0) master = consumers.FirstOrDefault(p => p.Value.Spot > 0);
            state.Master = master.Key;
            if (state.Revision != 0 && master.Key != owner) anchor = state.Anchor;
            var cadence = consumers.Where(p => p.Value.Flow).Select(p => p.Value.Minutes).DefaultIfEmpty(1).Min();
            var first = state.Revision == 0;
            var changedSchedule = cadence != state.Minutes || anchor != state.Anchor || now < state.LastNow;
            if (first || changedSchedule)
            {
                state.Minutes = cadence;
                state.Anchor = anchor;
                state.Next = NextBoundary(now, anchor, cadence);
            }
            if (first || (!changedSchedule && now >= state.Next))
            {
                if (master.Key != 0 && (Fresh(master.Value) || first)) state.Spot = master.Value.Spot;
                state.Revision = Interlocked.Increment(ref _nextRevision);
                state.Next = NextBoundary(now, anchor, cadence);
            }
            state.LastNow = now;
            return new(ticker, expiration, state.Revision, state.Spot, state.Minutes,
                state.Next, state.Master, master.Key == 0 || !Fresh(master.Value), consumers.Length, consumers.Max(p => p.Value.Levels));
        }
    }
    public bool Commit(Plan plan, Action apply)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue((plan.Ticker, plan.Expiration), out var state) || state.Revision != plan.Revision) return false;
            if (_owners.Values.Where(d => d.Ticker == plan.Ticker && d.Expiration == plan.Expiration).Max(d => d.Levels) != plan.RequestedLevels) return false;
            apply();
            return true;
        }
    }
    public void Remove(long owner) { lock (_sync) { _owners.Remove(owner); Prune(); } }
    private void Prune()
    {
        foreach (var key in _states.Keys.Where(key => !_owners.Values.Any(d => d.Ticker == key.Item1 && d.Expiration == key.Item2)).ToArray())
            _states.Remove(key);
    }
    internal static DateTime NextBoundary(DateTime now, DateTime anchor, int minutes)
    {
        if (now < anchor) return anchor;
        var step = TimeSpan.FromMinutes(minutes).Ticks;
        return anchor.AddTicks(((now - anchor).Ticks / step + 1) * step);
    }
}

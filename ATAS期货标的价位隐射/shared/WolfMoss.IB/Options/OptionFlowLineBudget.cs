// Source-shared module: compiled privately into each consuming plugin.
namespace WolfMoss.ATAS.PriceMapping.Core;

// One demand per indicator connection lease; identical tickers share the same slice.
internal sealed class OptionFlowLineBudget
{
    private readonly object _sync = new();
    private readonly Dictionary<long, (string Ticker, int Levels, int Budget)> _demands = new();
    public int Update(long owner, string ticker, int levels, int budget)
    {
        lock (_sync)
        {
            _demands[owner] = (ticker, levels, Math.Max(0, budget));
            if (levels == 0) return 0;
            var groups = _demands.Values.Where(static d => d.Levels > 0)
                .GroupBy(static d => d.Ticker, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static g => g.Key, static g => g.Max(d => d.Levels), StringComparer.OrdinalIgnoreCase);
            var capacity = _demands.Values.Min(static d => d.Budget) / 2;
            var allocations = groups.Keys.ToDictionary(static key => key, static _ => 0, StringComparer.OrdinalIgnoreCase);
            // First fund one ATM pair for every ticker, then add symmetric pairs of strikes.
            if (capacity < groups.Count) return 0;
            foreach (var key in groups.Keys) { allocations[key] = 1; capacity--; }
            while (true)
            {
                var eligible = groups.Keys.Where(key => allocations[key] + 2 <= groups[key]).ToArray();
                if (eligible.Length == 0 || capacity < eligible.Length * 2) break;
                foreach (var key in eligible) { allocations[key] += 2; capacity -= 2; }
            }
            return Math.Min(levels, allocations[ticker]);
        }
    }
    public void Remove(long owner) { lock (_sync) _demands.Remove(owner); }
    public int EffectiveBudget(int requested)
    {
        lock (_sync) return _demands.Count == 0 ? requested
            : Math.Min(requested, _demands.Values.Min(static d => d.Budget));
    }
}

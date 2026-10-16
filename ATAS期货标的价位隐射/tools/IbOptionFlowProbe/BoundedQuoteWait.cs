namespace IbOptionFlowProbe;

// A bounded receive-order experiment, separate from the causal latest/source pipelines.
// Exactly the first price change is used, never a later quote selected by trade price.
internal sealed class BoundedQuoteWait(long frequency, int[]? thresholds = null)
{
    private readonly int[] _thresholds = thresholds ?? DirectionSettings.WaitThresholds;
    private sealed class Pending(long conId, long ticks, DateTime utc,
        Action<int, string, QuoteState?, long, DateTime> complete, int count, Func<QuoteState, long, DateTime, bool>? accept)
    {
        public Func<QuoteState, long, DateTime, bool>? Accept = accept;
        public long ConId = conId, Ticks = ticks;
        public DateTime Utc = utc;
        public bool[] Done = new bool[count];
        public Action<int, string, QuoteState?, long, DateTime> Complete = complete;
    }
    private readonly LinkedList<Pending> _pending = new();
    public void Add(long conId, long ticks, DateTime utc, Action<int, string, QuoteState?, long, DateTime> complete, Func<QuoteState, long, DateTime, bool>? accept = null)
    {
        if (_pending.Count >= 65536) throw new InvalidOperationException("有限等待队列超过保护上限。");
        _pending.AddLast(new Pending(conId, ticks, utc, complete, _thresholds.Length, accept));
    }
    private void Visit(Action<Pending> act)
    {
        for (var node = _pending.First; node != null;)
        {
            var next = node.Next; act(node.Value);
            if (node.Value.Done.All(x => x)) _pending.Remove(node);
            node = next;
        }
    }
    private void Resolve(Pending p, int i, string status, QuoteState? quote, long ticks, DateTime utc)
    {
        if (p.Done[i]) return;
        p.Done[i] = true;
        p.Complete(_thresholds[i], status, quote, ticks, utc);
    }
    public void Advance(long now)
        => Visit(p =>
        {
            for (var i = 0; i < p.Done.Length; i++)
            {
                var wait = _thresholds[i];
                var deadline = p.Ticks + wait * frequency / 1000d;
                // Inclusive deadline: a quote received exactly at the limit participates.
                if (now > deadline) Resolve(p, i, "TIMEOUT_PRE_ONLY", null, (long)deadline, p.Utc.AddMilliseconds(wait));
            }
        });
    public void PriceChanged(long conId, QuoteState quote, long ticks, DateTime utc)
        => Visit(p =>
        {
            if (p.ConId != conId || ticks < p.Ticks) return;
            if (p.Accept != null && !p.Accept(quote, ticks, utc)) return;
            for (var i = 0; i < p.Done.Length; i++)
                if ((ticks - (double)p.Ticks) * 1000 / frequency <= _thresholds[i])
                    Resolve(p, i, "PAIRED", quote, ticks, utc);
        });
    public void Break(DateTime utc, long? conId = null)
        => Visit(p =>
        {
            if (conId.HasValue && p.ConId != conId) return;
            for (var i = 0; i < p.Done.Length; i++) Resolve(p, i, "WAIT_INTERRUPTED", null, p.Ticks, utc);
        });
}

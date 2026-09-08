namespace WolfMoss.ATAS.PriceMapping;

// Connection-local request IDs, deliberately NOT IB order IDs / nextValidId.
internal sealed class IbRequestIdSequence(int initial = 10_000)
{
    private int _last = initial >= 0 ? initial : throw new ArgumentOutOfRangeException(nameof(initial));
    public int Next()
    {
        while (true)
        {
            var current = Volatile.Read(ref _last);
            if (current == int.MaxValue) throw new InvalidOperationException("IB request ID range exhausted; replace the connection");
            if (Interlocked.CompareExchange(ref _last, current + 1, current) == current) return current + 1;
        }
    }
}

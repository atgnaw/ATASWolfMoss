namespace WolfMoss.ATAS.PriceMapping.Core;

public enum ReferenceMinuteSelectionPolicy
{
    QqqNqCommonMinuteYahoo,
    QqqNqCommonMinuteNasdaq,
    SpxCashMinuteYahoo,
    SpxCashMinuteMarketWatch
}

public readonly record struct ReferenceQuoteRequestKey(
    string Symbol,
    ReferenceMinuteSelectionPolicy SelectionPolicy);

public sealed class ReferenceQuoteCoordinator<TKey, TValue>
    where TKey : notnull
{
    private readonly object _sync = new();
    private readonly Dictionary<TKey, Entry> _entries = new();
    private readonly TimeSpan _successLifetime;
    private readonly TimeSpan _failureLifetime;
    private readonly Func<DateTime> _utcNow;

    public ReferenceQuoteCoordinator(
        TimeSpan successLifetime,
        TimeSpan failureLifetime,
        Func<DateTime>? utcNow = null)
    {
        _successLifetime = successLifetime;
        _failureLifetime = failureLifetime;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<TValue> GetAsync(
        TKey key,
        Func<CancellationToken, Task<TValue>> requestFactory,
        CancellationToken cancellationToken)
    {
        Task<TValue> sharedRequest;
        var now = _utcNow();

        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries.Add(key, entry);
            }

            if (entry.HasValue && now < entry.ValueExpiresUtc)
                return entry.Value!;

            if (entry.Error is not null && now < entry.ErrorExpiresUtc)
                throw entry.Error;

            sharedRequest = entry.InFlight
                            ??= Task.Run(
                                () => FetchAndStoreAsync(key, entry, requestFactory),
                                CancellationToken.None);
        }

        return await sharedRequest.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TValue> FetchAndStoreAsync(
        TKey key,
        Entry entry,
        Func<CancellationToken, Task<TValue>> requestFactory)
    {
        try
        {
            // A chart instance may stop waiting without cancelling the request shared
            // by other instances. The HTTP clients provide the actual request timeout.
            var value = await requestFactory(CancellationToken.None).ConfigureAwait(false);

            lock (_sync)
            {
                entry.Value = value;
                entry.HasValue = true;
                entry.ValueExpiresUtc = _utcNow() + _successLifetime;
                entry.Error = null;
                entry.ErrorExpiresUtc = DateTime.MinValue;
            }

            return value;
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                entry.HasValue = false;
                entry.Value = default;
                entry.Error = exception;
                entry.ErrorExpiresUtc = _utcNow() + _failureLifetime;
            }

            throw;
        }
        finally
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var current)
                    && ReferenceEquals(current, entry))
                {
                    entry.InFlight = null;
                }
            }
        }
    }

    private sealed class Entry
    {
        public bool HasValue { get; set; }

        public TValue? Value { get; set; }

        public DateTime ValueExpiresUtc { get; set; }

        public Exception? Error { get; set; }

        public DateTime ErrorExpiresUtc { get; set; }

        public Task<TValue>? InFlight { get; set; }
    }
}

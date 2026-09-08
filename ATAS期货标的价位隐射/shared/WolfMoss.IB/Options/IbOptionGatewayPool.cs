// Source-shared module: compiled privately into each consuming plugin.
namespace WolfMoss.ATAS.PriceMapping;

internal static class IbOptionGatewayPool
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, SharedClient> Clients = new();
    // Tombstones carry the disconnect barrier across a last-release/new-acquire race.
    private static readonly Dictionary<string, Task> Retiring = new();

    public static Lease Acquire(IbGatewayConnectionOptions options)
    {
        lock (Sync)
        {
            var key = options.PoolKey;
            if (Clients.TryGetValue(key, out var existing)
                && existing.Client is ReflectionIbOptionGatewayClient { ConnectionFaulted: true })
            {
                Clients.Remove(key);
                Retire(key, existing);
            }
            if (!Clients.TryGetValue(key, out var shared))
            {
                Retiring.TryGetValue(key, out var previous);
                shared = new SharedClient(CreateClient(options, previous));
                Clients.Add(key, shared);
            }
            shared.ReferenceCount++;
            return new Lease(key, shared);
        }
    }

    private static IIbOptionGatewayClient CreateClient(IbGatewayConnectionOptions options, Task? previous)
    {
        var assembly = EmbeddedDependencyResolver.TryLoadIbApiAssembly();
        return assembly?.GetType("IBApi.EClientSocket") == null
            ? new UnavailableIbOptionGatewayClient()
            : new ReflectionIbOptionGatewayClient(options, previous);
    }

    // Under Sync. Disposal never executes socket work on the caller.
    private static Task Retire(string key, SharedClient shared)
    {
        if (shared.Disposal != null) return shared.Disposal;
        Retiring.TryGetValue(key, out var previous);
        shared.Disposal = Task.Run(async () =>
        {
            try { if (previous != null) await previous.ConfigureAwait(false); }
            finally { await shared.Client.DisposeAsync().ConfigureAwait(false); }
        });
        Retiring[key] = shared.Disposal;
        _ = RemoveTombstoneAsync(key, shared.Disposal);
        return shared.Disposal;
    }

    private static async Task RemoveTombstoneAsync(string key, Task disposal)
    {
        try { await disposal.ConfigureAwait(false); }
        catch { return; } // A failed retirement must not permit an overlapping socket.
        lock (Sync)
            if (Retiring.TryGetValue(key, out var current) && ReferenceEquals(current, disposal))
                Retiring.Remove(key);
    }

    internal sealed class SharedClient(IIbOptionGatewayClient client)
    {
        public IIbOptionGatewayClient Client { get; } = client;
        public int ReferenceCount;
        public Task? Disposal;
    }

    public sealed class Lease : IAsyncDisposable
    {
        private static long _nextOwner;
        private readonly long _owner = Interlocked.Increment(ref _nextOwner);
        private readonly string _key;
        private SharedClient? _shared;
        internal Lease(string key, SharedClient shared) { _key = key; _shared = shared; }
        public IIbOptionGatewayClient Client => Volatile.Read(ref _shared)?.Client
            ?? throw new ObjectDisposedException(nameof(Lease));
        public int SetFlowDemand(string ticker, int levels, int budget)
            => Client is ReflectionIbOptionGatewayClient client
                ? client.FlowBudget.Update(_owner, ticker, levels, budget) : levels;

        public ValueTask DisposeAsync()
        {
            var shared = Interlocked.Exchange(ref _shared, null);
            if (shared == null) return ValueTask.CompletedTask;
            if (shared.Client is ReflectionIbOptionGatewayClient client) client.FlowBudget.Remove(_owner);
            lock (Sync)
            {
                if (--shared.ReferenceCount != 0) return ValueTask.CompletedTask;
                if (Clients.TryGetValue(_key, out var current) && ReferenceEquals(current, shared))
                    Clients.Remove(_key);
                return new ValueTask(Retire(_key, shared));
            }
        }
    }
}

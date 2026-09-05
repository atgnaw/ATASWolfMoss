namespace WolfMoss.ATAS.PriceMapping;

using System.Collections.Concurrent;

internal static class IbOptionGatewayPool
{
    private static readonly ConcurrentDictionary<string, SharedClient> Clients = new();

    public static Lease Acquire(IbGatewayConnectionOptions options)
    {
        while (true)
        {
            var shared = Clients.GetOrAdd(
                options.PoolKey,
                _ => new SharedClient(CreateClient(options)));

            if (shared.Client is ReflectionIbOptionGatewayClient
                {
                    ConnectionFaulted: true
                })
            {
                Clients.TryRemove(new KeyValuePair<string, SharedClient>(
                    options.PoolKey, shared));
                continue;
            }

            Interlocked.Increment(ref shared.ReferenceCount);
            return new Lease(options.PoolKey, shared);
        }
    }

    private static IIbOptionGatewayClient CreateClient(IbGatewayConnectionOptions options)
    {
        var assembly = EmbeddedDependencyResolver.TryLoadIbApiAssembly();
        return assembly?.GetType("IBApi.EClientSocket") == null
            ? new UnavailableIbOptionGatewayClient()
            : new ReflectionIbOptionGatewayClient(options);
    }

    internal sealed class SharedClient(IIbOptionGatewayClient client)
    {
        public IIbOptionGatewayClient Client { get; } = client;

        public int ReferenceCount;
    }

    public sealed class Lease : IAsyncDisposable
    {
        private readonly string _key;
        private SharedClient? _shared;

        internal Lease(string key, SharedClient shared)
        {
            _key = key;
            _shared = shared;
        }

        public IIbOptionGatewayClient Client
            => _shared?.Client
               ?? throw new ObjectDisposedException(nameof(Lease));

        public async ValueTask DisposeAsync()
        {
            var shared = Interlocked.Exchange(ref _shared, null);

            if (shared == null || Interlocked.Decrement(ref shared.ReferenceCount) != 0)
                return;

            Clients.TryRemove(new KeyValuePair<string, SharedClient>(_key, shared));
            await shared.Client.DisposeAsync().ConfigureAwait(false);
        }
    }
}

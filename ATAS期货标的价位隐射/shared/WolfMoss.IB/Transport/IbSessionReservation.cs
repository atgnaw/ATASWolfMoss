namespace WolfMoss.ATAS.PriceMapping;

// Only identity exclusion is shared across plugin assemblies. No socket, callbacks,
// request IDs, account data or subscriptions are stored in this registry.
internal static class IbSessionReservation
{
    private const string RegistryName = "WolfMoss.IB.SessionReservations.v1";
    private static object Gate => string.Intern(RegistryName);

    internal static string Key(string host, int port, int clientId)
    {
        var normalized = host.Trim().ToUpperInvariant();
        if (normalized is "LOCALHOST" or "127.0.0.1" or "::1" or "[::1]") normalized = "LOOPBACK";
        return $"{normalized}|{port}|{clientId}";
    }

    public static IDisposable Reserve(string host, int port, int clientId)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535 || clientId < 0)
            throw new ArgumentException("Invalid IB connection identity");
        var key = Key(host, port, clientId);
        lock (Gate)
        {
            var registry = Registry();
            if (registry.ContainsKey(key)) throw new InvalidOperationException("CLIENT_ID_IN_USE: choose a distinct IB Client ID for this plugin");
            var token = Guid.NewGuid();
            registry.Add(key, token);
            return new Reservation(key, token);
        }
    }

    private static Dictionary<string, Guid> Registry()
    {
        var value = AppDomain.CurrentDomain.GetData(RegistryName);
        if (value is Dictionary<string, Guid> registry) return registry;
        if (value != null) throw new InvalidOperationException("Incompatible IB session registry");
        var created = new Dictionary<string, Guid>(StringComparer.Ordinal);
        AppDomain.CurrentDomain.SetData(RegistryName, created);
        return created;
    }

    private sealed class Reservation(string key, Guid token) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Gate)
            {
                var registry = Registry();
                if (registry.TryGetValue(key, out var current) && current == token) registry.Remove(key);
            }
        }
    }
}

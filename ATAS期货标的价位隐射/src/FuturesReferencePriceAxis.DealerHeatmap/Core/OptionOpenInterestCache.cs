namespace WolfMoss.ATAS.PriceMapping.Core;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

public readonly record struct OptionOiCacheEntry(long OpenInterest, DateTime ReceivedUtc);

public sealed record OptionOpenInterestCacheSnapshot(DateTime ReceivedUtc, IReadOnlyDictionary<long, long> Values)
{
    public IReadOnlyDictionary<long, OptionOiCacheEntry> Entries { get; init; } = new Dictionary<long, OptionOiCacheEntry>();
}

public static class OptionOpenInterestCache
{
    public const int MaximumFileBytes = 1_048_576;
    public const int MaximumEntries = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string GetDefaultDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WolfMoss", "FuturesReferencePriceAxis", "option-oi");

    public static IReadOnlyDictionary<long, long> Load(string directory, string ticker, DateOnly expiration)
        => LoadSnapshot(directory, ticker, expiration).Values;

    public static OptionOpenInterestCacheSnapshot LoadSnapshot(
        string directory, string ticker, DateOnly expiration, DateTime? nowUtc = null)
    {
        try { return Read(GetPath(directory, ticker, expiration), ticker, expiration, nowUtc ?? DateTime.UtcNow); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or NotSupportedException)
        { return Empty(); }
    }

    private static OptionOpenInterestCacheSnapshot Read(string path, string ticker, DateOnly expiration, DateTime now)
    {
        if (expiration < DateOnly.FromDateTime(UsMarketClock.ToEastern(now))) return Empty();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MaximumFileBytes) return Empty();
        var document = JsonSerializer.Deserialize<CacheDocument>(stream, JsonOptions);
        if (document == null || document.SchemaVersion is < 0 or > 2
            || !string.Equals(document.Ticker, ticker, StringComparison.OrdinalIgnoreCase)
            || document.Expiration != expiration || document.Values == null
            || document.Values.Count > MaximumEntries) return Empty();
        var entries = new Dictionary<long, OptionOiCacheEntry>();
        foreach (var item in document.Values)
        {
            var received = item.ReceivedUtc ?? document.ReceivedUtc;
            if (item.ConId <= 0 || item.OpenInterest < 0 || received.Kind != DateTimeKind.Utc
                || received == DateTime.MinValue || received > now) continue;
            if (!entries.TryGetValue(item.ConId, out var prior) || received >= prior.ReceivedUtc)
                entries[item.ConId] = new(item.OpenInterest, received);
        }
        return new(entries.Count == 0 ? DateTime.MinValue : entries.Values.Max(static v => v.ReceivedUtc),
            entries.ToDictionary(static e => e.Key, static e => e.Value.OpenInterest)) { Entries = entries };
    }

    public static void Save(string directory, string ticker, DateOnly expiration, DateTime receivedUtc,
        IReadOnlyDictionary<long, long> values)
        => SaveEntries(directory, ticker, expiration,
            values.ToDictionary(static e => e.Key, e => new OptionOiCacheEntry(e.Value, receivedUtc)), receivedUtc);

    public static void SaveEntries(string directory, string ticker, DateOnly expiration,
        IReadOnlyDictionary<long, OptionOiCacheEntry> values, DateTime nowUtc)
    {
        var path = GetPath(directory, ticker, expiration);
        Directory.CreateDirectory(directory);
        // Cross-process exclusion. Busy writers fail fast; the background loop retries.
        using var writer = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var merged = new Dictionary<long, OptionOiCacheEntry>(LoadSnapshot(directory, ticker, expiration, nowUtc).Entries);
        foreach (var item in values)
        {
            var entry = item.Value;
            if (item.Key <= 0 || entry.OpenInterest < 0 || entry.ReceivedUtc.Kind != DateTimeKind.Utc
                || entry.ReceivedUtc == DateTime.MinValue || entry.ReceivedUtc > nowUtc) continue;
            if (!merged.TryGetValue(item.Key, out var previous) || entry.ReceivedUtc >= previous.ReceivedUtc)
                merged[item.Key] = entry;
        }
        var retained = merged.OrderByDescending(static e => e.Value.ReceivedUtc)
            .ThenBy(static e => e.Key).Take(MaximumEntries).ToArray();
        var document = new CacheDocument(ticker.ToUpperInvariant(), expiration,
            retained.Length == 0 ? DateTime.MinValue : retained[0].Value.ReceivedUtc,
            retained.Select(static e => new CacheValue(e.Key, e.Value.OpenInterest, e.Value.ReceivedUtc)).ToArray(), 2);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaximumFileBytes) throw new IOException("OI cache size limit");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string GetPath(string directory, string ticker, DateOnly expiration)
    {
        if (!OptionUnderlyingProfile.TryResolve(ticker, out _)) throw new ArgumentException("Unsupported OI ticker", nameof(ticker));
        return Path.Combine(directory, $"{ticker.ToUpperInvariant()}-{expiration:yyyyMMdd}.json");
    }
    private static OptionOpenInterestCacheSnapshot Empty() => new(DateTime.MinValue, new Dictionary<long, long>());
    private sealed record CacheDocument(string Ticker, DateOnly Expiration, DateTime ReceivedUtc,
        IReadOnlyList<CacheValue>? Values, int SchemaVersion = 0);
    private readonly record struct CacheValue(long ConId, long OpenInterest, DateTime? ReceivedUtc = null);
}

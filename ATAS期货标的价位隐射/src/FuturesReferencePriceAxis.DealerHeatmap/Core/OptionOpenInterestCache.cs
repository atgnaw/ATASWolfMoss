namespace WolfMoss.ATAS.PriceMapping.Core;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record OptionOpenInterestCacheSnapshot(
    DateTime ReceivedUtc,
    IReadOnlyDictionary<long, long> Values);

public static class OptionOpenInterestCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string GetDefaultDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WolfMoss",
            "FuturesReferencePriceAxis",
            "option-oi");

    public static IReadOnlyDictionary<long, long> Load(
        string directory,
        string ticker,
        DateOnly expiration)
        => LoadSnapshot(directory, ticker, expiration).Values;

    public static OptionOpenInterestCacheSnapshot LoadSnapshot(
        string directory,
        string ticker,
        DateOnly expiration)
    {
        try
        {
            var path = GetPath(directory, ticker, expiration);

            if (!File.Exists(path))
                return EmptySnapshot();

            var document = JsonSerializer.Deserialize<CacheDocument>(
                File.ReadAllBytes(path), JsonOptions);

            if (document == null
                || !string.Equals(document.Ticker, ticker,
                    StringComparison.OrdinalIgnoreCase)
                || document.Expiration != expiration)
            {
                return EmptySnapshot();
            }

            var values = document.Values
                .Where(static item => item.ConId > 0 && item.OpenInterest >= 0)
                .GroupBy(static item => item.ConId)
                .ToDictionary(static group => group.Key,
                    static group => group.Last().OpenInterest);
            var receivedUtc = document.ReceivedUtc.Kind == DateTimeKind.Utc
                ? document.ReceivedUtc
                : document.ReceivedUtc.ToUniversalTime();
            return new OptionOpenInterestCacheSnapshot(receivedUtc, values);
        }
        catch
        {
            return EmptySnapshot();
        }
    }

    public static void Save(
        string directory,
        string ticker,
        DateOnly expiration,
        DateTime receivedUtc,
        IReadOnlyDictionary<long, long> values)
    {
        Directory.CreateDirectory(directory);
        var path = GetPath(directory, ticker, expiration);
        var temporaryPath = path + ".tmp";
        var document = new CacheDocument(
            ticker.ToUpperInvariant(),
            expiration,
            receivedUtc,
            values.Select(static item => new CacheValue(item.Key, item.Value)).ToArray());
        File.WriteAllBytes(temporaryPath,
            JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
        File.Move(temporaryPath, path, true);
    }

    private static string GetPath(string directory, string ticker, DateOnly expiration)
        => Path.Combine(directory,
            $"{ticker.ToUpperInvariant()}-{expiration:yyyyMMdd}.json");

    private static OptionOpenInterestCacheSnapshot EmptySnapshot()
        => new(DateTime.MinValue,
            new Dictionary<long, long>());

    private sealed record CacheDocument(
        string Ticker,
        DateOnly Expiration,
        DateTime ReceivedUtc,
        IReadOnlyList<CacheValue> Values);

    private readonly record struct CacheValue(long ConId, long OpenInterest);
}

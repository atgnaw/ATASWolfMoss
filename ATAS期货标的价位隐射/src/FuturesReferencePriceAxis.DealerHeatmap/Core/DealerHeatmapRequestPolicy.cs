namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

internal static class NightwatchCredentialFingerprint
{
    public static string Create(string apiKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }
}

internal readonly record struct DealerHeatmapRequestIdentity(
    string CredentialFingerprint,
    string Ticker,
    DateOnly Expiration,
    DateTime SampleBucketUtc)
{
    public static DealerHeatmapRequestIdentity Create(
        string apiKey,
        string ticker,
        DateOnly expiration,
        DateTime utcNow)
    {
        return new DealerHeatmapRequestIdentity(
            NightwatchCredentialFingerprint.Create(apiKey),
            ticker.ToUpperInvariant(),
            expiration,
            DealerHeatmapSchedule.GetSampleBucketUtc(utcNow));
    }
}

internal readonly record struct DealerGexRequestIdentity(
    string CredentialFingerprint,
    string Ticker,
    DateTime SampleBucketUtc)
{
    public static DealerGexRequestIdentity Create(
        string apiKey,
        string ticker,
        DateTime utcNow)
        => new(
            NightwatchCredentialFingerprint.Create(apiKey),
            ticker.ToUpperInvariant(),
            DealerHeatmapSchedule.GetSampleBucketUtc(utcNow));
}

internal static class NightwatchRetryGate
{
    private static readonly ConcurrentDictionary<string, DateTime> RetryNotBefore = new();

    public static DateTime? GetRetryNotBeforeUtc(string apiKey, DateTime utcNow)
    {
        var fingerprint = NightwatchCredentialFingerprint.Create(apiKey);

        if (!RetryNotBefore.TryGetValue(fingerprint, out var retryAt))
            return null;

        if (utcNow < retryAt)
            return retryAt;

        RetryNotBefore.TryRemove(
            new KeyValuePair<string, DateTime>(fingerprint, retryAt));
        return null;
    }

    public static void Register(string credentialFingerprint, DateTime retryAt)
        => RetryNotBefore.AddOrUpdate(
            credentialFingerprint,
            retryAt,
            (_, existing) => existing > retryAt ? existing : retryAt);
}

public static class DealerHeatmapRetryPolicy
{
    public static DateTime ResolveRetryAfterUtc(
        DateTime utcNow,
        TimeSpan? delta,
        DateTimeOffset? date)
    {
        if (delta.HasValue)
            return utcNow + delta.Value;

        if (date.HasValue)
            return date.Value.UtcDateTime;

        return utcNow + TimeSpan.FromMinutes(1);
    }
}

namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Security.Cryptography;
using System.Text;

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
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return new DealerHeatmapRequestIdentity(
            Convert.ToHexString(hash.AsSpan(0, 16)),
            ticker.ToUpperInvariant(),
            expiration,
            DealerHeatmapSchedule.GetSampleBucketUtc(utcNow));
    }
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

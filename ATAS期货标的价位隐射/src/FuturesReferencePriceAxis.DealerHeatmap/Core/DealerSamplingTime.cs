namespace WolfMoss.ATAS.PriceMapping.Core;

public static class DealerSamplingTime
{
    public static readonly TimeSpan SampleOffset = TimeSpan.FromMinutes(4);

    public static DateTime FromBucketStartUtc(DateTime bucketStartUtc)
    {
        var normalized = bucketStartUtc.Kind == DateTimeKind.Utc
            ? bucketStartUtc
            : DateTime.SpecifyKind(bucketStartUtc, DateTimeKind.Utc);
        return normalized + SampleOffset;
    }
}

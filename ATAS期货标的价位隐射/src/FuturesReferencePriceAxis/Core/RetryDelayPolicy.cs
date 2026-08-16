namespace WolfMoss.ATAS.PriceMapping.Core;

public static class RetryDelayPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    ];

    public static TimeSpan ForConsecutiveFailure(int failureIndex)
        => Delays[Math.Clamp(failureIndex, 0, Delays.Length - 1)];
}

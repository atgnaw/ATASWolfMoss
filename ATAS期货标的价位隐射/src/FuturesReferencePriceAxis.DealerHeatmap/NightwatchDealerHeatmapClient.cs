namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

internal static class NightwatchDealerHeatmapClient
{
    private const int MaximumResponseBytes = 524_288;
    private static readonly ReferenceQuoteCoordinator<
        DealerHeatmapRequestIdentity,
        DealerHeatmapFrame> Coordinator = new(
        TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(5));

    public static Task<DealerHeatmapFrame> GetLatestAsync(
        string apiKey,
        string ticker,
        DateOnly expiration,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var identity = DealerHeatmapRequestIdentity.Create(
            apiKey,
            ticker,
            expiration,
            utcNow);
        var retryAt = NightwatchRetryGate.GetRetryNotBeforeUtc(apiKey, utcNow);

        if (retryAt.HasValue)
        {
            throw new DealerHeatmapDataException(
                "HTTP429",
                "Nightwatch 限流，等待 Retry-After",
                retryAt.Value);
        }

        return Coordinator.GetAsync(
            identity,
            token => DownloadAsync(
                apiKey,
                ticker,
                expiration,
                identity.CredentialFingerprint,
                token),
            cancellationToken);
    }

    private static async Task<DealerHeatmapFrame> DownloadAsync(
        string apiKey,
        string ticker,
        DateOnly expiration,
        string credentialFingerprint,
        CancellationToken cancellationToken)
    {
        var symbol = ticker.ToUpperInvariant();
        var url = "https://api.yehangshe.com/v1/derived/heatmap/"
                  + Uri.EscapeDataString(symbol)
                  + "/snapshot";
        var json = await NightwatchHttpTransport.GetJsonAsync(
                apiKey,
                url,
                credentialFingerprint,
                MaximumResponseBytes,
                "Nightwatch 响应过大",
                "Nightwatch 请求超时",
                "Nightwatch 网络错误",
                static (code, message, retryAfterUtc, innerException) =>
                    new DealerHeatmapDataException(
                        code,
                        message,
                        retryAfterUtc,
                        innerException),
                cancellationToken)
            .ConfigureAwait(false);
        return NightwatchEventCapture.Heatmap(DealerHeatmapParser.ParseSnapshot(json.Span, symbol, expiration), DateTime.UtcNow);
    }
}

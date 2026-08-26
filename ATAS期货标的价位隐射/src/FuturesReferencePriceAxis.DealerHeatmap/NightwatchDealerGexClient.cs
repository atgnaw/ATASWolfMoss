namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

internal static class NightwatchDealerGexClient
{
    private const int MaximumResponseBytes = 262_144;
    private static readonly ReferenceQuoteCoordinator<
        DealerGexRequestIdentity,
        DealerGexFrame> Coordinator = new(
        TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(5));

    public static Task<DealerGexFrame> GetLatestAsync(
        string apiKey,
        string ticker,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var identity = DealerGexRequestIdentity.Create(
            apiKey,
            ticker,
            utcNow);
        var retryAt = NightwatchRetryGate.GetRetryNotBeforeUtc(apiKey, utcNow);

        if (retryAt.HasValue)
        {
            throw new DealerGexDataException(
                "HTTP429",
                "Nightwatch 限流，等待 Retry-After",
                retryAt.Value);
        }

        return Coordinator.GetAsync(
            identity,
            token => DownloadAsync(
                apiKey,
                ticker,
                identity.CredentialFingerprint,
                token),
            cancellationToken);
    }

    private static async Task<DealerGexFrame> DownloadAsync(
        string apiKey,
        string ticker,
        string credentialFingerprint,
        CancellationToken cancellationToken)
    {
        var symbol = ticker.ToUpperInvariant();
        var url = "https://api.yehangshe.com/v1/derived/dealer-gex/"
                  + Uri.EscapeDataString(symbol)
                  + "/snapshot";
        var json = await NightwatchHttpTransport.GetJsonAsync(
                apiKey,
                url,
                credentialFingerprint,
                MaximumResponseBytes,
                "Nightwatch Dealer GEX 响应过大",
                "Nightwatch Dealer GEX 请求超时",
                "Nightwatch Dealer GEX 网络错误",
                static (code, message, retryAfterUtc, innerException) =>
                    new DealerGexDataException(
                        code,
                        message,
                        retryAfterUtc,
                        innerException),
                cancellationToken)
            .ConfigureAwait(false);
        return DealerGexParser.ParseSnapshot(json.Span, symbol);
    }
}

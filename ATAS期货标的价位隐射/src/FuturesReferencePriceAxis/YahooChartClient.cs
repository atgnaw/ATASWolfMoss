namespace WolfMoss.ATAS.PriceMapping;

using global::System.Net.Http;

using WolfMoss.ATAS.PriceMapping.Core;

internal static class YahooChartClient
{
    private static readonly string[] Hosts =
    [
        "https://query1.finance.yahoo.com",
        "https://query2.finance.yahoo.com"
    ];

    private static readonly HttpClient Client = BoundedHttpDownloader.CreateJsonClient();
    private static readonly ReferenceQuoteCoordinator<
        ReferenceQuoteRequestKey,
        ReferenceMinuteClose> Coordinator = new(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(5));

    public static async Task<ReferenceMinuteClose> GetLatestCompletedAsync(
        string yahooSymbol,
        DateTime nowUtc,
        TimeSpan maximumAge,
        DateTime? lastAcceptedMinuteUtc,
        CancellationToken cancellationToken)
    {
        var isQqq = string.Equals(yahooSymbol, "QQQ", StringComparison.OrdinalIgnoreCase);
        var allowExpired = !isQqq
                           || UsEquitySessionCalendar.IsQqqExtendedSessionClosed(nowUtc)
                           || !NqTradingSessionCalendar.IsOpen(nowUtc);
        var primaryPolicy = isQqq
            ? ReferenceMinuteSelectionPolicy.QqqNqCommonMinuteYahoo
            : ReferenceMinuteSelectionPolicy.SpxCashMinuteYahoo;

        try
        {
            var quote = await Coordinator.GetAsync(
                    new ReferenceQuoteRequestKey(yahooSymbol, primaryPolicy),
                    token => GetLatestFromYahooAsync(yahooSymbol, nowUtc, token),
                    cancellationToken)
                .ConfigureAwait(false);
            return ReferenceQuoteValidator.Validate(
                quote,
                nowUtc,
                maximumAge,
                lastAcceptedMinuteUtc,
                allowExpired);
        }
        catch (ReferenceDataException primaryError)
        {
            if (isQqq)
            {
                try
                {
                    var fallback = await Coordinator.GetAsync(
                            new ReferenceQuoteRequestKey(
                                yahooSymbol,
                                ReferenceMinuteSelectionPolicy.QqqNqCommonMinuteNasdaq),
                            token => NasdaqQqqClient.GetLatestCandidateAsync(nowUtc, token),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return ReferenceQuoteValidator.Validate(
                        fallback,
                        nowUtc,
                        maximumAge,
                        lastAcceptedMinuteUtc,
                        allowExpired);
                }
                catch (ReferenceDataException fallbackError)
                {
                    throw new ReferenceSourcesException(
                        "Yahoo",
                        primaryError,
                        "Nasdaq",
                        fallbackError);
                }
            }

            if (string.Equals(yahooSymbol, "^GSPC", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var fallback = await Coordinator.GetAsync(
                            new ReferenceQuoteRequestKey(
                                yahooSymbol,
                                ReferenceMinuteSelectionPolicy.SpxCashMinuteMarketWatch),
                            token => MarketWatchSpxClient.GetLatestCandidateAsync(nowUtc, token),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return ReferenceQuoteValidator.Validate(
                        fallback,
                        nowUtc,
                        maximumAge,
                        lastAcceptedMinuteUtc,
                        allowExpired);
                }
                catch (ReferenceDataException fallbackError)
                {
                    throw new ReferenceSourcesException(
                        "Yahoo",
                        primaryError,
                        "MarketWatch",
                        fallbackError);
                }
            }

            throw;
        }
    }

    private static async Task<ReferenceMinuteClose> GetLatestFromYahooAsync(
        string yahooSymbol,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ReferenceDataException? lastError = null;

        foreach (var host in Hosts)
        {
            try
            {
                var encodedSymbol = Uri.EscapeDataString(yahooSymbol);
                var isCashIndex = string.Equals(
                    yahooSymbol,
                    "^GSPC",
                    StringComparison.OrdinalIgnoreCase);
                var isQqq = string.Equals(
                    yahooSymbol,
                    "QQQ",
                    StringComparison.OrdinalIgnoreCase);
                var range = isCashIndex ? "5d" : "1d";
                var includePrePost = isCashIndex ? "false" : "true";
                var url =
                    $"{host}/v8/finance/chart/{encodedSymbol}"
                    + $"?range={range}&interval=1m"
                    + $"&includePrePost={includePrePost}&events=div%2Csplits";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                var json = await BoundedHttpDownloader.DownloadAsync(
                        Client,
                        request,
                        "Yahoo",
                        static (code, message, inner) =>
                            new YahooDataException(code, message, inner),
                        cancellationToken)
                    .ConfigureAwait(false);

                return YahooChartParser.ParseLatestCompleted(
                    json,
                    yahooSymbol,
                    nowUtc,
                    TimeSpan.MaxValue,
                    allowExpiredQuote: true,
                    requireNqTradableMinute: isQqq);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ReferenceDataException exception)
            {
                lastError = exception;
            }
        }

        throw lastError
              ?? new YahooDataException("NetworkError", "Yahoo 请求失败");
    }
}

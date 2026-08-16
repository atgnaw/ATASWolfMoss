namespace WolfMoss.ATAS.PriceMapping;

using global::System.Net.Http;
using global::System.Text.Json;

using WolfMoss.ATAS.PriceMapping.Core;

internal static class MarketWatchSpxClient
{
    private const string EntitlementToken = "cecc4267a0194af89ca343805a3e57af";

    private static readonly string[] Hosts =
    [
        "https://api-secure.wsj.net",
        "https://api.wsj.net"
    ];

    private static readonly HttpClient Client = CreateClient();

    public static async Task<ReferenceMinuteClose> GetLatestCandidateAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ReferenceDataException? lastError = null;

        foreach (var host in Hosts)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(host));
                var json = await BoundedHttpDownloader.DownloadAsync(
                        Client,
                        request,
                        "MarketWatch",
                        static (code, message, inner) =>
                            new MarketWatchDataException(code, message, inner),
                        cancellationToken)
                    .ConfigureAwait(false);
                return MarketWatchSpxParser.ParseLatestCompleted(
                    json,
                    nowUtc,
                    TimeSpan.MaxValue,
                    allowExpiredQuote: true);
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
              ?? new MarketWatchDataException(
                  "NetworkError",
                  "SPX MarketWatch备用源网络错误");
    }

    internal static string BuildUrl(string host)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Step = "PT1M",
            TimeFrame = "D5",
            EntitlementToken,
            IncludeMockTick = true,
            FilterNullSlots = false,
            FilterClosedPoints = true,
            IncludeClosedSlots = false,
            IncludeOfficialClose = true,
            InjectOpen = false,
            ShowPreMarket = false,
            ShowAfterHours = false,
            UseExtendedTimeFrame = false,
            WantPriorClose = true,
            IncludeCurrentQuotes = false,
            ResetTodaysAfterHoursPercentChange = false,
            Series = new[]
            {
                new
                {
                    Key = "INDEX/US/S&P US/SPX",
                    Dialect = "Charting",
                    Kind = "Ticker",
                    SeriesId = "s1",
                    DataTypes = new[] { "Last" }
                }
            }
        });
        var encoded = Uri.EscapeDataString(payload);
        return $"{host}/api/michelangelo/timeseries/history"
               + $"?json={encoded}&ckey={EntitlementToken[..10]}";
    }

    private static HttpClient CreateClient()
    {
        var client = BoundedHttpDownloader.CreateJsonClient(
            "application/json, text/javascript, */*; q=0.01");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Dylan2010.EntitlementToken",
            EntitlementToken);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Origin",
            "https://www.marketwatch.com");
        client.DefaultRequestHeaders.Referrer = new Uri(
            "https://www.marketwatch.com/investing/index/spx/charts");
        return client;
    }
}

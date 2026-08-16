namespace WolfMoss.ATAS.PriceMapping;

using global::System.Net.Http;

using WolfMoss.ATAS.PriceMapping.Core;

internal static class NasdaqQqqClient
{
    private const string Url =
        "https://api.nasdaq.com/api/quote/QQQ/chart?assetclass=etf";

    private static readonly HttpClient Client = BoundedHttpDownloader.CreateJsonClient();

    public static async Task<ReferenceMinuteClose> GetLatestCandidateAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        var json = await BoundedHttpDownloader.DownloadAsync(
                Client,
                request,
                "Nasdaq",
                static (code, message, inner) =>
                    new NasdaqDataException(code, message, inner),
                cancellationToken)
            .ConfigureAwait(false);
        return NasdaqQqqParser.ParseLatestCompleted(
            json,
            nowUtc,
            TimeSpan.MaxValue,
            allowExpiredQuote: true,
            requireNqTradableMinute: true);
    }
}

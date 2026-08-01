namespace WolfMoss.ATAS.PriceMapping;

using global::System.Buffers;
using global::System.IO;
using global::System.Net;
using global::System.Net.Http;

using WolfMoss.ATAS.PriceMapping.Core;

internal static class YahooChartClient
{
    private const int MaximumResponseBytes = 1_048_576;

    private static readonly string[] Hosts =
    [
        "https://query1.finance.yahoo.com",
        "https://query2.finance.yahoo.com"
    ];

    private static readonly HttpClient Client = CreateClient();

    public static async Task<ReferenceMinuteClose> GetLatestCompletedAsync(
        string yahooSymbol,
        DateTime nowUtc,
        TimeSpan maximumAge,
        DateTime? lastAcceptedMinuteUtc,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        foreach (var host in Hosts)
        {
            try
            {
                var encodedSymbol = Uri.EscapeDataString(yahooSymbol);
                var isCashIndex = string.Equals(
                    yahooSymbol,
                    "^GSPC",
                    StringComparison.OrdinalIgnoreCase);
                var range = isCashIndex ? "5d" : "1d";
                var includePrePost = isCashIndex ? "false" : "true";
                var url =
                    $"{host}/v8/finance/chart/{encodedSymbol}"
                    + $"?range={range}&interval=1m"
                    + $"&includePrePost={includePrePost}&events=div%2Csplits";
                var json = await DownloadBoundedAsync(url, cancellationToken).ConfigureAwait(false);

                return YahooChartParser.ParseLatestCompleted(
                    json,
                    yahooSymbol,
                    nowUtc,
                    maximumAge,
                    lastAcceptedMinuteUtc,
                    allowExpiredQuote: isCashIndex);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException
                                       or TaskCanceledException
                                       or YahooDataException)
            {
                lastError = ex;
            }
        }

        if (lastError is YahooDataException yahooError)
            throw yahooError;

        if (lastError is TaskCanceledException)
            throw new YahooDataException("Timeout", "Yahoo 请求超时");

        throw new YahooDataException(
            "NetworkError",
            lastError?.Message ?? "Yahoo 请求失败");
    }

    private static async Task<ReadOnlyMemory<byte>> DownloadBoundedAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new YahooDataException("HTTP429", "Yahoo 429 限流");

        if (!response.IsSuccessStatusCode)
        {
            throw new YahooDataException(
                $"HTTP{(int)response.StatusCode}",
                $"Yahoo HTTP {(int)response.StatusCode}");
        }

        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new YahooDataException("ResponseTooLarge", "Yahoo 响应过大");

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            while (true)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                    break;

                if (output.Length + read > MaximumResponseBytes)
                    throw new YahooDataException("ResponseTooLarge", "Yahoo 响应过大");

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 ATAS-FuturesReferencePriceAxis/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}

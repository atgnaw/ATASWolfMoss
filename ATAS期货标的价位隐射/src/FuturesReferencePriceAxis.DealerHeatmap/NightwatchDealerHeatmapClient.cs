namespace WolfMoss.ATAS.PriceMapping;

using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

using WolfMoss.ATAS.PriceMapping.Core;

internal static class NightwatchDealerHeatmapClient
{
    private const int MaximumResponseBytes = 524_288;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient Client = CreateClient();
    private static readonly ReferenceQuoteCoordinator<
        DealerHeatmapRequestIdentity,
        DealerHeatmapFrame> Coordinator = new(
        TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(5));
    private static readonly ConcurrentDictionary<string, DateTime> RetryNotBefore = new();

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
        var fingerprint = identity.CredentialFingerprint;

        if (RetryNotBefore.TryGetValue(fingerprint, out var retryAt)
            && utcNow < retryAt)
        {
            throw new DealerHeatmapDataException(
                "HTTP429",
                "Nightwatch 限流，等待 Retry-After",
                retryAt);
        }

        return Coordinator.GetAsync(
            identity,
            token => DownloadAsync(apiKey, ticker, expiration, fingerprint, token),
            cancellationToken);
    }

    private static async Task<DealerHeatmapFrame> DownloadAsync(
        string apiKey,
        string ticker,
        DateOnly expiration,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var symbol = ticker.ToUpperInvariant();
        var url = "https://api.yehangshe.com/v1/derived/heatmap/"
                  + Uri.EscapeDataString(symbol)
                  + "/snapshot";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using var response = await Client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAt = DealerHeatmapRetryPolicy.ResolveRetryAfterUtc(
                    DateTime.UtcNow,
                    response.Headers.RetryAfter?.Delta,
                    response.Headers.RetryAfter?.Date);
                RetryNotBefore.AddOrUpdate(
                    fingerprint,
                    retryAt,
                    (_, existing) => existing > retryAt ? existing : retryAt);
                throw new DealerHeatmapDataException(
                    "HTTP429",
                    "Nightwatch 429 限流",
                    retryAt);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new DealerHeatmapDataException(
                    $"HTTP{(int)response.StatusCode}",
                    "Nightwatch API key 鉴权失败");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DealerHeatmapDataException(
                    $"HTTP{(int)response.StatusCode}",
                    $"Nightwatch HTTP {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                throw new DealerHeatmapDataException(
                    "ResponseTooLarge",
                    "Nightwatch 响应过大");
            }

            var json = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
            RetryNotBefore.TryRemove(fingerprint, out _);
            return DealerHeatmapParser.ParseSnapshot(json.Span, symbol, expiration);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DealerHeatmapDataException)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new DealerHeatmapDataException(
                "Timeout",
                "Nightwatch 请求超时",
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new DealerHeatmapDataException(
                "NetworkError",
                "Nightwatch 网络错误",
                innerException: exception);
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
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
                {
                    throw new DealerHeatmapDataException(
                        "ResponseTooLarge",
                        "Nightwatch 响应过大");
                }

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
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ATAS-FuturesReferencePriceAxis-Pro/2.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

}

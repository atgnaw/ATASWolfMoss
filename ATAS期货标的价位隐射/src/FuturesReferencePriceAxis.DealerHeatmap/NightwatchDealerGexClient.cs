namespace WolfMoss.ATAS.PriceMapping;

using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

using WolfMoss.ATAS.PriceMapping.Core;

internal static class NightwatchDealerGexClient
{
    private const int MaximumResponseBytes = 262_144;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient Client = CreateClient();
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
                NightwatchRetryGate.Register(credentialFingerprint, retryAt);
                throw new DealerGexDataException(
                    "HTTP429",
                    "Nightwatch 429 限流",
                    retryAt);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new DealerGexDataException(
                    $"HTTP{(int)response.StatusCode}",
                    "Nightwatch API key 鉴权失败");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DealerGexDataException(
                    $"HTTP{(int)response.StatusCode}",
                    $"Nightwatch HTTP {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                throw new DealerGexDataException(
                    "ResponseTooLarge",
                    "Nightwatch Dealer GEX 响应过大");
            }

            var json = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
            return DealerGexParser.ParseSnapshot(json.Span, symbol);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DealerGexDataException)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new DealerGexDataException(
                "Timeout",
                "Nightwatch Dealer GEX 请求超时",
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new DealerGexDataException(
                "NetworkError",
                "Nightwatch Dealer GEX 网络错误",
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
                    throw new DealerGexDataException(
                        "ResponseTooLarge",
                        "Nightwatch Dealer GEX 响应过大");
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
            "ATAS-FuturesReferencePriceAxis-Pro/2.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}

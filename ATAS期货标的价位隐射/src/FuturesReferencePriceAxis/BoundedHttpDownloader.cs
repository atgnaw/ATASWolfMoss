namespace WolfMoss.ATAS.PriceMapping;

using global::System.Buffers;
using global::System.IO;
using global::System.Net;
using global::System.Net.Http;

using WolfMoss.ATAS.PriceMapping.Core;

internal static class BoundedHttpDownloader
{
    internal const int MaximumResponseBytes = 1_048_576;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public static async Task<ReadOnlyMemory<byte>> DownloadAsync(
        HttpClient client,
        HttpRequestMessage request,
        string providerName,
        Func<string, string, Exception?, ReferenceDataException> errorFactory,
        CancellationToken cancellationToken)
    {
        using (request)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(RequestTimeout);

            try
            {
                using var response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw errorFactory("HTTP429", $"{providerName} 429 限流", null);

                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = (int)response.StatusCode;
                    throw errorFactory(
                        $"HTTP{statusCode}",
                        $"{providerName} HTTP {statusCode}",
                        null);
                }

                if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                {
                    throw errorFactory(
                        "ResponseTooLarge",
                        $"{providerName} 响应过大",
                        null);
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(timeout.Token)
                    .ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

                try
                {
                    while (true)
                    {
                        var read = await stream
                            .ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token)
                            .ConfigureAwait(false);

                        if (read == 0)
                            break;

                        if (output.Length + read > MaximumResponseBytes)
                        {
                            throw errorFactory(
                                "ResponseTooLarge",
                                $"{providerName} 响应过大",
                                null);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ReferenceDataException)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                throw errorFactory(
                    "Timeout",
                    $"{providerName} 请求超时",
                    exception);
            }
            catch (HttpRequestException exception)
            {
                throw errorFactory(
                    "NetworkError",
                    $"{providerName} 网络错误",
                    exception);
            }
        }
    }

    public static HttpClient CreateJsonClient(string accept = "application/json")
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 ATAS-FuturesReferencePriceAxis/1.1");
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        return client;
    }
}

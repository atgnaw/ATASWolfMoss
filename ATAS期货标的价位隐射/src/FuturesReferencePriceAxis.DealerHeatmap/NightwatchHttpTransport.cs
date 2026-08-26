namespace WolfMoss.ATAS.PriceMapping;

using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

using WolfMoss.ATAS.PriceMapping.Core;

internal delegate Exception NightwatchDataExceptionFactory(
    string code,
    string message,
    DateTime? retryAfterUtc,
    Exception? innerException);

internal static class NightwatchHttpTransport
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient Client = CreateClient();

    public static async Task<ReadOnlyMemory<byte>> GetJsonAsync(
        string apiKey,
        string url,
        string credentialFingerprint,
        int maximumResponseBytes,
        string responseTooLargeMessage,
        string timeoutMessage,
        string networkErrorMessage,
        NightwatchDataExceptionFactory createException,
        CancellationToken cancellationToken)
    {
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

            ValidateResponse(
                response,
                credentialFingerprint,
                maximumResponseBytes,
                responseTooLargeMessage,
                createException);

            return await ReadBoundedAsync(
                    response,
                    maximumResponseBytes,
                    responseTooLargeMessage,
                    createException,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw createException("Timeout", timeoutMessage, null, exception);
        }
        catch (HttpRequestException exception)
        {
            throw createException("NetworkError", networkErrorMessage, null, exception);
        }
    }

    private static void ValidateResponse(
        HttpResponseMessage response,
        string credentialFingerprint,
        int maximumResponseBytes,
        string responseTooLargeMessage,
        NightwatchDataExceptionFactory createException)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAt = DealerHeatmapRetryPolicy.ResolveRetryAfterUtc(
                DateTime.UtcNow,
                response.Headers.RetryAfter?.Delta,
                response.Headers.RetryAfter?.Date);
            NightwatchRetryGate.Register(credentialFingerprint, retryAt);
            throw createException(
                "HTTP429",
                "Nightwatch 429 限流",
                retryAt,
                null);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw createException(
                $"HTTP{(int)response.StatusCode}",
                "Nightwatch API key 鉴权失败",
                null,
                null);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw createException(
                $"HTTP{(int)response.StatusCode}",
                $"Nightwatch HTTP {(int)response.StatusCode}",
                null,
                null);
        }

        if (response.Content.Headers.ContentLength > maximumResponseBytes)
        {
            throw createException(
                "ResponseTooLarge",
                responseTooLargeMessage,
                null,
                null);
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumResponseBytes,
        string responseTooLargeMessage,
        NightwatchDataExceptionFactory createException,
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

                if (output.Length + read > maximumResponseBytes)
                {
                    throw createException(
                        "ResponseTooLarge",
                        responseTooLargeMessage,
                        null,
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

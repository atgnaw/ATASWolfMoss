using System.Diagnostics;
using System.Text.Json;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe;

internal sealed record ProbeConnection(IIbOptionGatewayClient Client,
    Action<Action<IbRawMarketEvent>?> SetObserver, Func<long> ObservationFailures, Func<bool> Faulted)
{
    public static ProbeConnection Create(ProbeConfig config)
    {
        var client = new ReflectionIbOptionGatewayClient(new(config.Host, config.Port, config.ClientId));
        return new(client, client.SetRawObserver, () => client.RawObservationFailures, () => client.ConnectionFaulted);
    }
}

internal static class CaptureRunner
{
    public static async Task<(string Directory, CaptureSummary Summary)> RunAsync(ProbeConfig config,
        CancellationToken token, Func<ProbeConnection>? connectionFactory = null, Func<DateTime>? clock = null,
        TimeSpan? captureDuration = null)
    {
        config.Validate();
        clock ??= () => DateTime.UtcNow;
        // Reject known QQQ closed hours before opening a socket or creating a run.
        foreach (var ticker in config.ReferenceSpots.Keys)
        { OptionUnderlyingProfile.TryResolve(ticker, out var p); ProbeContracts.Expiration(p, clock()); }
        var run = Guid.NewGuid();
        var created = clock();
        var directory = Path.Combine(Path.GetFullPath(config.OutputDirectory), $"{created:yyyyMMdd-HHmmss}-{run:N}");
        Directory.CreateDirectory(directory);
        var manifest = new ProbeManifest(1, run, "0.5.3", created, Stopwatch.Frequency, config, []);
        await SaveNewAsync(Path.Combine(directory, "manifest.json"), manifest);
        await using var writer = new RawCaptureWriter(directory, run);
        ProbeConnection? connection = null;
        IIbOptionSubscriptionLease? lease = null;
        DateTime? captureStart = null;
        var reason = "COMPLETED";
        long observationFailures = 0;
        long partialPermissionWarnings = 0;
        var fatal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stopMonitor = MonitorStopAsync(writer.StopRequested.Task, fatal.Task, abort);
        try
        {
            connection = connectionFactory?.Invoke() ?? ProbeConnection.Create(config);
            connection.SetObserver(raw =>
            {
                writer.Publish(raw);
                if (raw.ErrorCode == 10090) Interlocked.Increment(ref partialPermissionWarnings);
                var error = FatalError(raw);
                if (error != null) fatal.TrySetResult(error);
            });
            // A single connection attempt. Never automatically reconnect or increment Client ID.
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(abort.Token);
            setup.CancelAfter(TimeSpan.FromMinutes(3));
            await connection.Client.ConnectAsync(setup.Token);
            var contracts = new List<ProbeContract>();
            foreach (var pair in config.ReferenceSpots)
            {
                OptionUnderlyingProfile.TryResolve(pair.Key, out var profile);
                contracts.AddRange(await ProbeContracts.DiscoverAsync(connection.Client, profile,
                    pair.Value, clock(), setup.Token, config.StrikesEachSide));
            }
            if (contracts.Count != config.RequiredMarketDataLines || contracts.Select(x => x.Contract.ConId).Distinct().Count() != contracts.Count)
                throw new IbOptionGatewayException("INVALID_CONTRACTS", "合约数量或身份无效。");
            await SaveNewAsync(Path.Combine(directory, "contracts.json"), contracts);
            abort.Token.ThrowIfCancellationRequested();
            if (contracts.Any(c => !c.Segment.Contains(clock())))
                throw new IbOptionGatewayException("CLOSED", "合约验证期间交易区段已结束。");
            lease = await connection.Client.SubscribeAsync(contracts.Select(x => x.Contract).ToArray(),
                new(false, true, true), update =>
                {
                    if (update.ErrorCode is "NO_PERMISSION" or "LINE_LIMIT" or "CONNECTION_CLOSED" or "PACING")
                        fatal.TrySetResult(update.ErrorCode);
                }, config.RequiredMarketDataLines, setup.Token);
            captureStart = clock();
            await SaveNewAsync(Path.Combine(directory, "capture-start.json"), new { StartedUtc = captureStart });
            var elapsed = Stopwatch.StartNew();
            using var process = Process.GetCurrentProcess();
            var previousCpu = process.TotalProcessorTime;
            var previousElapsed = elapsed.Elapsed;
            long lastWritten = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            Console.WriteLine($"采集已开始：{contracts.Count}/{config.RequiredMarketDataLines} 行；ATM 上下各 {config.StrikesEachSide} 档；文件：{directory}");
            while (elapsed.Elapsed < (captureDuration ?? TimeSpan.FromMinutes(config.DurationMinutes)))
            {
                var next = timer.WaitForNextTickAsync(abort.Token).AsTask();
                var completed = await Task.WhenAny(next, writer.StopRequested.Task, fatal.Task);
                if (completed == writer.StopRequested.Task) { reason = await writer.StopRequested.Task; break; }
                if (completed == fatal.Task) { reason = await fatal.Task; break; }
                if (!await next) break;
                if (connection.Faulted() || !connection.Client.IsConnected) { reason = "CONNECTION_CLOSED"; break; }
                if (contracts.Any(c => !c.Segment.Contains(clock()))) { reason = "SESSION_ENDED"; break; }
                process.Refresh();
                var time = elapsed.Elapsed;
                var cpu = process.TotalProcessorTime;
                var pct = (cpu - previousCpu).TotalMilliseconds / Math.Max(1, (time - previousElapsed).TotalMilliseconds)
                    / Environment.ProcessorCount * 100;
                var written = writer.Written;
                Console.WriteLine($"{time:mm\\:ss} 事件/s {(written - lastWritten) / Math.Max(.001, (time - previousElapsed).TotalSeconds):F0} "
                    + $"队列峰值 {writer.QueuePeak} 丢弃 {writer.Dropped} 写入 {writer.Bytes / 1048576d:F1} MiB "
                    + $"CPU {pct:F1}% 内存 {process.WorkingSet64 / 1048576d:F1} MiB "
                    + $"部分权限警告(10090) {Interlocked.Read(ref partialPermissionWarnings)}");
                previousCpu = cpu; previousElapsed = time; lastWritten = written;
            }
        }
        catch (OperationCanceledException)
        {
            reason = fatal.Task.IsCompletedSuccessfully ? fatal.Task.Result
                : writer.StopRequested.Task.IsCompletedSuccessfully ? writer.StopRequested.Task.Result
                : token.IsCancellationRequested ? "CANCELLED" : "SETUP_TIMEOUT";
        }
        catch (IbOptionGatewayException ex) { reason = ex.Code; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { reason = "IO_FAILURE"; }
        catch { reason = "CAPTURE_FAILURE"; }
        finally
        {
            try { lease?.Dispose(); } catch { reason = "CLEANUP_FAILURE"; }
            if (connection != null)
            {
                try { await connection.Client.DisposeAsync(); } catch { reason = "CLEANUP_FAILURE"; }
                connection.SetObserver(null);
                observationFailures = connection.ObservationFailures();
            }
            await writer.DisposeAsync();
            abort.Cancel();
            await stopMonitor;
        }
        if (writer.Failure != null) reason = writer.Failure;
        else if (writer.StopRequested.Task.IsCompletedSuccessfully && reason == "COMPLETED")
            reason = writer.StopRequested.Task.Result;
        var complete = reason == "COMPLETED" && writer.Failure == null && writer.Dropped == 0
            && writer.Accepted == writer.Written && observationFailures == 0;
        var summary = new CaptureSummary(run, clock(), reason, complete, writer.Accepted,
            writer.Written, writer.Dropped, writer.QueuePeak, writer.Bytes, observationFailures,
            writer.Failure, captureStart);
        await SaveNewAsync(Path.Combine(directory, "capture-summary.json"), summary);
        Console.WriteLine($"采集结束：{reason}；原始文件和采集摘要已保存。");
        if (partialPermissionWarnings > 0)
            Console.WriteLine($"IB 10090 部分权限警告 {partialPermissionWarnings} 次：未中断采集；不代表全部字段可用，请检查报告与原始记录。");
        return (directory, summary);
    }
    internal static string? FatalError(IbRawMarketEvent raw)
        => raw.Kind == "connectionClosed" ? "CONNECTION_CLOSED" : raw.ErrorCode switch
        {
            326 => "CLIENT_ID_IN_USE", 100 => "PACING", 101 => "LINE_LIMIT",
            // 10090 is partial entitlement: keep collecting subscription-independent ticks.
            354 or 10089 or 10186 => "NO_PERMISSION", 1100 or 1300 => "CONNECTION_CLOSED", _ => null
        };
    private static async Task MonitorStopAsync(Task<string> writer, Task<string> fatal, CancellationTokenSource abort)
    {
        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, abort.Token);
        var completed = await Task.WhenAny(writer, fatal, cancelled);
        if (completed != cancelled) abort.Cancel();
    }
    internal static async Task SaveNewAsync<T>(string path, T value)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(file, value, ProbeJson.Options);
    }
}
